using ChatVerse.API.Extensions;
using ChatVerse.API.Services;
using ChatVerse.API.Services.Personas;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Hubs;

// ============================================================
//  PersonaHub — Persona Roulette surface.
//
//  Client methods:
//    • GetMyPersona()                        → today's Persona DTO
//    • GetActiveStreaks()                    → all streaks I'm in
//    • DiscoverPersonas()                    → 10 random today-personas
//    • SendPersonaMessage(toPersonaId, body) → write + push
//    • GetThreadWithPersona(otherPersonaId)  → recent history
//    • RequestMutualUnmask(streakId)         → per-side handshake
//
//  Server-push events:
//    • "PersonaMessageReceived" — fanned to recipient on send
//    • "StreakUnmasked"         — fired to BOTH sides when handshake completes
//
//  Privacy guarantees enforced in this file:
//    • Real user IDs are NEVER serialised back to clients.
//      Inbound calls accept a persona id; server translates to
//      real id via Mongo, performs the action, and only echoes
//      persona-shaped DTOs back.
//    • Mutual-unmask uses a per-side request list — single tap on
//      one side can't reveal anyone's identity.
// ============================================================

[Authorize]
public class PersonaHub : Hub
{
    private readonly MongoService _mongo;
    private readonly ILogger<PersonaHub> _logger;

    private const int MaxMessageChars = 1000;

    public PersonaHub(MongoService mongo, ILogger<PersonaHub> logger)
    {
        _mongo = mongo;
        _logger = logger;
    }

    // ─── Today's persona ────────────────────────────────────────

