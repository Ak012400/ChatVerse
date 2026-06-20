using ChatVerse.API.Hubs;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Services;

// ============================================================
//  PyaarLiveOrchestrator — Saturday 8pm IST show lifecycle.
//
//  Minute tick handles:
//    A) FORM at Saturday 8pm IST. Picks 20 from pending pool,
//       random-shuffles, pairs into 10 couples, opens show with
//       Round 1 (Icebreaker, 30 min). Push ShowStarted to all.
//    B) ADVANCE rounds when current round window closes:
//       Round 1 → Round 2 (Free chat, 45 min)
//       Round 2 → ELIMINATION (bottom 3 by votes) → Round 3 (Deeper Q's, 30 min)
//       Round 3 → Round 4 (Final pitch, 15 min)
//       Round 4 → COMPLETION (top 3 by votes + ShowEnded push)
//    C) Idempotent — each phase only fires once per show.
//
//  MVP: single region (IST). Multi-region (USA/EU/APAC) folds in
//  by repeating this loop with different cron windows + a region
//  field on the show row. Documented in PROGRESS.
// ============================================================

public sealed class PyaarLiveOrchestrator : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<PyaarLiveHub> _hub;
    private readonly ILogger<PyaarLiveOrchestrator> _logger;

    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    // Round timings — keep in one place so future tuning is easy.
    private const int Round1Minutes = 30;   // Icebreaker
    private const int Round2Minutes = 45;   // Free chat
    private const int Round3Minutes = 30;   // Deeper Q's
    private const int Round4Minutes = 15;   // Final pitch

    private string _lastFormedEvent = "";

    public PyaarLiveOrchestrator(
        IServiceScopeFactory scopeFactory,
        IHubContext<PyaarLiveHub> hub,
        ILogger<PyaarLiveOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("PyaarLiveOrchestrator started (cadence: {Sec}s)", Cadence.TotalSeconds);
        try { await Task.Delay(StartupDelay, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "PYAAR LIVE tick failed; will retry"); }
            try { await Task.Delay(Cadence, ct); }
            catch (OperationCanceledException) { break; }
        }
        _logger.LogInformation("PyaarLiveOrchestrator stopping");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var ist    = nowUtc.AddHours(5).AddMinutes(30);

        using var scope = _scopeFactory.CreateScope();
        var mongo    = scope.ServiceProvider.GetRequiredService<MongoService>();
        var postgres = scope.ServiceProvider.GetRequiredService<PostgresProcService>();

        // A) Saturday 8pm IST formation.
        if (ist.DayOfWeek == DayOfWeek.Saturday
            && ist.Hour == 20
            && _lastFormedEvent != ist.Date.ToString("yyyy-MM-dd"))
        {
            var eventDate = ist.Date.ToString("yyyy-MM-dd");
            var scheduledFor = ist.Date.AddHours(20).AddHours(-5).AddMinutes(-30);
            await FormShowAsync(mongo, postgres, eventDate, scheduledFor, ct);
            _lastFormedEvent = eventDate;
        }

        // B) Round advancement for any show whose current round window expired.
        var needAdvance = await mongo.GetPyaarShowsNeedingAdvanceAsync();
        foreach (var show in needAdvance)
        {
            await AdvanceShowAsync(mongo, show, ct);
        }
    }

    // ─── Formation ─────────────────────────────────────────────

    private async Task FormShowAsync(
        MongoService mongo, PostgresProcService postgres,
        string eventDate, DateTime scheduledFor, CancellationToken ct)
    {
        var pool = await mongo.GetPendingPyaarRegistrationsAsync(eventDate);
        if (pool.Count < 2)
        {
            _logger.LogInformation("PYAAR LIVE formation on {Date}: pool too small ({N})", eventDate, pool.Count);
            foreach (var r in pool) await mongo.UpdatePyaarRegistrationStatusAsync(r.Id!, "no_match", null, null);
            return;
        }

        // Cap to 20 participants (10 couples). Shuffle and slice.
        var rng = new Random();
        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        var maxParticipants = MongoService.PyaarCoupleCount * 2;
        var participants = pool.Take(Math.Min(pool.Count - pool.Count % 2, maxParticipants)).ToList();
        var coupleCount  = participants.Count / 2;

        // Create the show first.
        var show = new PyaarShow
        {
            Region        = "IST",
            EventDate     = eventDate,
            ScheduledFor  = scheduledFor,
            Status        = "live",
            CurrentRound  = 1,
            CurrentRoundLabel = "Icebreaker",
            CurrentRoundEndsAt = scheduledFor.AddMinutes(Round1Minutes),
            PrizePool     = 0,
        };
        var savedShow = await mongo.InsertPyaarShowAsync(show);

        // Build couples + look up usernames.
        var couples = new List<PyaarCouple>();
        for (int i = 0; i < participants.Count; i += 2)
        {
            var a = participants[i];
            var b = participants[i + 1];
            string aName = "Voyager", bName = "Voyager";
            try
            {
                if (Guid.TryParse(a.UserId, out var gA))
                    aName = (await postgres.GetUserAuthByIdAsync(gA))?.Username ?? aName;
                if (Guid.TryParse(b.UserId, out var gB))
                    bName = (await postgres.GetUserAuthByIdAsync(gB))?.Username ?? bName;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Username lookup failed during PYAAR LIVE formation");
            }
            var coupleNum = (i / 2) + 1;
            couples.Add(new PyaarCouple
            {
                ShowId        = savedShow.Id!,
                UserAId       = a.UserId, UserAUsername = aName,
                UserBId       = b.UserId, UserBUsername = bName,
                Codename      = $"Couple {coupleNum}",
                CoupleNumber  = coupleNum,
            });
        }
        await mongo.InsertPyaarCouplesAsync(couples);

        // Refresh: get inserted couples with their IDs.
        var insertedCouples = await mongo.GetCouplesForShowAsync(savedShow.Id!);

        // Mark registrations matched.
        for (int i = 0; i < participants.Count; i++)
        {
            var couple = insertedCouples[i / 2];
            await mongo.UpdatePyaarRegistrationStatusAsync(participants[i].Id!, "matched", savedShow.Id, couple.Id);
        }

        // Anyone left over → no_match.
        for (int i = participants.Count; i < pool.Count; i++)
        {
            await mongo.UpdatePyaarRegistrationStatusAsync(pool[i].Id!, "no_match", null, null);
        }

        _logger.LogInformation(
            "PYAAR LIVE show {ShowId} formed: {Couples} couples on {Date}",
            savedShow.Id, coupleCount, eventDate);

        // Broadcast ShowStarted to everyone.
        await _hub.Clients.All.SendAsync("ShowStarted", new
        {
            showId        = savedShow.Id,
            eventDate     = savedShow.EventDate,
            scheduledFor  = savedShow.ScheduledFor,
            currentRound  = savedShow.CurrentRound,
            currentRoundLabel = savedShow.CurrentRoundLabel,
            currentRoundEndsAt = savedShow.CurrentRoundEndsAt,
            coupleCount   = coupleCount,
        }, ct);
    }

    // ─── Round advancement ─────────────────────────────────────

    private async Task AdvanceShowAsync(MongoService mongo, PyaarShow show, CancellationToken ct)
    {
        switch (show.CurrentRound)
        {
            case 1:
                // → Round 2 (Free chat, 45 min)
                var r2EndsAt = DateTime.UtcNow.AddMinutes(Round2Minutes);
                await mongo.UpdatePyaarShowRoundAsync(show.Id!, 2, "Free chat", r2EndsAt, "live");
                await PushRoundAdvancedAsync(show.Id!, 2, "Free chat", r2EndsAt, ct);
                break;

            case 2:
                // Mid-show elimination then → Round 3.
                await EliminateBottomAsync(mongo, show, ct);
                var r3EndsAt = DateTime.UtcNow.AddMinutes(Round3Minutes);
                await mongo.UpdatePyaarShowRoundAsync(show.Id!, 3, "Deeper questions", r3EndsAt, "live");
                await PushRoundAdvancedAsync(show.Id!, 3, "Deeper questions", r3EndsAt, ct);
                break;

            case 3:
                // → Round 4 (Final pitch, 15 min)
                var r4EndsAt = DateTime.UtcNow.AddMinutes(Round4Minutes);
                await mongo.UpdatePyaarShowRoundAsync(show.Id!, 4, "Final pitch", r4EndsAt, "live");
                await PushRoundAdvancedAsync(show.Id!, 4, "Final pitch", r4EndsAt, ct);
                break;

            case 4:
                // → Completion + winners
                await CompleteShowAsync(mongo, show, ct);
                break;
        }
    }

    private async Task EliminateBottomAsync(MongoService mongo, PyaarShow show, CancellationToken ct)
    {
        var couples = await mongo.GetCouplesForShowAsync(show.Id!);
        // Bottom 3 by vote count; ties broken by higher coupleNumber (arbitrary stable).
        var ranked = couples
            .Where(c => c.EliminatedAt is null)
            .OrderBy(c => c.VoteCount)
            .ThenByDescending(c => c.CoupleNumber)
            .ToList();
        var kicked = ranked.Take(MongoService.PyaarEliminationCount).Select(c => c.Id!).ToList();
        if (kicked.Count > 0)
        {
            await mongo.EliminatePyaarCouplesAsync(kicked, 2);
            await mongo.SetPyaarShowEliminationAsync(show.Id!, kicked);
        }
        await _hub.Clients.All.SendAsync("EliminationAnnounced", new
        {
            showId             = show.Id,
            eliminatedCoupleIds = kicked,
        }, ct);
    }

    private async Task CompleteShowAsync(MongoService mongo, PyaarShow show, CancellationToken ct)
    {
        var couples = await mongo.GetCouplesForShowAsync(show.Id!);
        // Top 3 from couples NOT eliminated, by vote count desc; ties → lower coupleNumber wins.
        var contenders = couples
            .Where(c => c.EliminatedAt is null)
            .OrderByDescending(c => c.VoteCount)
            .ThenBy(c => c.CoupleNumber)
            .Take(MongoService.PyaarWinnerCount)
            .ToList();
        await mongo.SetPyaarShowWinnersAsync(show.Id!, contenders.Select(c => c.Id!).ToList());
        await mongo.SetPyaarCoupleRanksAsync(contenders.Select((c, i) => (c.Id!, i + 1)));
        await mongo.UpdatePyaarShowRoundAsync(show.Id!, 5, "Wrapped", null, "completed");

        await _hub.Clients.All.SendAsync("ShowEnded", new
        {
            showId       = show.Id,
            winners      = contenders.Select((c, i) => new
            {
                coupleId   = c.Id,
                codename   = c.Codename,
                rank       = i + 1,
                voteCount  = c.VoteCount,
            }).ToList(),
        }, ct);
    }

    private async Task PushRoundAdvancedAsync(string showId, int round, string label, DateTime endsAt, CancellationToken ct)
    {
        await _hub.Clients.All.SendAsync("RoundAdvanced", new
        {
            showId,
            currentRound       = round,
            currentRoundLabel  = label,
            currentRoundEndsAt = endsAt,
        }, ct);
    }
}
