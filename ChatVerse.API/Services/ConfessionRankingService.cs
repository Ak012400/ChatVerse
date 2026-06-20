using ChatVerse.API.Hubs;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Services;

// ============================================================
//  ConfessionRankingService — daily crowning + reveal nudge.
//
//  Each UTC day, sometime after 00:00:
//    1. Look at yesterday's confessions, pick the top-reaction
//       row, set TopRankedAt. Idempotent — once-per-date.
//    2. Push a "TopConfessionOffered" SignalR event to that
//       author's connections. Their client surfaces the
//       reveal modal: accept (public banner) or decline
//       (Ghost Voice badge).
//
//  Confessions past 30 days are swept by the existing
//  MaintenanceService at its 6h cadence — no need to duplicate
//  here. Only exception: top-ranked + revealed confessions stay
//  on the Lore Wall forever, which the sweeper already respects
//  via the TopRankedAt presence check.
//
//  Cadence: 30 minutes — fine-grained enough that the daily
//  promotion happens within half-an-hour of midnight UTC.
// ============================================================

public sealed class ConfessionRankingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<ConfessionHub> _hub;
    private readonly ILogger<ConfessionRankingService> _logger;

    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(4);

    private string _lastProcessedYesterday = "";

    public ConfessionRankingService(
        IServiceScopeFactory scopeFactory,
        IHubContext<ConfessionHub> hub,
        ILogger<ConfessionRankingService> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "ConfessionRankingService started (cadence: {Mins} min)",
            Cadence.TotalMinutes);

        try { await Task.Delay(StartupDelay, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Confession ranking tick failed; will retry");
            }
            try { await Task.Delay(Cadence, ct); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("ConfessionRankingService stopping");
    }

    private async Task TickOnceAsync(CancellationToken ct)
    {
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");

        // Same-day skip — we only need to promote a given date once.
        if (_lastProcessedYesterday == yesterday) return;

        using var scope = _scopeFactory.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<MongoService>();

        var top = await mongo.GetTopForDateAsync(yesterday);
        if (top is null)
        {
            // Nothing got any reactions yesterday — perfectly fine.
            // Skip without locking the date so a late-blooming
            // reaction could still promote it on a future tick. We
            // bound that by only re-processing if the date string
            // changes (= new UTC day rolls over).
            _lastProcessedYesterday = yesterday;
            return;
        }

        var promoted = await mongo.MarkTopRankedAsync(top.Id!);
        if (!promoted)
        {
            // Already promoted in a previous tick — treat as success.
            _lastProcessedYesterday = yesterday;
            return;
        }

        _logger.LogInformation(
            "Confession {Id} crowned top for {Date} ({Reactions} reactions)",
            top.Id, yesterday, top.TotalReactions);

        // Fan a per-user event to the author. Their client modal
        // appears on next render — see useConfessionHub on the
        // frontend.
        await _hub.Clients
            .User(top.AuthorUserId)
            .SendAsync("TopConfessionOffered", new
            {
                id             = top.Id,
                content        = top.Content,
                totalReactions = top.TotalReactions,
                date           = top.Date,
            }, ct);

        _lastProcessedYesterday = yesterday;
    }
}
