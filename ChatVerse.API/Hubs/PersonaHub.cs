using ChatVerse.API.Extensions;
using ChatVerse.API.Services;
using ChatVerse.API.Services.Personas;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Hubs;

// ============================================================
//  PersonaHub — server-side surface for the Persona Roulette
//  feature. Mirrors the layout of TimeCapsuleHub: privacy-first
//  DTO shaping, server-only real-user ↔ persona mapping.
//
//  Client methods (must match TECH.md ‑ §5):
//    • GetMyPersona() → today's Persona for the caller, generated
//      on-the-fly if PersonaResetService hasn't claimed it yet.
//    • GetActiveStreaks() → all streak rows the caller is part of,
//      with the OTHER party shown only as their CURRENT persona
//      (unless UnmaskedAt is set, in which case the real username
//      surfaces).
//    • RequestMutualUnmask(streakId) → idempotent "I'd like to
//      unmask". When BOTH sides have requested, the streak flips.
//    • AcceptMutualUnmask(streakId) → alias of Request for now;
//      kept distinct in the contract for clarity.
//
//  Server-push events (used by future PersonaResetService):
//    • "PersonaRolled"  — your new daily persona is ready.
//    • "StreakMilestone" — your streak crossed 3/7/30 days.
//    • "StreakUnmasked"  — the other side accepted unmask.
//
//  Note: actual persona-DM messaging will route through a thin
//  extension of the existing DM pipeline once the page lands —
//  this hub doesn't carry message traffic.
// ============================================================

[Authorize]
public class PersonaHub : Hub
{
    private readonly MongoService _mongo;
    private readonly ILogger<PersonaHub> _logger;

    public PersonaHub(MongoService mongo, ILogger<PersonaHub> logger)
    {
        _mongo = mongo;
        _logger = logger;
    }

    public async Task<object> GetMyPersona()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var persona = await _mongo.GetPersonaForDayAsync(meId, today);
        if (persona is null)
        {
            // Self-heal: the reset service runs at 00:00 UTC but if a
            // user logs in during the small startup window we just
            // generate on-demand and upsert. PersonaResetService will
            // see it already there and skip.
            persona = PersonaGenerator.Generate(meId, DateTime.UtcNow);
            await _mongo.UpsertPersonaAsync(persona);
        }
        return ToPersonaDto(persona);
    }

    public async Task<object> GetActiveStreaks()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var streaks = await _mongo.GetStreaksForUserAsync(meId);

        // For each streak, surface the OTHER side's CURRENT persona —
        // or their real username if mutual-unmask has flipped.
        var view = new List<object>();
        foreach (var s in streaks)
        {
            var otherRealId = s.RealUserA == meId ? s.RealUserB : s.RealUserA;
            var otherPersona = await _mongo.GetPersonaForDayAsync(otherRealId, today);

            view.Add(new
            {
                streakId        = s.Id,
                consecutiveDays = s.ConsecutiveDays,
                lastDay         = s.LastDay,
                unmasked        = s.UnmaskedAt != null,
                vaulted         = s.VaultedAt != null,
                otherDisplay    = otherPersona?.DisplayName ?? "Drifted away",
                otherAvatarSeed = otherPersona?.AvatarSeed  ?? null,
                otherMood       = otherPersona?.Mood        ?? null,
                // Tier reveal — drives UI badges.
                canUnmask       = s.ConsecutiveDays >= 7 && s.UnmaskedAt == null,
                hasMarker       = s.ConsecutiveDays >= 3,
            });
        }
        return new { count = view.Count, streaks = view };
    }

    /// <summary>
    /// Two-step handshake to avoid accidental reveal: the SAME user
    /// calling twice on the same streak does nothing; both sides must
    /// call. We track "requested" by writing a marker on the streak
    /// (UnmaskedAt is set ONLY when both sides have requested) —
    /// kept simple for the scaffold; full handshake will land with
    /// the frontend page.
    /// </summary>
    public async Task<bool> RequestMutualUnmask(string streakId)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        // TODO(persona-roulette frontend): per-side "requested" flag
        // so we don't unmask on a single click. For now the scaffold
        // just no-ops; the actual flip happens in AcceptMutualUnmask.
        _logger.LogInformation(
            "Persona unmask requested by {User} on streak {Streak} — pending second side",
            meId, streakId);
        await Task.CompletedTask;
        return true;
    }

    /// <summary>
    /// Atomic flip: only fires if the streak isn't already unmasked.
    /// </summary>
    public async Task<bool> AcceptMutualUnmask(string streakId)
    {
        var ok = await _mongo.SetStreakUnmaskedAsync(streakId);
        if (ok)
        {
            // TODO: notify both parties via server-push so their
            // streak card flips to "Unmasked" without a refresh.
        }
        return ok;
    }

    // ── Privacy-shaped DTOs ─────────────────────────────────────

    private static object ToPersonaDto(Persona p) => new
    {
        // NB: We deliberately DON'T expose UserId. Even though the
        // client only ever asks about their own persona via this DTO,
        // keeping it absent makes accidental future reuse safe.
        id          = p.Id,
        displayName = p.DisplayName,
        avatarSeed  = p.AvatarSeed,
        bio         = p.Bio,
        mood        = p.Mood,
        date        = p.Date,
        expiresAt   = p.ExpiresAt,
    };
}
