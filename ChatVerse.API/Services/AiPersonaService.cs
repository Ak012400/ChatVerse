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

    // Scan cadence — chosen so a lonely user never waits more than this
    // before the AI persona shows up. 6s feels near-instant in UX terms.
    private const int ScanIntervalMs = 6_000;

    // For rooms with multiple humans, AI doesn't barge in mid-conversation —
    // it only chimes in after this long of silence.
    private const int IdleThresholdSecsActive = 45;

    // For rooms with ≤1 humans (the "lonely" path), we skip the idle gate
    // entirely. The cooldown below is what stops the AI from spamming.
    private const int CooldownSecsLonely = 75;     // AI posts at most once / 75s when alone
    private const int CooldownSecsActive = 150;    // longer when humans are talking

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
    //  Decision tree (online = real human users in the room):
    //
    //    online == 0  →  skip (no one's looking, don't burn quota)
    //    online == 1  →  LONELY path — fast, no idle gate. Single user
    //                    gets an AI companion within one scan tick (~6s).
    //                    Throttled only by the 75-second cooldown.
    //    online >  1  →  ACTIVE path — only chime in after 45s of silence
    //                    so we never barge in on a real conversation.
    //                    Throttled by the 150-second cooldown.
    // ============================================================
    private async Task MaybePostInRoomAsync(
        string slug, string roomName,
        MongoService mongo, RedisService redis, AiChatProvider ai,
        DateTime now, CancellationToken ct)
    {
        var onlineCount = await redis.GetRoomOnlineCountAsync(slug);
        if (onlineCount == 0) return; // nobody to see it

        var isLonely = onlineCount == 1;

        // Cooldown check first — cheap, avoids hitting Mongo + Groq
        // if we just posted. Uses a real string key with explicit TTL
        // rather than the old "abuse UserOnline helper" approach which
        // pinned the cooldown to UserOnline's TTL (~5 min) regardless.
        var cooldownKey = $"ai:cooldown:{slug}";
        if (await redis.GetStringAsync(cooldownKey) != null) return;

        // Pull recent messages — used both for idle gating (active rooms)
        // and as conversational context for Groq/Gemini.
        var recent = await mongo.GetRoomMessagesAsync(slug, 0, 8);

        if (!isLonely && recent.Count > 0)
        {
            // ACTIVE room — only chime in once the humans have gone quiet.
            var last = recent[0]; // proc returns newest-first
            if ((now - last.CreatedAt).TotalSeconds < IdleThresholdSecsActive) return;
        }
        // Lonely path: no idle gate. We post even if the user just sent
        // a message a second ago — the AI is there to keep them company.

        // Acquire the cooldown lock with an explicit TTL. The cooldown
        // is different for lonely vs active rooms so a single user gets
        // more company than a chatty group.
        var cooldownSecs = isLonely ? CooldownSecsLonely : CooldownSecsActive;
        await redis.SetStringAsync(cooldownKey, "1", TimeSpan.FromSeconds(cooldownSecs));

        var persona = PersonaPool.PickFor(slug, now);
        var reply = await GenerateReplyAsync(persona, roomName, recent, ai, ct);
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
    //  Build chat-completion request and delegate to AiChatProvider
    //  (which handles Groq → Gemini fallback transparently).
    // ============================================================
    private async Task<string> GenerateReplyAsync(
        PersonaPool.Persona persona,
        string roomName,
        IReadOnlyList<ChatVerse.Domain.Entities.Message> recent,
        AiChatProvider ai,
        CancellationToken ct)
    {
        // System prompt is dynamic — when the room is empty / brand-new
        // user just joined, the persona should greet warmly rather than
        // reply-to-a-message-that-doesn't-exist.
        var isOpening = recent.Count == 0;
        var sceneRule = isOpening
            ? "- The room is quiet right now. Greet the room warmly with one short line that fits the theme — make a stranger feel welcome, ask something casual they can answer in one sentence."
            : "- Reply naturally to what people are saying. Don't repeat them. One short message, one open-ended hook at most.";

        var system = $$"""
        You are a casual user named {{persona.Username}} hanging out in the chat
        room "{{roomName}}". Your background: {{persona.Description}} You're from {{persona.City}}.

        Rules:
        - Keep replies short (1-2 sentences max).
        - Match the room's tone — casual, warm.
        - Don't mention you are an AI, but never claim to be human if asked directly —
          deflect with humour. Never give medical, legal, or financial advice.
        {{sceneRule}}
        """;

        var msgs = new List<AiChatProvider.ChatMessage>
        {
            new("system", system)
        };

        // Recent messages oldest-first, formatted "name: text" so the model
        // has speaker attribution without needing structured turns.
        foreach (var m in recent.OrderBy(m => m.CreatedAt))
            msgs.Add(new("user", $"{m.SenderName}: {m.Content}"));

        msgs.Add(new("user", isOpening
            ? "(You just walked into the room. There's one person here. Greet warmly with one short line that fits the theme.)"
            : "(Reply to the last message naturally.)"));

        var reply = await ai.CompleteAsync(
            new AiChatProvider.ChatRequest(msgs, Temperature: 0.8, MaxTokens: 80),
            ct);

        return reply.Trim().Trim('"');
    }
}
