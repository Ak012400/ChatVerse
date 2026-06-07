using ChatVerse.API.Hubs;
using ChatVerse.API.Models.Games;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Services.Games;

// ============================================================
//  GameTickerService — single background loop driving deadline-
//  based state advancement across every active game session.
//
//  Why one shared loop instead of a per-session Timer?
//    A Timer per session looks tidy until you have 100 rooms. Each
//    timer has its own ThreadPool slot, its own GC pressure, and
//    can't share batching with siblings. A single 1Hz loop iterating
//    a snapshot of the registry stays flat under load — the cost
//    grows linearly with active rooms, not with deadlines × rooms.
//
//  Cadence:
//    1Hz is plenty for a 15-second-deadline quiz — even the slowest
//    advance is ≤ 1s late, imperceptible to humans. Bumping to higher
//    frequency would just waste cycles.
//
//  Resilience:
//    Any exception in a single session's TickAsync is logged and
//    swallowed — one misbehaving room can't kill the ticker. Render's
//    process supervisor will restart us if the loop itself crashes,
//    but the loop body is too small to crash in practice.
// ============================================================

public sealed class GameTickerService : BackgroundService
{
    private readonly GameSessionRegistry _registry;
    private readonly IHubContext<GameHub> _hub;
    private readonly ILogger<GameTickerService> _logger;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    public GameTickerService(
        GameSessionRegistry registry,
        IHubContext<GameHub> hub,
        ILogger<GameTickerService> logger)
    {
        _registry = registry;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GameTickerService started (1Hz)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GameTicker tick loop threw");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("GameTickerService stopping");
    }

    private async Task TickOnceAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // Snapshot the collection so a concurrent CreateAsync/DropAsync
        // can't trip our iterator.
        var sessions = _registry.AllActive;
        if (sessions.Count == 0) return;

        foreach (var session in sessions)
        {
            try
            {
                var changed = await session.TickAsync(now, ct);
                // Even if Tick returned false, the session may have
                // pending events from a previous call (e.g. ParticipantJoined
                // queued by JoinAsync, but the hub returned before the
                // broadcast could be flushed). Always drain — DrainEvents
                // is part of the IGameSession contract so no type-check
                // is needed (works for Quiz, Jokes, and any future games).
                foreach (var ev in session.DrainEvents())
                    await BroadcastAsync(session.Slug, ev, ct);

                // NB: no auto-drop on Ended status. The user explicitly
                // requested that ended rooms persist until the creator
                // ends them — so post-game review (board replay,
                // scoreboard, chat) stays available indefinitely. Redis
                // TTL still GC's abandoned rooms after RedisTTL.GameRoom
                // (1hr) as a backstop.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Tick failed for game session {Slug} — skipping this round",
                    session.Slug);
            }
        }
    }

    /// <summary>
    /// Translate a typed GameEvent into the appropriate SignalR
    /// broadcast. Centralising the switch here means the hub doesn't
    /// need to know event types — it just calls back into the registry.
    ///
    /// Method names must match what useGameHub.ts subscribes to on the
    /// frontend: "QuestionPushed", "QuestionRevealed", "ScoreUpdated",
    /// "GameEnded", "ParticipantJoined", "ParticipantLeft".
    /// </summary>
    public async Task BroadcastAsync(string slug, GameEvent ev, CancellationToken ct)
    {
        var group = GameHub.RoomGroup(slug);
        switch (ev)
        {
            case QuestionPushedEvent qp:
                await _hub.Clients.Group(group).SendAsync(
                    "QuestionPushed", qp.Question, ct);
                break;
            case QuestionRevealedEvent qr:
                await _hub.Clients.Group(group).SendAsync(
                    "QuestionRevealed",
                    new { reveal = qr.Reveal, scoreboard = qr.Scoreboard },
                    ct);
                break;
            case ScoreUpdatedEvent su:
                await _hub.Clients.Group(group).SendAsync(
                    "ScoreUpdated", su.Scoreboard, ct);
                break;
            case GameEndedEvent ge:
                await _hub.Clients.Group(group).SendAsync(
                    "GameEnded",
                    new { finalScoreboard = ge.FinalScoreboard, reason = ge.Reason },
                    ct);
                break;
            case ParticipantJoinedEvent pj:
                await _hub.Clients.Group(group).SendAsync(
                    "ParticipantJoined", pj.Participant, ct);
                break;
            case ParticipantLeftEvent pl:
                await _hub.Clients.Group(group).SendAsync(
                    "ParticipantLeft", new { userId = pl.UserId }, ct);
                break;

            // ─── Jokes-mode events ─────────────────────────────────────
            // Methods names mirror useGameHub.ts subscriptions: client
            // listens for "JokePushed", "ReactionsUpdated", "JokeRevealed",
            // and "JokesFinished".
            case JokePushedEvent jp:
                await _hub.Clients.Group(group).SendAsync(
                    "JokePushed", jp.Joke, ct);
                break;
            case ReactionsUpdatedEvent ru:
                await _hub.Clients.Group(group).SendAsync(
                    "ReactionsUpdated", ru.Update, ct);
                break;
            case JokeRevealedEvent jr:
                await _hub.Clients.Group(group).SendAsync(
                    "JokeRevealed", jr.Reveal, ct);
                break;
            case JokesFinishedEvent jf:
                await _hub.Clients.Group(group).SendAsync(
                    "JokesFinished",
                    new { finalStats = jf.FinalStats, reason = jf.Reason },
                    ct);
                break;

            // ─── Chess events ──────────────────────────────────────
            case ChessGameStartedEvent cg:
                await _hub.Clients.Group(group).SendAsync(
                    "ChessGameStarted", cg.Snapshot, ct);
                break;
            case ChessMovePushedEvent cm:
                await _hub.Clients.Group(group).SendAsync(
                    "ChessMovePushed", cm.Move, ct);
                break;
            case JoinRequestedEvent jrq:
                await _hub.Clients.Group(group).SendAsync(
                    "JoinRequested", jrq.Request, ct);
                break;
            case JoinRequestResolvedEvent jrr:
                await _hub.Clients.Group(group).SendAsync(
                    "JoinRequestResolved", jrr.Resolution, ct);
                break;

            default:
                _logger.LogWarning("Unhandled GameEvent type {Type}", ev.GetType().Name);
                break;
        }
    }
}
