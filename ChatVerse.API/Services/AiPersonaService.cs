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

    private const int ScanIntervalMs = 30_000;       // every 30s
    private const int IdleThresholdSecs = 45;        // room must be idle this long
    private const int PostCooldownSecs = 90;         // don't double-post within this

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
    // ============================================================
    private async Task MaybePostInRoomAsync(
        string slug, string roomName,
        MongoService mongo, RedisService redis, AiChatProvider ai,
        DateTime now, CancellationToken ct)
    {
        // Cooldown: did we (any replica) post here recently?
        var cooldownKey = $"ai:cooldown:{slug}";
        var coldExists = await redis.IsUserOnlineAsync(cooldownKey); // re-using helper as KEYS check
        if (coldExists) return;

        // Recent messages — used both for idle check + as Groq context.
        var recent = await mongo.GetRoomMessagesAsync(slug, 0, 8);
        if (recent.Count == 0)
        {
            // Empty room is fine but only post if the room is active enough
            // that someone will see it — gate on presence > 0.
            if (await redis.GetRoomOnlineCountAsync(slug) == 0) return;
        }
        else
        {
            // Idle check — last message must be > IdleThresholdSecs old.
            var last = recent[0]; // proc returns newest-first
            if ((now - last.CreatedAt).TotalSeconds < IdleThresholdSecs) return;
        }

        // Acquire a soft lock (write-once with TTL via existing setter).
        await redis.SetUserOnlineAsync(cooldownKey); // ~5min TTL, more than enough

        var persona = PersonaPool.PickFor(slug, now);
        var reply = await GenerateReplyAsync(persona, roomName, recent, ai, ct);
        if (string.IsNullOrWhiteSpace(reply)) return;

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
        var system = $$"""
        You are a casual user named {{persona.Username}} hanging out in the chat
        room "{{roomName}}". Your background: {{persona.Description}} You're from {{persona.City}}.

        Rules:
        - Keep replies short (1-2 sentences max).
        - Match the room's tone — casual, warm.
        - Don't ask many questions in one message; one open-ended hook is fine.
        - Don't mention you are an AI, but never claim to be human if asked directly —
          deflect with humour. Never give medical, legal, or financial advice.
        - Don't repeat what someone just said back at them.
        - If the room is empty, post a light conversation starter that fits the room theme.
        """;

        var msgs = new List<AiChatProvider.ChatMessage>
        {
            new("system", system)
        };

        // Recent messages oldest-first, formatted "name: text" so the model
        // has speaker attribution without needing structured turns.
        foreach (var m in recent.OrderBy(m => m.CreatedAt))
            msgs.Add(new("user", $"{m.SenderName}: {m.Content}"));

        msgs.Add(new("user", recent.Count == 0
            ? "(The room is quiet. Drop a casual line that fits this room.)"
            : "(Reply to the last message naturally.)"));

        var reply = await ai.CompleteAsync(
            new AiChatProvider.ChatRequest(msgs, Temperature: 0.8, MaxTokens: 80),
            ct);

        return reply.Trim().Trim('"');
    }
}