    public async Task<object> GetMyPersona()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var persona = await _mongo.GetPersonaForDayAsync(meId, today);
        if (persona is null)
        {
            // Self-heal: PersonaResetService might not have generated
            // for this user yet (low-traffic days only run a sample).
            persona = PersonaGenerator.Generate(meId, DateTime.UtcNow);
            await _mongo.UpsertPersonaAsync(persona);
        }
        return ToPersonaDto(persona);
    }

    // ─── Streaks ────────────────────────────────────────────────

    public async Task<object> GetActiveStreaks()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var streaks = await _mongo.GetStreaksForUserAsync(meId);

        var view = new List<object>();
        foreach (var s in streaks)
        {
            var otherRealId = s.RealUserA == meId ? s.RealUserB : s.RealUserA;
            var otherPersona = await _mongo.GetPersonaForDayAsync(otherRealId, today);

            view.Add(new
            {
                streakId         = s.Id,
                otherPersonaId   = otherPersona?.Id,
                consecutiveDays  = s.ConsecutiveDays,
                lastDay          = s.LastDay,
                unmasked         = s.UnmaskedAt != null,
                vaulted          = s.VaultedAt != null,
                otherDisplay     = otherPersona?.DisplayName ?? "Drifted away",
                otherAvatarSeed  = otherPersona?.AvatarSeed,
                otherMood        = otherPersona?.Mood,
                canUnmask        = s.ConsecutiveDays >= 7 && s.UnmaskedAt == null,
                hasMarker        = s.ConsecutiveDays >= 3,
                myUnmaskRequested = s.UnmaskRequestedBy.Contains(meId),
            });
        }
        return new { count = view.Count, streaks = view };
    }

    // ─── Discover ───────────────────────────────────────────────

    public async Task<object> DiscoverPersonas()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var personas = await _mongo.GetRandomTodayPersonasAsync(meId, today, 10);
        return new
        {
            count    = personas.Count,
            personas = personas.Select(ToPersonaDto).ToList(),
        };
    }

    // ─── Messaging ──────────────────────────────────────────────

    public async Task<object> SendPersonaMessage(string toPersonaId, string content)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        if (string.IsNullOrWhiteSpace(content))
            throw new HubException("Message can't be empty.");
        if (content.Length > MaxMessageChars)
            throw new HubException($"Message too long ({MaxMessageChars} chars max).");

        var recipientPersona = await _mongo.GetPersonaByIdAsync(toPersonaId)
            ?? throw new HubException("That persona has drifted away.");

        // Sender's persona for today (self-heal again).
        var senderPersona = await _mongo.GetPersonaForDayAsync(meId, today);
        if (senderPersona is null)
        {
            senderPersona = PersonaGenerator.Generate(meId, DateTime.UtcNow);
            await _mongo.UpsertPersonaAsync(senderPersona);
        }

        // Can't DM yourself.
        if (recipientPersona.UserId == meId)
            throw new HubException("That persona is your own — try someone else.");

        // Recipient persona must be from TODAY — yesterday's personas
        // are read-only history.
        if (recipientPersona.Date != today)
            throw new HubException("That persona expired. Find a fresh one in Discover.");

        var msg = new PersonaMessage
        {
            SenderRealUserId  = meId,
            SenderPersonaId   = senderPersona.Id!,
            SenderDisplayName = senderPersona.DisplayName,
            SenderAvatarSeed  = senderPersona.AvatarSeed,
            Content           = content.Trim(),
        };

        var saved = await _mongo.InsertPersonaMessageAsync(
            msg,
            recipientRealUserId: recipientPersona.UserId,
            dateUtc:             today,
            recipientPersonaId:  recipientPersona.Id!);

        // Push to recipient's connections so their thread updates live.
        await Clients
            .User(recipientPersona.UserId)
            .SendAsync("PersonaMessageReceived", ToPersonaMessageDto(saved));

        return ToPersonaMessageDto(saved);
    }

    public async Task<object> GetThreadWithPersona(string otherPersonaId)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var other = await _mongo.GetPersonaByIdAsync(otherPersonaId)
            ?? throw new HubException("That persona has drifted away.");

        if (other.UserId == meId)
            throw new HubException("Nothing to read — that's you.");

        var messages = await _mongo.GetPersonaThreadAsync(meId, other.UserId);
        return new
        {
            count    = messages.Count,
            messages = messages.Select(ToPersonaMessageDto).ToList(),
        };
    }

    // ─── Mutual unmask handshake ────────────────────────────────

    public async Task<object> RequestMutualUnmask(string streakId)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var streak = await _mongo.RequestStreakUnmaskAsync(streakId, meId);

        if (streak is null)
            throw new HubException("Streak not found.");

        if (streak.ConsecutiveDays < 7)
        {
            return new
            {
                ok = false,
                reason = "Mutual unmask unlocks after 7 consecutive days.",
                consecutiveDays = streak.ConsecutiveDays,
            };
        }

        var bothFlipped = streak.UnmaskedAt != null;
        if (bothFlipped)
        {
            // Fan-out to BOTH parties so their UI flips live.
            await Clients
                .User(streak.RealUserA)
                .SendAsync("StreakUnmasked", new { streakId = streak.Id });
            await Clients
                .User(streak.RealUserB)
                .SendAsync("StreakUnmasked", new { streakId = streak.Id });
        }

        return new
        {
            ok = true,
            myRequested = streak.UnmaskRequestedBy.Contains(meId),
            bothRequested = bothFlipped,
        };
    }

    // ─── DTOs ───────────────────────────────────────────────────

    private static object ToPersonaDto(Persona p) => new
    {
        id          = p.Id,
        displayName = p.DisplayName,
        avatarSeed  = p.AvatarSeed,
        bio         = p.Bio,
        mood        = p.Mood,
        date        = p.Date,
        expiresAt   = p.ExpiresAt,
    };

    private static object ToPersonaMessageDto(PersonaMessage m) => new
    {
        id                = m.Id,
        senderPersonaId   = m.SenderPersonaId,
        senderDisplayName = m.SenderDisplayName,
        senderAvatarSeed  = m.SenderAvatarSeed,
        content           = m.Content,
        createdAt         = m.CreatedAt,
        // mineFlag is decided client-side by comparing senderPersonaId
        // to the local "my today's persona" id, so we don't leak the
        // real-user-id from server.
    };
}
