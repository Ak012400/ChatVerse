using ChatVerse.API.Hubs;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Services;

// ============================================================
//  GhostDateService — Thursday 9pm IST pairing + lifecycle ticker.
//
//  Three responsibilities, each idempotent:
//    1. PAIR at Thursday 9pm IST. Pull all pending registrations
//       for today's IST date, shuffle, pair atomically. Push
//       GhostDateMatched to each side. Odd-one-out flips to
//       no_match status.
//    2. END chat at +30 min. Push GhostDateEnded to both sides
//       so their UI swaps to the decision modal.
//    3. EXPIRE at +35 min (= decision deadline). Resolve any
//       still-undecided dates as "expired" and push outcome.
//
//  Cadence: 60 seconds — fine-grained enough that all three time
//  anchors fire within a minute of their target.
//
//  Author-name lookup: we need real usernames in the GhostDate
//  row at pairing time (so reveal can show them later). The
//  PostgresProcService already exposes a username lookup helper —
//  we batch by hitting `GetUserByIdAsync` for each user in the
//  pair (cheap; <50 round-trips on a busy Thursday).
// ============================================================

public sealed class GhostDateService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<GhostDateHub> _hub;
    private readonly ILogger<GhostDateService> _logger;

    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    private string _lastPairedDate = "";   // event-date string we last ran pairing for

    public GhostDateService(
        IServiceScopeFactory scopeFactory,
        IHubContext<GhostDateHub> hub,
        ILogger<GhostDateService> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "GhostDateService started (cadence: {Seconds}s)",
            Cadence.TotalSeconds);

        try { await Task.Delay(StartupDelay, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ghost date tick failed; will retry");
            }
            try { await Task.Delay(Cadence, ct); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("GhostDateService stopping");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var ist    = nowUtc.AddHours(5).AddMinutes(30);

        using var scope = _scopeFactory.CreateScope();
        var mongo    = scope.ServiceProvider.GetRequiredService<MongoService>();
        var postgres = scope.ServiceProvider.GetRequiredService<PostgresProcService>();

        // 1) PAIRING at Thursday 9pm IST.
        if (ist.DayOfWeek == DayOfWeek.Thursday
            && ist.Hour == 21
            && _lastPairedDate != ist.Date.ToString("yyyy-MM-dd"))
        {
            var eventDate = ist.Date.ToString("yyyy-MM-dd");
            var scheduledFor = ist.Date.AddHours(21).AddHours(-5).AddMinutes(-30); // back to UTC
            await PairAsync(mongo, postgres, eventDate, scheduledFor, ct);
            _lastPairedDate = eventDate;
        }

        // 2) END CHAT — fire GhostDateEnded for every date that just
        //   crossed its ExpiresAt and hasn't been notified. We treat
        //   "ExpiresAt within the last minute" as the firing window.
        var awaiting = await mongo.GetDatesAwaitingDecisionAsync();
        foreach (var d in awaiting)
        {
            if ((nowUtc - d.ExpiresAt).TotalSeconds is >= 0 and <= 60)
            {
                await PushEndedAsync(d, ct);
            }
        }

        // 3) EXPIRE force-resolve.
        var expired = await mongo.GetExpiredUndecidedDatesAsync();
        foreach (var d in expired)
        {
            var resolved = await mongo.MarkGhostDateExpiredAsync(d.Id!);
            if (resolved is not null && resolved.Outcome is not null)
            {
                await _hub.Clients.User(resolved.UserAId)
                    .SendAsync("GhostDateOutcome", new
                    {
                        id        = resolved.Id,
                        outcome   = resolved.Outcome,
                        outcomeAt = resolved.OutcomeAt,
                        theirDisplay = resolved.Outcome == "mutual_reveal"
                            ? resolved.UserBUsername : (string?)null,
                        nextEligibleMatchAt = resolved.NextEligibleMatchAt,
                    }, ct);
                await _hub.Clients.User(resolved.UserBId)
                    .SendAsync("GhostDateOutcome", new
                    {
                        id        = resolved.Id,
                        outcome   = resolved.Outcome,
                        outcomeAt = resolved.OutcomeAt,
                        theirDisplay = resolved.Outcome == "mutual_reveal"
                            ? resolved.UserAUsername : (string?)null,
                        nextEligibleMatchAt = resolved.NextEligibleMatchAt,
                    }, ct);
            }
        }
    }

    private async Task PairAsync(
        MongoService mongo, PostgresProcService postgres,
        string eventDate, DateTime scheduledFor, CancellationToken ct)
    {
        var pool = await mongo.GetPendingRegistrationsAsync(eventDate);
        if (pool.Count == 0)
        {
            _logger.LogInformation("GhostDate pairing on {Date}: empty pool", eventDate);
            return;
        }

        // Fisher-Yates shuffle for unbiased pairing.
        var rng = new Random();
        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        var expiresAt        = scheduledFor.AddMinutes(MongoService.GhostDateChatMinutes);
        var decisionDeadline = expiresAt.AddMinutes(MongoService.GhostDateDecisionMinutes);

        int paired = 0;
        for (int i = 0; i + 1 < pool.Count; i += 2)
        {
            var a = pool[i];
            var b = pool[i + 1];

            string? aName = null, bName = null;
            try
            {
                if (Guid.TryParse(a.UserId, out var aGuid))
                    aName = (await postgres.GetUserAuthByIdAsync(aGuid))?.Username;
                if (Guid.TryParse(b.UserId, out var bGuid))
                    bName = (await postgres.GetUserAuthByIdAsync(bGuid))?.Username;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Username lookup failed for ghost-date pair");
            }

            var date = new GhostDate
            {
                UserAId           = a.UserId,
                UserBId           = b.UserId,
                UserAUsername     = aName ?? "Voyager",
                UserBUsername     = bName ?? "Voyager",
                EventDate         = eventDate,
                ScheduledFor      = scheduledFor,
                ExpiresAt         = expiresAt,
                DecisionDeadline  = decisionDeadline,
            };
            var saved = await mongo.InsertGhostDateAsync(date);

            await mongo.MarkRegistrationMatchedAsync(a.Id!, saved.Id!);
            await mongo.MarkRegistrationMatchedAsync(b.Id!, saved.Id!);

            // Per-user push — neither side sees the other's userId
            // anywhere in the payload.
            await _hub.Clients.User(a.UserId).SendAsync("GhostDateMatched", new
            {
                dateId           = saved.Id,
                scheduledFor     = saved.ScheduledFor,
                expiresAt        = saved.ExpiresAt,
                decisionDeadline = saved.DecisionDeadline,
            }, ct);
            await _hub.Clients.User(b.UserId).SendAsync("GhostDateMatched", new
            {
                dateId           = saved.Id,
                scheduledFor     = saved.ScheduledFor,
                expiresAt        = saved.ExpiresAt,
                decisionDeadline = saved.DecisionDeadline,
            }, ct);

            paired++;
        }

        // Odd one out (if pool is odd) — mark no_match.
        if (pool.Count % 2 == 1)
        {
            var orphan = pool[^1];
            await mongo.MarkRegistrationNoMatchAsync(orphan.Id!);
            _logger.LogInformation(
                "GhostDate pairing on {Date}: {User} got no match (odd pool)",
                eventDate, orphan.UserId);
        }

        _logger.LogInformation(
            "GhostDate pairing on {Date}: {Paired} pairs from {Pool} registrations",
            eventDate, paired, pool.Count);
    }

    private async Task PushEndedAsync(GhostDate d, CancellationToken ct)
    {
        await _hub.Clients.User(d.UserAId).SendAsync("GhostDateEnded", new
        {
            dateId           = d.Id,
            decisionDeadline = d.DecisionDeadline,
        }, ct);
        await _hub.Clients.User(d.UserBId).SendAsync("GhostDateEnded", new
        {
            dateId           = d.Id,
            decisionDeadline = d.DecisionDeadline,
        }, ct);
    }
}
