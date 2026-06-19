using ChatVerse.API.Hubs;
using ChatVerse.API.Services.StoryChain;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Services;

// ============================================================
//  StoryChainService — daily lifecycle ticker for Story Chain.
//
//  Two responsibilities, both idempotent:
//    1. SPAWN today's chain at 3pm IST. Most days the hub's
//       lazy EnsureChainAsync will have already created it the
//       moment the first user opened the page after 3pm — this
//       service is the belt-and-braces "if no-one opens until
//       later, today's chain still exists" guarantee.
//    2. CLOSE yesterday's chain at IST midnight. Lock any
//       chain still active for a date < today (IST) and promote
//       it to published. Partial chains ship — that's by design.
//
//  Cadence: 10 minutes — fine-grained enough that "3pm IST"
//  triggers without more than a 10-min delay, light enough to
//  not hammer Mongo.
// ============================================================

public sealed class StoryChainService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<StoryChainHub> _hub;
    private readonly ILogger<StoryChainService> _logger;

    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    public StoryChainService(
        IServiceScopeFactory scopeFactory,
        IHubContext<StoryChainHub> hub,
        ILogger<StoryChainService> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "StoryChainService started (cadence: {Mins} min)",
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
                _logger.LogError(ex, "StoryChain tick failed; will retry");
            }
            try { await Task.Delay(Cadence, ct); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("StoryChainService stopping");
    }

    private async Task TickOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<MongoService>();

        var nowIst    = DateTime.UtcNow.AddHours(5).AddMinutes(30);
        var todayIst  = nowIst.ToString("yyyy-MM-dd");

        // ── 1) Spawn today's chain if past 3pm IST and none exists.
        //   Earlier-in-the-day chain creation is left to the lazy
        //   hub path so we honour the "3pm reveal" feel.
        if (nowIst.Hour >= 15)
        {
            var existing = await mongo.GetActiveStoryChainAsync(todayIst);
            if (existing is null)
            {
                var prompt = StoryPromptBank.PickForDate(todayIst);
                var chain = await mongo.EnsureDailyStoryChainAsync(todayIst, prompt);
                _logger.LogInformation(
                    "StoryChain {Id} spawned for {Date}",
                    chain.Id, todayIst);
            }
        }

        // ── 2) Lock + publish any still-active chains from past days.
        //   Anything whose PromptDate < today IST is overdue: midnight
        //   has come and gone for that date.
        var active = await mongo.GetActiveChainsAsync();
        foreach (var chain in active)
        {
            if (string.CompareOrdinal(chain.PromptDate, todayIst) >= 0) continue;

            var locked = await mongo.LockStoryChainAsync(chain.Id!);
            if (locked)
            {
                await _hub.Clients
                    .Group($"story-chain:{chain.Id}")
                    .SendAsync("ChainLocked", new
                    {
                        chainId = chain.Id,
                        reason  = "day_ended",
                    }, ct);
            }
        }

        // ── 3) Promote locked → published. Kept as a separate pass
        //   so a moderation hook could slot in here later.
        var locked2 = await mongo.GetLockedChainsAsync();
        foreach (var chain in locked2)
        {
            await mongo.PublishStoryChainAsync(chain.Id!);
        }
    }
}
