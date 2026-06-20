using ChatVerse.API.Hubs;
using ChatVerse.API.Services.Cipher;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Services;

// ============================================================
//  CipherRoundService — weekly lifecycle for The Cipher.
//
//  Three idempotent responsibilities:
//    1. OPEN at Monday 9am IST. Pick a phrase via deterministic
//       per-week-label hash. Pick N Cipher Members from a random
//       active-user sample where N = phrase word count. Assign
//       one word per Member. Insert round + members + push
//       CipherRoundStarted to everyone, CipherFragmentAssigned
//       to picked Members.
//    2. CLOSE rounds at Sunday 11pm IST (or whenever EndsAt
//       passes). Score every submission (Jaccard phrase sim +
//       member-set recall). Flag ≥ 50% as winners. Update Member
//       was_identified counts. Push CipherRoundClosed to all
//       participants.
//
//  Cadence: 5 minutes. Both anchors fire within 5 min of target.
// ============================================================

public sealed class CipherRoundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<CipherHub> _hub;
    private readonly ILogger<CipherRoundService> _logger;

    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
    private const int MemberPoolSampleSize = 200;

    private string _lastOpenedWeek = "";

    public CipherRoundService(
        IServiceScopeFactory scopeFactory,
        IHubContext<CipherHub> hub,
        ILogger<CipherRoundService> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("CipherRoundService started (cadence: {Mins} min)", Cadence.TotalMinutes);
        try { await Task.Delay(StartupDelay, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cipher tick failed; will retry");
            }
            try { await Task.Delay(Cadence, ct); }
            catch (OperationCanceledException) { break; }
        }
        _logger.LogInformation("CipherRoundService stopping");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var ist    = nowUtc.AddHours(5).AddMinutes(30);

        using var scope = _scopeFactory.CreateScope();
        var mongo    = scope.ServiceProvider.GetRequiredService<MongoService>();
        var postgres = scope.ServiceProvider.GetRequiredService<PostgresProcService>();

        // 1) OPEN at Monday 9am IST.
        if (ist.DayOfWeek == DayOfWeek.Monday
            && ist.Hour == 9
            && _lastOpenedWeek != WeekLabelFor(ist))
        {
            var weekLabel  = WeekLabelFor(ist);
            // Only open if no active round already (idempotency hardening).
            var existing = await mongo.GetActiveCipherRoundAsync();
            if (existing is null)
            {
                await OpenRoundAsync(mongo, postgres, weekLabel, ist, ct);
            }
            _lastOpenedWeek = weekLabel;
        }

        // 2) CLOSE rounds whose end window has passed.
        var ready = await mongo.GetCipherRoundsReadyToCloseAsync();
        foreach (var round in ready)
        {
            await CloseRoundAsync(mongo, round, ct);
        }
    }

    // ─── Opening ───────────────────────────────────────────────

    private async Task OpenRoundAsync(
        MongoService mongo, PostgresProcService postgres,
        string weekLabel, DateTime istNow, CancellationToken ct)
    {
        var phrase = CipherPhraseBank.PickForWeek(weekLabel);
        var words  = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var memberCount = words.Length;        // one Member per word

        // Pick Members from a random active-user sample. Smaller than
        // 5% for the MVP — keeps the puzzle tight (8 Members vs hundreds).
        var pool = await postgres.GetRandomActiveUserSampleAsync(MemberPoolSampleSize);
        if (pool.Count < memberCount)
        {
            _logger.LogWarning("Cipher: pool too small ({N}) to form {M} Members — skipping week {W}",
                pool.Count, memberCount, weekLabel);
            return;
        }

        var rng = new Random();
        // Fisher-Yates partial shuffle just for the first memberCount slots.
        for (int i = 0; i < memberCount; i++)
        {
            int j = i + rng.Next(pool.Count - i);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        var picked = pool.Take(memberCount).ToList();

        var startsAtUtc = istNow.AddHours(-5).AddMinutes(-30);                  // Monday 9am IST → UTC
        var endsAtIst   = istNow.Date.AddDays(6).AddHours(23);                  // following Sunday 11pm IST
        var endsAtUtc   = endsAtIst.AddHours(-5).AddMinutes(-30);

        var round = new CipherRound
        {
            WeekLabel  = weekLabel,
            Phrase     = phrase,
            PhraseHash = CipherPhraseBank.HashPhrase(phrase),
            Status     = "active",
            StartsAt   = startsAtUtc,
            EndsAt     = endsAtUtc,
            PrizePool  = 0,
        };
        var savedRound = await mongo.InsertCipherRoundAsync(round);

        var members = new List<CipherMember>();
        for (int i = 0; i < memberCount; i++)
        {
            var u = picked[i];
            members.Add(new CipherMember
            {
                RoundId          = savedRound.Id!,
                UserId           = u.UserId.ToString(),
                Username         = u.Username,
                AssignedFragment = words[i],
            });
        }
        await mongo.InsertCipherMembersAsync(members);

        _logger.LogInformation(
            "Cipher round opened: week={Week} phrase=\"{Phrase}\" members={N}",
            weekLabel, phrase, memberCount);

        // Public push: just the round metadata (NOT the phrase).
        var publicPayload = new
        {
            roundId   = savedRound.Id,
            weekLabel = savedRound.WeekLabel,
            startsAt  = savedRound.StartsAt,
            endsAt    = savedRound.EndsAt,
            phraseLength = memberCount,
        };
        await _hub.Clients.All.SendAsync("CipherRoundStarted", publicPayload, ct);

        // Per-member push: each gets their fragment privately.
        foreach (var m in members)
        {
            await _hub.Clients.User(m.UserId).SendAsync("CipherFragmentAssigned", new
            {
                roundId          = savedRound.Id,
                weekLabel        = savedRound.WeekLabel,
                roundEndsAt      = savedRound.EndsAt,
                assignedFragment = m.AssignedFragment,
            }, ct);
        }
    }

    // ─── Closing ───────────────────────────────────────────────

    private async Task CloseRoundAsync(
        MongoService mongo, CipherRound round, CancellationToken ct)
    {
        var members = await mongo.GetCipherMembersAsync(round.Id!);
        var memberIds = members.Select(m => m.UserId).ToHashSet();
        var submissions = await mongo.GetCipherSubmissionsForRoundAsync(round.Id!);

        var scored = new List<(string Id, int Accuracy, bool Won)>();
        var correctGuessersByMember = members.ToDictionary(m => m.Id!, _ => 0);
        var winningHunterIds = new List<string>();

        foreach (var sub in submissions)
        {
            var phraseSim = CipherPhraseBank.PhraseSimilarity(sub.GuessedPhrase, round.Phrase);
            var memberOverlap = CipherPhraseBank.MemberSetOverlap(sub.NamedUserIds, memberIds);
            // Weighted: phrase weighs slightly more than naming.
            var accuracy = (int)Math.Round(phraseSim * 0.6 + memberOverlap * 0.4);
            var won = accuracy >= MongoService.CipherWinningAccuracyThreshold;

            scored.Add((sub.Id!, accuracy, won));
            if (won) winningHunterIds.Add(sub.HunterUserId);

            // Per-member identification counts.
            foreach (var named in sub.NamedUserIds)
            {
                var member = members.FirstOrDefault(m => m.UserId == named);
                if (member is not null)
                {
                    correctGuessersByMember[member.Id!] += 1;
                }
            }
        }

        await mongo.BulkUpdateCipherSubmissionScoresAsync(scored);
        await mongo.BulkUpdateCipherMemberScoresAsync(correctGuessersByMember);
        await mongo.CloseCipherRoundAsync(round.Id!, winningHunterIds);

        _logger.LogInformation(
            "Cipher round closed: week={Week} submissions={Subs} winners={Wins}",
            round.WeekLabel, submissions.Count, winningHunterIds.Count);

        // Public closure push — reveals phrase + member usernames now
        // that the round is over.
        var closurePayload = new
        {
            roundId         = round.Id,
            weekLabel       = round.WeekLabel,
            phrase          = round.Phrase,
            memberUsernames = members.Select(m => m.Username).ToList(),
            winningHunters  = winningHunterIds.Count,
            closedAt        = DateTime.UtcNow,
        };
        await _hub.Clients.All.SendAsync("CipherRoundClosed", closurePayload, ct);
    }

    // ─── Helpers ───────────────────────────────────────────────

    /// <summary>"YYYY-Www" ISO-week label for the IST date passed in.</summary>
    private static string WeekLabelFor(DateTime ist)
    {
        var iso = System.Globalization.ISOWeek.GetWeekOfYear(ist);
        var isoYear = System.Globalization.ISOWeek.GetYear(ist);
        return $"{isoYear}-W{iso:D2}";
    }
}
