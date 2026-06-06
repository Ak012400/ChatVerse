using ChatVerse.API.Hubs;
using ChatVerse.Infrastructure.ExternalServices.AI;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.Redis;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Services;

/// <summary>
/// Keeps lightly-active rooms alive by having a Groq-driven AI persona
/// post the occasional message when a room has one (or zero) real users
/// and the conversation has been quiet for a while.
///
/// Design constraints:
///  • Off by default — gated on Ai:EnablePresence in config.
///  • Picks a persona stable per (room × hour) so the user perceives
///    a single human, not a stream of bots.
///  • Emits messages with senderType = "ai_host" so the frontend can
///    render a small "AI" badge — never claims to be human in text.
///  • Throttles per room (Redis lock 60s) so multiple replicas don't
///    double-post.
///  • Stores nothing in Mongo — messages are ephemeral by design.
/// </summary>
public class AiPersonaService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IHubContext<ChatHub> _chatHub;
    private readonly ILogger<AiPersonaService> _logger;
    private readonly IConfiguration _config;

    // Scan cadence — 25s strikes a balance: lonely users still feel
    // attended to (under half a minute is psychologically "fast"),
    // while the background loop only spends 4 Redis SCARD calls/minute
    // per room instead of 10. At 10 active rooms that's a 6,000 cmd/day
    // saving — important on the Upstash free tier's 10k/day cap.
    private const int ScanIntervalMs = 25_000;

    // For rooms with multiple humans, AI doesn't barge in mid-conversation —
    // it only chimes in after this long of silence.
    private const int IdleThresholdSecsActive = 45;

    // Per-PERSONA cooldown (each room hosts 2 personas, each on its own
    // timer). Lonely: short, so back-and-forth feels lively. Active: longer
    // so the AI hosts don't drown out actual humans.
    private const int CooldownSecsLonelyPerPersona = 25;
    private const int CooldownSecsActivePerPersona = 90;

    public AiPersonaService(
        IServiceProvider services,
        IHubContext<ChatHub> chatHub,
        IConfiguration config,
        ILogger<AiPersonaService> logger)
    {
        _services = services;
        _chatHub = chatHub;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config.GetValue<bool>("Ai:EnablePresence");
        // Any provider key counts — AiChatProvider tries Groq first, falls
        // back to Gemini, so either is enough to flip the service on.
        var hasAnyAiKey =
            IsRealKey(_config["Groq:ApiKey"]) ||
            IsRealKey(_config["Gemini:ApiKey"]);

        if (!enabled || !hasAnyAiKey)
        {
            _logger.LogInformation(
                "AiPersonaService disabled (Ai:EnablePresence={Enabled}, AI provider key set={KeySet})",
                enabled, hasAnyAiKey);
            return; // permanent no-op; container stays running, just no work
        }

        _logger.LogInformation("AiPersonaService started — scanning every {Ms}ms", ScanIntervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "AiPersonaService tick failed"); }
            await Task.Delay(ScanIntervalMs, stoppingToken);
        }
    }

    private static bool IsRealKey(string? k) =>
        !string.IsNullOrWhiteSpace(k) && !k.StartsWith("YOUR_");

    // ============================================================
    //  Tick — one scan over all active public rooms
    // ============================================================
    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<MongoService>();
        var redis = scope.ServiceProvider.GetRequiredService<RedisService>();
        var ai = scope.ServiceProvider.GetRequiredService<AiChatProvider>();

        // Public rooms only — never AI-spam a private/invite room.
        var rooms = await mongo.GetActiveRoomsAsync();
        var now = DateTime.UtcNow;

        foreach (var room in rooms)
        {
            if (room.IsPrivate) continue;
            ct.ThrowIfCancellationRequested();

            try
            {
                await MaybePostInRoomAsync(room.Slug, room.DisplayName, mongo, redis, ai, now, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI post failed for room {Slug}", room.Slug);
            }
        }
    }

    // ============================================================
    //  Should this room get an AI message right now?
    //
    //  Each public room is hosted by TWO deterministic AI personas
    //  (PersonaPool.PickPairFor). Both can speak; each has its own
    //  cooldown so they alternate naturally — feels like two regulars
    //  hanging out in the room rather than one bot interrupting.
    //
    //  Decision tree (online = real human users in the room):
    //
    //    online == 0  →  skip (no one's looking, don't burn quota)
    //    online == 1  →  LONELY path — fast back-and-forth. Each persona
    //                    cools down for 25s independently so the room
    //                    sees a new AI message every ~12s on average.
    //    online >  1  →  ACTIVE path — wait for 45s of silence first,
    //                    then post; each persona is throttled at 90s.
    // ============================================================
    private async Task MaybePostInRoomAsync(
        string slug, string roomName,
        MongoService mongo, RedisService redis, AiChatProvider ai,
        DateTime now, CancellationToken ct)
    {
        var onlineCount = await redis.GetRoomOnlineCountAsync(slug);
        if (onlineCount == 0) return; // nobody to see it

        var isLonely = onlineCount == 1;
        var (personaA, personaB) = PersonaPool.PickPairFor(slug, now);

        // Pull recent messages — used for idle gating, persona selection,
        // and as conversational context for Groq/Gemini.
        var recent = await mongo.GetRoomMessagesAsync(slug, 0, 10);

        if (!isLonely && recent.Count > 0)
        {
            // ACTIVE room — only chime in once humans have gone quiet.
            var last = recent[0]; // proc returns newest-first
            if ((now - last.CreatedAt).TotalSeconds < IdleThresholdSecsActive) return;
        }

        // Decide which of the two personas should speak this tick.
        // Prefer the one whose cooldown has expired AND who didn't just
        // speak — keeps the conversation feeling like two distinct people.
        var chosen = await PickAvailablePersonaAsync(slug, personaA, personaB, recent, redis);
        if (chosen == null) return; // both still cooling down

        // Lock the chosen persona's cooldown immediately so two replicas
        // (or the next tick) don't both pick this one.
        var cooldownKey = $"ai:cooldown:{slug}:{chosen.Username.ToLowerInvariant()}";
        var cooldownSecs = isLonely ? CooldownSecsLonelyPerPersona : CooldownSecsActivePerPersona;
        await redis.SetStringAsync(cooldownKey, "1", TimeSpan.FromSeconds(cooldownSecs));

        var persona = chosen;
        var reply = await GenerateReplyAsync(persona, roomName, recent, personaA, personaB, ai, ct);
        if (string.IsNullOrWhiteSpace(reply))
        {
            // Provider failed — release the lock so we can try again
            // sooner instead of waiting out the full cooldown.
            await redis.DeleteKeyAsync(cooldownKey);
            return;
        }

        // Optional typing pre-signal so the message doesn't pop instantly.
        try
        {
            await _chatHub.Clients.Group(slug).SendAsync("UserTyping", new
            {
                userId = AiUserIdFor(persona),
                username = persona.Username,
            });
            await Task.Delay(Random.Shared.Next(1500, 3000));
        }
        catch { /* typing is best-effort */ }

        var msgId = Guid.NewGuid().ToString("N")[..12];
        await _chatHub.Clients.Group(slug).SendAsync("ReceiveMessage", new
        {
            id = msgId,
            roomId = slug,
            senderId = AiUserIdFor(persona),
            senderName = persona.Username,
            senderType = "ai_host",     // frontend keys off this to show badge
            senderAvatar = (string?)null,
            content = reply,
            type = "text",
            mediaUrl = (string?)null,
            replyTo = (string?)null,
            reactions = new Dictionary<string, List<string>>(),
            modStatus = "clean",
            createdAt = DateTime.UtcNow,
        });

        _logger.LogInformation("AI ({Persona}) posted in #{Slug}: {Preview}",
            persona.Username, slug,
            reply.Length > 60 ? reply[..60] + "…" : reply);
    }

    private static string AiUserIdFor(PersonaPool.Persona p)
        => $"ai-host:{p.Username.ToLowerInvariant()}";

    // ============================================================
    //  Persona selection — return the persona that should speak next,
    //  or null if both are still cooling down.
    //
    //  Rules:
    //   • A persona on cooldown can't speak.
    //   • If the LAST message was from one of the personas, prefer
    //     the OTHER one — keeps the room feeling like two voices.
    //   • Otherwise return whichever is available (A first as tiebreak).
    // ============================================================
    private static async Task<PersonaPool.Persona?> PickAvailablePersonaAsync(
        string slug,
        PersonaPool.Persona a, PersonaPool.Persona b,
        IReadOnlyList<ChatVerse.Domain.Entities.Message> recent,
        RedisService redis)
    {
        var aCooldown = await redis.GetStringAsync($"ai:cooldown:{slug}:{a.Username.ToLowerInvariant()}");
        var bCooldown = await redis.GetStringAsync($"ai:cooldown:{slug}:{b.Username.ToLowerInvariant()}");
        var aFree = aCooldown == null;
        var bFree = bCooldown == null;

        if (!aFree && !bFree) return null;
        if (aFree && !bFree) return a;
        if (!aFree && bFree) return b;

        // Both free — pick the one who DIDN'T speak last, so the two
        // personas alternate naturally.
        if (recent.Count > 0)
        {
            var lastFromB = recent[0].SenderName == b.Username;
            return lastFromB ? a : b;
        }
        return a;
    }

    // ============================================================
    //  Build chat-completion request and delegate to AiChatProvider
    //  (which handles Groq → Gemini fallback transparently).
    // ============================================================
    private async Task<string> GenerateReplyAsync(
        PersonaPool.Persona persona,
        string roomName,
        IReadOnlyList<ChatVerse.Domain.Entities.Message> recent,
        PersonaPool.Persona companionA,
        PersonaPool.Persona companionB,
        AiChatProvider ai,
        CancellationToken ct)
    {
        // System prompt adapts to context:
        //   • brand-new room (no messages)  → warm greeting
        //   • last message was from a real user → reply to them
        //   • last message was from the OTHER persona → banter, agree,
        //     extend the thread (this is what makes two personas feel
        //     like a real conversation, not parallel monologues)
        var otherPersona = persona.Username == companionA.Username ? companionB : companionA;

        // Identify which recent messages came from humans vs from AI hosts.
        // The previous version fed every recent message back into the model
        // as context, which produced a runaway feedback loop: an AI host
        // mentions a topic once, that line lands in the next scan's context,
        // the model dutifully continues the same topic, and the chat ends up
        // talking about (e.g.) "Japan" for twenty straight messages with no
        // human input. The fix is to be smarter about what gets included:
        //   • If the last message is a real human, build the full context
        //     and reply.
        //   • If the LAST few messages are all AI, pretend the room is
        //     fresh — open a new line instead of extending the old thread.
        var aiUsernames = new HashSet<string>(StringComparer.Ordinal)
        {
            companionA.Username, companionB.Username,
        };
        var lastSender = recent.Count > 0 ? recent[0].SenderName : null;
        var lastWasOtherAi = lastSender == otherPersona.Username;
        var lastWasHuman = recent.Count > 0 && !aiUsernames.Contains(recent[0].SenderName);

        // "Effectively opening" = either the room is genuinely empty, or the
        // last 4 messages are all AI hosts with no human in between. In that
        // case the model should pivot to a fresh topic rather than reply to
        // a dead AI thread.
        var aiOnlyTrail = recent.Count > 0
            && recent.Take(4).All(m => aiUsernames.Contains(m.SenderName));
        var isOpening = recent.Count == 0;
        var pivotToFresh = !isOpening && aiOnlyTrail;

        // Topic pool to nudge the model away from sticky topics. Picked
        // deterministically per scan tick so neither persona can lock onto
        // a single theme.
        var freshTopics = new[]
        {
            "your weekend plans", "something funny that happened today",
            "a song you can't get out of your head", "your favourite chai/coffee spot",
            "a tiny life-hack you wish you knew earlier",
            "what you'd cook tonight if guests were coming",
            "a hobby you've been meaning to start",
            "a movie or show you'd rewatch tomorrow",
            "a small win from this week",
            "a quirky observation about your city",
        };
        var freshTopic = freshTopics[(int)(((uint)(roomName + ":" + DateTime.UtcNow.ToString("yyyyMMddHHmm")).GetHashCode()) % (uint)freshTopics.Length)];

        string sceneRule;
        if (isOpening)
            sceneRule = $"- The room is quiet. You just walked in alongside your friend {otherPersona.Username}. Drop a single warm greeting that fits the room theme — make any stranger feel welcome.";
        else if (pivotToFresh)
            sceneRule = $"- The room has been quiet (no one has spoken for a while). Pivot to a FRESH topic — try something like \"{freshTopic}\". Do NOT continue the previous topic, that thread is over. One short line, casual tone.";
        else if (lastWasOtherAi)
            sceneRule = $"- {otherPersona.Username} just said something. React naturally — agree briefly then add NEW information or pivot. Do NOT just rephrase what they said. One short line.";
        else
            sceneRule = "- Reply naturally to what the human just said. Don't echo them back. Keep it short and warm.";

        var system = $$"""
        You are a casual user named {{persona.Username}} in the chat room "{{roomName}}".
        Background: {{persona.Description}} You're from {{persona.City}}.

        You share this room with your friend {{otherPersona.Username}} — sometimes you'll
        be replying to them, sometimes to other people in the room. Treat them like a
        familiar buddy. Don't @-mention them every time; just talk naturally.

        Rules:
        - Keep replies SHORT (1-2 sentences max). Never longer.
        - Match the room's tone — casual, warm, Hindi-English mix is fine if the room uses it.
        - Don't mention you are an AI, but never claim to be human if asked directly —
          deflect with humour. Never give medical, legal, or financial advice.
        - Don't open every message with "Hey" or with the other person's name.
        - CRITICAL: Do NOT repeat or extend the dominant topic of the previous
          messages unless a human just brought it up. Vary topics actively —
          if the last 2-3 lines were about one subject, switch to something new.
        - Avoid naming specific countries / places / brands unless a human has
          just mentioned them. Stay grounded in your own persona's background.
        {{sceneRule}}
        """;

        var msgs = new List<AiChatProvider.ChatMessage>
        {
            new("system", system)
        };

        // Context construction:
        //   • If a human spoke recently, include all recent context so the AI
        //     can respond meaningfully.
        //   • Otherwise (pivoting to fresh), include NOTHING from the AI-only
        //     trail — the prompt above tells the model to start fresh, and
        //     including the stale thread would just tempt it to continue.
        if (!pivotToFresh)
        {
            foreach (var m in recent.OrderBy(m => m.CreatedAt))
                msgs.Add(new("user", $"{m.SenderName}: {m.Content}"));
        }

        msgs.Add(new("user", isOpening
            ? "(You just walked into the room. Greet warmly with one short line that fits the theme.)"
            : pivotToFresh
                ? $"(The previous topic is dead. Open a fresh line — try {freshTopic}. One short casual sentence.)"
                : lastWasOtherAi
                    ? $"(React to what your friend {otherPersona.Username} just said in ONE short line — add a new angle, don't restate.)"
                    : "(Reply to what the human just said. Keep it short and warm.)"));

        var reply = await ai.CompleteAsync(
            new AiChatProvider.ChatRequest(msgs, Temperature: 0.8, MaxTokens: 80),
            ct);

        return reply.Trim().Trim('"');
    }
}
