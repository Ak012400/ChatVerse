using System.Text;
using System.Text.Json;
using ChatVerse.API.Hubs;
using ChatVerse.Infrastructure.Persistence.Mongo;
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
    private readonly HttpClient _http;

    private const string GroqUrl = "https://api.groq.com/openai/v1/chat/completions";
    private const string Model = "llama-3.1-8b-instant";
    private const int ScanIntervalMs = 30_000;       // every 30s
    private const int IdleThresholdSecs = 45;        // room must be idle this long
    private const int PostCooldownSecs = 90;         // don't double-post within this

    public AiPersonaService(
        IServiceProvider services,
        IHubContext<ChatHub> chatHub,
        IConfiguration config,
        ILogger<AiPersonaService> logger,
        IHttpClientFactory httpFactory)
    {
        _services = services;
        _chatHub = chatHub;
        _config = config;
        _logger = logger;
        _http = httpFactory.CreateClient();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config.GetValue<bool>("Ai:EnablePresence");
        var apiKey = _config["Groq:ApiKey"] ?? "";

        if (!enabled || string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("YOUR_"))
        {
            _logger.LogInformation("AiPersonaService disabled (Ai:EnablePresence={Enabled}, Groq key set={KeySet})",
                enabled, !string.IsNullOrWhiteSpace(apiKey) && !apiKey.StartsWith("YOUR_"));
            return; // permanent no-op; container stays running, just no work
        }

        _logger.LogInformation("AiPersonaService started — scanning every {Ms}ms", ScanIntervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(apiKey, stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "AiPersonaService tick failed"); }
            await Task.Delay(ScanIntervalMs, stoppingToken);
        }
    }

    // ============================================================
    //  Tick — one scan over all active public rooms
    // ============================================================
    private async Task TickAsync(string apiKey, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<MongoService>();
        var redis = scope.ServiceProvider.GetRequiredService<RedisService>();

        // Public rooms only — never AI-spam a private/invite room.
        var rooms = await mongo.GetActiveRoomsAsync(50);
        var now = DateTime.UtcNow;

        foreach (var room in rooms)
        {
            if (room.IsPrivate) continue;
            ct.ThrowIfCancellationRequested();

            try
            {
                await MaybePostInRoomAsync(room.Slug, room.Name, mongo, redis, apiKey, now);
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
        MongoService mongo, RedisService redis,
        string apiKey, DateTime now)
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
        var reply = await GenerateReplyAsync(persona, roomName, recent, apiKey);
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
    //  Groq chat completion
    // ============================================================
    private async Task<string> GenerateReplyAsync(
        PersonaPool.Persona persona,
        string roomName,
        IReadOnlyList<ChatVerse.Domain.Enums.Message> recent,
        string apiKey)
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

        // Build messages array oldest-first.
        var userMessages = recent
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { role = "user", content = $"{m.SenderName}: {m.Content}" })
            .ToList<object>();

        var userTurn = userMessages.Count == 0
            ? new { role = "user", content = "(The room is quiet. Drop a casual line that fits this room.)" }
            : new { role = "user", content = "(Reply to the last message naturally.)" };
        userMessages.Add(userTurn);

        var msgs = new List<object> { new { role = "system", content = system } };
        msgs.AddRange(userMessages);

        var payload = new
        {
            model = Model,
            messages = msgs,
            temperature = 0.8,
            max_tokens = 80,
            stream = false,
        };

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, GroqUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Authorization", $"Bearer {apiKey}");

            var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Groq returned {Status}", res.StatusCode);
                return "";
            }

            var body = await res.Content.ReadAsStringAsync();
            var doc = JsonSerializer.Deserialize<JsonElement>(body);
            var text = doc.GetProperty("choices")[0]
                          .GetProperty("message")
                          .GetProperty("content")
                          .GetString() ?? "";
            return text.Trim().Trim('"');
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Groq call failed");
            return "";
        }
    }
}
