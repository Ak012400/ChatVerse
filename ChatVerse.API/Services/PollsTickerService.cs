using ChatVerse.API.Hubs;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Services;

// ============================================================
//  PollsTickerService — auto-close expired in-room polls.
//
//  The poll lifecycle is mostly client-driven (create / vote /
//  close-early), but the ticker handles the auto-close edge so the
//  UI doesn't have to chase a wall-clock comparison against a stale
//  ExpiresAt. Every minute we sweep:
//
//   1. Fetch up to 50 polls where IsClosed=false AND ExpiresAt <= now.
//   2. For each: close it, broadcast PollClosed to its room group.
//
//  Idempotent — ClosePollAsync filters on IsClosed=false so the second
//  sweep in the same window is a no-op.
//
//  All work uses the stoppingToken so a Render restart kills the loop
//  cleanly without leaking a half-finished sweep.
// ============================================================

public sealed class PollsTickerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<ChatHub> _hub;
    private readonly ILogger<PollsTickerService> _logger;

    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
    private const int SweepBatchSize = 50;

    public PollsTickerService(
        IServiceScopeFactory scopeFactory,
        IHubContext<ChatHub> hub,
        ILogger<PollsTickerService> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "PollsTickerService started (cadence: {Sec} sec)",
            (int)Cadence.TotalSeconds);

        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PollsTickerService sweep failed");
            }

            try { await Task.Delay(Cadence, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<MongoService>();

        var expired = await mongo.GetExpiredOpenPollsAsync(SweepBatchSize, ct);
        if (expired.Count == 0) return;

        _logger.LogInformation("Auto-closing {Count} expired poll(s)", expired.Count);

        foreach (var p in expired)
        {
            if (ct.IsCancellationRequested) return;
            if (p.Id is null) continue;

            try
            {
                var closed = await mongo.ClosePollAsync(p.Id, ct);
                if (closed is null) continue;

                // Push final counts to the room. The ChatHub.ShapePoll
                // shape isn't reachable from here, so we project a
                // matching payload inline. (If the contract changes
                // there, mirror it here — small price for keeping the
                // ticker decoupled from the hub class internals.)
                await _hub.Clients.Group(closed.RoomSlug).SendAsync(
                    "PollClosed",
                    ProjectPoll(closed, includeVoters: !closed.Anonymous),
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PollsTickerService failed to close poll {PollId}", p.Id);
            }
        }
    }

    private static object ProjectPoll(ChatVerse.Domain.Entities.Poll p, bool includeVoters)
    {
        var counts = new int[p.Options.Count];
        foreach (var picks in p.Votes.Values)
        {
            foreach (var idx in picks)
            {
                if (idx >= 0 && idx < counts.Length) counts[idx]++;
            }
        }
        return new
        {
            id              = p.Id,
            roomSlug        = p.RoomSlug,
            creatorUserId   = p.CreatorUserId,
            creatorUsername = p.CreatorUsername,
            question        = p.Question,
            options         = p.Options,
            counts,
            multiSelect     = p.MultiSelect,
            anonymous       = p.Anonymous,
            createdAt       = p.CreatedAt,
            expiresAt       = p.ExpiresAt,
            isClosed        = p.IsClosed,
            closedAt        = p.ClosedAt,
            voters          = includeVoters ? p.Votes : null,
        };
    }
}
