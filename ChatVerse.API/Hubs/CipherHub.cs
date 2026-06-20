using ChatVerse.API.Extensions;
using ChatVerse.API.Services;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Hubs;

// ============================================================
//  CipherHub — The Cipher (weekly community ARG).
//
//  Client methods:
//    • GetCurrentRound()         → public state (week label, ends-at,
//                                  phrase length, member-count public-but-
//                                  -anonymous, my-status)
//    • GetMyFragment()           → if I'm a Member this week, returns
//                                  my single-word fragment. Else null.
//    • SubmitGuess(phrase, namedUserIds[])
//                                → upserts my submission for the
//                                  active round. Last write wins.
//    • GetMySubmission()         → my pending guess for this round
//    • GetLeaderboard(roundId?)  → top scoring Hunters
//    • GetArchive()              → recent closed rounds
//
//  Server-push events:
//    • CipherRoundStarted   — Monday 9am IST fan-out to all users
//    • CipherFragmentAssigned — fanned only to picked Members
//    • CipherRoundClosed    — Sunday 11pm IST fan-out with results
//
//  Privacy:
//    • A Member's fragment NEVER appears in anyone else's hub call.
//    • The submission shape returned to a Hunter shows only their
//      own guess. Other Hunters' guesses are never readable.
//    • Member set isn't revealed until round close.
// ============================================================

[Authorize]
public class CipherHub : Hub
{
    private readonly MongoService _mongo;
    private readonly ILogger<CipherHub> _logger;

    public CipherHub(MongoService mongo, ILogger<CipherHub> logger)
    {
        _mongo = mongo;
        _logger = logger;
    }

    // ─── Read endpoints ────────────────────────────────────────

    public async Task<object> GetCurrentRound()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var round = await _mongo.GetActiveCipherRoundAsync();
        if (round is null)
        {
            return new
            {
                hasActive = false,
            };
        }

        var myMember = await _mongo.GetMyCipherMemberAsync(round.Id!, meId);
        var mySubmission = await _mongo.GetMyCipherSubmissionAsync(round.Id!, meId);

        var phraseWords = round.Phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        return new
        {
            hasActive    = true,
            roundId      = round.Id,
            weekLabel    = round.WeekLabel,
            startsAt     = round.StartsAt,
            endsAt       = round.EndsAt,
            phraseLength = phraseWords,
            iAmMember    = myMember is not null,
            iHaveSubmitted = mySubmission is not null,
            mySubmission = mySubmission is null ? null : new
            {
                guessedPhrase = mySubmission.GuessedPhrase,
                namedUserIds  = mySubmission.NamedUserIds,
                submittedAt   = mySubmission.SubmittedAt,
            },
        };
    }

    public async Task<object> GetMyFragment()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var round = await _mongo.GetActiveCipherRoundAsync();
        if (round is null) return new { hasFragment = false };
        var member = await _mongo.GetMyCipherMemberAsync(round.Id!, meId);
        if (member is null) return new { hasFragment = false };
        return new
        {
            hasFragment      = true,
            weekLabel        = round.WeekLabel,
            roundEndsAt      = round.EndsAt,
            assignedFragment = member.AssignedFragment,
        };
    }

    // ─── Submission ────────────────────────────────────────────

    public async Task<object> SubmitGuess(string phrase, string[] namedUserIds)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var meName = JwtService.GetUsername(Context.User!);

        if (string.IsNullOrWhiteSpace(phrase))
            throw new HubException("Phrase guess can't be empty.");
        if (phrase.Length > 500)
            throw new HubException("Phrase too long.");
        if (namedUserIds is null || namedUserIds.Length == 0)
            throw new HubException("Name at least one suspect.");
        if (namedUserIds.Length > 20)
            throw new HubException("Too many suspects.");

        var round = await _mongo.GetActiveCipherRoundAsync()
            ?? throw new HubException("No active Cipher round right now.");

        // Members can't submit guesses on a round they're playing.
        var member = await _mongo.GetMyCipherMemberAsync(round.Id!, meId);
        if (member is not null)
            throw new HubException("You're a Cipher Member this round — Hunters submit, not Members.");

        var sub = new CipherSubmission
        {
            RoundId        = round.Id!,
            HunterUserId   = meId,
            HunterUsername = meName,
            GuessedPhrase  = phrase.Trim(),
            NamedUserIds   = namedUserIds.Distinct().ToList(),
        };
        var saved = await _mongo.UpsertCipherSubmissionAsync(sub);
        return new
        {
            id            = saved.Id,
            roundId       = saved.RoundId,
            guessedPhrase = saved.GuessedPhrase,
            namedUserIds  = saved.NamedUserIds,
            submittedAt   = saved.SubmittedAt,
        };
    }

    public async Task<object> GetMySubmission()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var round = await _mongo.GetActiveCipherRoundAsync();
        if (round is null) return new { hasSubmission = false };
        var s = await _mongo.GetMyCipherSubmissionAsync(round.Id!, meId);
        if (s is null) return new { hasSubmission = false };
        return new
        {
            hasSubmission = true,
            guessedPhrase = s.GuessedPhrase,
            namedUserIds  = s.NamedUserIds,
            submittedAt   = s.SubmittedAt,
        };
    }

    // ─── Leaderboard + archive ─────────────────────────────────

    public async Task<object> GetLeaderboard(string? roundId = null)
    {
        var entries = await _mongo.GetCipherLeaderboardAsync(roundId);
        return new
        {
            count = entries.Count,
            rows  = entries.Select(s => new
            {
                hunterUsername = s.HunterUsername,
                accuracyPct    = s.AccuracyPct,
                won            = s.WonPrizeShare,
                roundId        = s.RoundId,
                submittedAt    = s.SubmittedAt,
            }).ToList(),
        };
    }

    public async Task<object> GetArchive()
    {
        var rounds = await _mongo.GetCipherArchiveAsync(10);
        var rows = new List<object>();
        foreach (var r in rounds)
        {
            var members = await _mongo.GetCipherMembersAsync(r.Id!);
            rows.Add(new
            {
                id              = r.Id,
                weekLabel       = r.WeekLabel,
                phrase          = r.Phrase,
                closedAt        = r.ClosedAt,
                memberCount     = members.Count,
                memberUsernames = members.Select(m => m.Username).ToList(),
                winningHunters  = r.WinningHunterIds.Count,
            });
        }
        return new { count = rows.Count, rounds = rows };
    }
}
