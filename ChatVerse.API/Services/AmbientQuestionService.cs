using ChatVerse.API.Hubs;
using ChatVerse.API.Services.Games;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Services;

// ============================================================
//  AmbientQuestionService — background ticker that drops a
//  prompt into gameable chat rooms every few minutes.
//
//  Why a BackgroundService (not a per-room job)?
//    Most rooms are dormant most of the time. One shared loop
//    iterating a tiny whitelist scales fine; per-room jobs would
//    multiply that by ~100 and we'd still be sending almost no
//    work because each room only emits every N minutes.
//
//  Why room WHITELIST instead of "every chat room"?
//    Tech Talk / Random / DM threads shouldn't have trivia. The
//    feature is themed to Gaming Lounge / Mini Game. Whitelist
//    keeps that explicit; we can add a per-room flag later if
//    the list grows.
//
//  No per-user dismissal on the backend — clients hide it via
//  local state when the X is clicked. The next emission replaces
//  it for everyone anyway.
// ============================================================

public sealed class AmbientQuestionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<ChatHub> _chatHub;
    private readonly ILogger<AmbientQuestionService> _logger;

    // Slugs we drop ambient questions into. Match the live chat
    // room slugs — see the Rooms sidebar.
    private static readonly string[] GameableSlugs =
    {
        "gaming-lounge",
        "mini-game",
    };

    // Cadence: gentle. Faster than this and the chat starts to
    // feel like the questions are spamming over real conversation;
    // slower and the engagement boost fades.
    private static readonly TimeSpan EmitInterval = TimeSpan.FromMinutes(7);
    // Initial delay so a Render cold-start doesn't fire one before
    // the chat has rendered for any user.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    public AmbientQuestionService(
        IServiceScopeFactory scopeFactory,
        IHubContext<ChatHub> chatHub,
        ILogger<AmbientQuestionService> logger)
    {
        _scopeFactory = scopeFactory;
        _chatHub = chatHub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "AmbientQuestionService started (cadence: {Min} min)",
            EmitInterval.TotalMinutes);

        // Initial pause — we never want the FIRST tick to land in the
        // first 30s of process startup (cold-start clients haven't
        // rendered yet, plus we don't want our log line to compete
        // with the SignalR hub-registered banner).
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EmitOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ambient emit failed; will retry next tick");
            }

            try { await Task.Delay(EmitInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("AmbientQuestionService stopping");
    }

    private async Task EmitOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<AmbientQuestionProvider>();

        // One provider call → fan out to every gameable slug. Same
        // question for every room is fine — they're separate audiences,
        // no one's watching both at once.
        var question = await provider.NextAsync(ct);
        if (question is null)
        {
            _logger.LogDebug("AmbientQuestionProvider returned null this tick");
            return;
        }

        foreach (var slug in GameableSlugs)
        {
            try
            {
                // SignalR group name for chat rooms == the room slug
                // (see ChatHub.JoinRoom). Method name "AmbientQuestion"
                // is what useChatHub subscribes to on the frontend.
                await _chatHub.Clients.Group(slug).SendAsync(
                    "AmbientQuestion", question, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Ambient broadcast to {Slug} failed; other rooms unaffected", slug);
            }
        }

        _logger.LogInformation(
            "Ambient {Mode} emitted to {Count} rooms: {Text}",
            question.Mode,
            GameableSlugs.Length,
            question.Text.Length > 60 ? question.Text[..60] + "…" : question.Text);
    }
}
