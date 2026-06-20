using ChatVerse.API.Hubs;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Services;

// ============================================================
//  LoveTriangleService — weekly Sunday 10pm IST lifecycle.
//
//  Three responsibilities, all idempotent:
//    1. FORM triangles at Sunday 10pm IST. Shuffle pending pool,
//       chunk into triples, insert triangles, push TriangleFormed.
//       Pool size that isn't a multiple of 3 leaves 1 or 2 as
//       "no_match" — they can come back next week.
//    2. OPEN VOTING at +7 days (next Sunday). Flip status active→
//       voting + push VotingOpened to all 3 members + push to a
//       global "love-triangle:public" group for spectators.
//    3. COMPLETE at +8 days. Resolve winning pair + flip to
//       completed + push TriangleCompleted.
//
//  Cadence: 5 minutes — fine enough that all three time anchors
//  fire within 5 min of their target, light enough to not hammer
//  Mongo on quiet weeks.
// ============================================================

public sealed class LoveTriangleService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<LoveTriangleHub> _hub;
    private readonly ILogger<LoveTriangleService> _logger;

    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private string _lastFormedWeek = "";

    public LoveTriangleService(
        IServiceScopeFactory scopeFactory,
        IHubContext<LoveTriangleHub> hub,
        ILogger<LoveTriangleService> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "LoveTriangleService started (cadence: {Mins} min)",
            Cadence.TotalMinutes);

        try { await Task.Delay(StartupDelay, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Love Triangle tick failed; will retry");
            }
            try { await Task.Delay(Cadence, ct); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("LoveTriangleService stopping");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var ist    = nowUtc.AddHours(5).AddMinutes(30);

        using var scope = _scopeFactory.CreateScope();
        var mongo    = scope.ServiceProvider.GetRequiredService<MongoService>();
        var postgres = scope.ServiceProvider.GetRequiredService<PostgresProcService>();

        // 1) FORMATION at Sunday 10pm IST.
        if (ist.DayOfWeek == DayOfWeek.Sunday
            && ist.Hour == 22
            && _lastFormedWeek != ist.Date.ToString("yyyy-MM-dd"))
        {
            var weekStart    = ist.Date.ToString("yyyy-MM-dd");
            var scheduledFor = ist.Date.AddHours(22).AddHours(-5).AddMinutes(-30); // → UTC
            await FormTrianglesAsync(mongo, postgres, weekStart, scheduledFor, ct);
            _lastFormedWeek = weekStart;
        }

        // 2) OPEN VOTING — triangles whose ChatEndsAt has passed.
        var votingDue = await mongo.GetTrianglesReadyForVotingAsync();
        foreach (var t in votingDue)
        {
            var opened = await mongo.OpenLoveTriangleVotingAsync(t.Id!);
            if (opened)
            {
                await PushToTriangleAsync(t, "VotingOpened", new
                {
                    triangleId   = t.Id,
                    votingEndsAt = t.VotingEndsAt,
                }, ct);
            }
        }

        // 3) COMPLETE — triangles whose VotingEndsAt has passed.
        var completionDue = await mongo.GetTrianglesReadyForCompletionAsync();
        foreach (var t in completionDue)
        {
            var done = await mongo.CompleteLoveTriangleAsync(t.Id!);
            if (done is not null && done.Status == "completed")
            {
                await PushToTriangleAsync(done, "TriangleCompleted", new
                {
                    triangleId  = done.Id,
                    winningPair = done.WinningPair,
                    completedAt = done.CompletedAt,
                }, ct);
            }
        }
    }

    private async Task FormTrianglesAsync(
        MongoService mongo, PostgresProcService postgres,
        string weekStart, DateTime scheduledFor, CancellationToken ct)
    {
        var pool = await mongo.GetPendingLoveTriangleRegistrationsAsync(weekStart);
        if (pool.Count < 3)
        {
            _logger.LogInformation(
                "LoveTriangle formation on {Week}: pool too small ({N})",
                weekStart, pool.Count);
            foreach (var r in pool) await mongo.MarkLoveTriangleRegistrationAssignedAsync(r.Id!, "", "no_match");
            return;
        }

        // Fisher-Yates shuffle.
        var rng = new Random();
        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        var chatEndsAt   = scheduledFor.AddDays(MongoService.LoveTriangleChatDays);
        var votingEndsAt = chatEndsAt.AddDays(MongoService.LoveTriangleVotingDays);

        int triangles = 0;
        for (int i = 0; i + 2 < pool.Count; i += 3)
        {
            var ra = pool[i];
            var rb = pool[i + 1];
            var rc = pool[i + 2];

            string aName = "Voyager", bName = "Voyager", cName = "Voyager";
            try
            {
                if (Guid.TryParse(ra.UserId, out var gA))
                    aName = (await postgres.GetUserAuthByIdAsync(gA))?.Username ?? aName;
                if (Guid.TryParse(rb.UserId, out var gB))
                    bName = (await postgres.GetUserAuthByIdAsync(gB))?.Username ?? bName;
                if (Guid.TryParse(rc.UserId, out var gC))
                    cName = (await postgres.GetUserAuthByIdAsync(gC))?.Username ?? cName;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Username lookup failed during love-triangle formation");
            }

            var t = new LoveTriangle
            {
                UserAId       = ra.UserId, UserAUsername = aName,
                UserBId       = rb.UserId, UserBUsername = bName,
                UserCId       = rc.UserId, UserCUsername = cName,
                WeekStart     = weekStart,
                ScheduledFor  = scheduledFor,
                ChatEndsAt    = chatEndsAt,
                VotingEndsAt  = votingEndsAt,
                Status        = "active",
            };
            var saved = await mongo.InsertLoveTriangleAsync(t);

            await mongo.MarkLoveTriangleRegistrationAssignedAsync(ra.Id!, saved.Id!, "matched");
            await mongo.MarkLoveTriangleRegistrationAssignedAsync(rb.Id!, saved.Id!, "matched");
            await mongo.MarkLoveTriangleRegistrationAssignedAsync(rc.Id!, saved.Id!, "matched");

            await PushToTriangleAsync(saved, "TriangleFormed", new
            {
                triangleId   = saved.Id,
                weekStart    = saved.WeekStart,
                scheduledFor = saved.ScheduledFor,
                chatEndsAt   = saved.ChatEndsAt,
            }, ct);

            triangles++;
        }

        // Leftover 1 or 2 → no_match.
        for (int leftover = pool.Count - (pool.Count % 3); leftover < pool.Count; leftover++)
        {
            await mongo.MarkLoveTriangleRegistrationAssignedAsync(
                pool[leftover].Id!, "", "no_match");
        }

        _logger.LogInformation(
            "LoveTriangle formation on {Week}: {Triangles} triangles from {Pool} registrations",
            weekStart, triangles, pool.Count);
    }

    private async Task PushToTriangleAsync(
        LoveTriangle t, string eventName, object payload, CancellationToken ct)
    {
        await _hub.Clients.User(t.UserAId).SendAsync(eventName, payload, ct);
        await _hub.Clients.User(t.UserBId).SendAsync(eventName, payload, ct);
        await _hub.Clients.User(t.UserCId).SendAsync(eventName, payload, ct);
    }
}
