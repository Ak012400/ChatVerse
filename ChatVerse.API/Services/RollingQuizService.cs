using ChatVerse.API.Hubs;
using ChatVerse.API.Models.Games;
using ChatVerse.API.Services.Games;
using ChatVerse.Domain.Constants;
using ChatVerse.Infrastructure.Persistence.Redis;
using Microsoft.AspNetCore.SignalR;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatVerse.API.Services;

// ============================================================
//  RollingQuizService — always-on background quiz that runs in
//  the #general chat room.
//
//  Lifecycle of a round:
//   t = 0      → push question to "general" SignalR group
//   t = ANSWER → players submit via ChatHub.SubmitRollingQuizAnswer
//   t = 90s    → reveal correct answer, refresh leaderboard
//   t = 90+30  → tiny gap, then next question
//
//  Persistence: Redis only. The round's current question +
//  submissions + leaderboard all live there with TTLs lining up
//  with their natural windows. A dyno restart loses at most the
//  in-flight question; the leaderboard survives.
//
//  Per-day session ID rolls at UTC midnight so leaderboards
//  reset organically — no permanent dominance, fresh start
//  every day.
// ============================================================

public sealed class RollingQuizService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<ChatHub> _chatHub;
    private readonly RedisService _redis;
    private readonly ILogger<RollingQuizService> _logger;

    private const string GeneralSlug = "general";
    private const int QuestionWindowSeconds = 90;
    private const int IntermissionSeconds = 30;

    // JsonOpts kept here (private static) so wire shapes are stable
    // even if other modules tweak their own serialiser configs.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public RollingQuizService(
        IServiceScopeFactory scopeFactory,
        IHubContext<ChatHub> chatHub,
        RedisService redis,
        ILogger<RollingQuizService> logger)
    {
        _scopeFactory = scopeFactory;
        _chatHub = chatHub;
        _redis = redis;
        _logger = logger;
    }

    public static string SessionIdForUtc(DateTime utc) => utc.ToString("yyyyMMdd");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "RollingQuizService started ({Window}s round + {Gap}s gap)",
            QuestionWindowSeconds, IntermissionSeconds);

        // Wait through cold-start so the first push doesn't race with
        // the SignalR hub-registered banner.
        try { await Task.Delay(TimeSpan.FromSeconds(90), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunRoundAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rolling quiz round failed; resting before retry");
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("RollingQuizService stopping");
    }

    private async Task RunRoundAsync(CancellationToken ct)
    {
        // Pull a fresh MCQ. We deliberately go for "Any" category so
        // the topic mix in #general stays varied without us building
        // a topic-rotation system on top of OpenTriviaDB's own variety.
        using var scope = _scopeFactory.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<QuizQuestionProvider>();

        var qs = await provider.FetchAsync(QuizCategory.Any, QuizDifficulty.Any, 1, ct);
        if (qs.Count == 0)
        {
            _logger.LogWarning("RollingQuiz couldn't fetch a question; retrying after intermission");
            await Task.Delay(TimeSpan.FromSeconds(IntermissionSeconds), ct);
            return;
        }
        var q = qs[0];
        var deadline = DateTime.UtcNow.AddSeconds(QuestionWindowSeconds);
        var sessionId = SessionIdForUtc(DateTime.UtcNow);

        // Persist the live question + correct answer. The submission
        // handler in ChatHub reads this to validate inputs.
        var current = new RollingQuizState
        {
            Id = q.Id,
            Question = q.Question,
            Options = q.Options.ToList(),
            CorrectIndex = q.CorrectIndex,
            Category = q.Category,
            Difficulty = q.Difficulty,
            DeadlineUtc = deadline,
            SessionId = sessionId,
        };
        await _redis.SetStringAsync(
            RedisKeys.RollingQuizCurrent,
            JsonSerializer.Serialize(current, JsonOpts),
            TimeSpan.FromSeconds(QuestionWindowSeconds + IntermissionSeconds + 60));

        // Push question to clients — clients render the card, players
        // tap an option, ChatHub handles the submission RPC.
        var clientPayload = new RollingQuizQuestion(
            Id: q.Id,
            Category: q.Category,
            Difficulty: q.Difficulty,
            Question: q.Question,
            Options: q.Options,
            DeadlineUtc: deadline,
            SessionId: sessionId);
        await _chatHub.Clients.Group(GeneralSlug).SendAsync(
            "RollingQuizQuestion", clientPayload, ct);

        _logger.LogInformation(
            "RollingQuiz question pushed (sessionId={Session}, id={Id}, cat={Cat})",
            sessionId, q.Id, q.Category);

        // Wait the round, then reveal + push leaderboard.
        try { await Task.Delay(TimeSpan.FromSeconds(QuestionWindowSeconds), ct); }
        catch (OperationCanceledException) { return; }

        await RevealAndScoreAsync(current, ct);

        // Quiet gap before the next round so the reveal animation has
        // breathing room on the client.
        try { await Task.Delay(TimeSpan.FromSeconds(IntermissionSeconds), ct); }
        catch (OperationCanceledException) { return; }
    }

    private async Task RevealAndScoreAsync(RollingQuizState q, CancellationToken ct)
    {
        // Read submissions hash + correct-order list (populated by
        // ChatHub.SubmitRollingQuizAnswer as players tap options).
        var subsKey = RedisKeys.RollingQuizSubmissions(q.Id);
        var orderKey = RedisKeys.RollingQuizCorrectOrder(q.Id);

        var allSubsRaw = await _redis.GetStringAsync(subsKey);
        var allSubs = string.IsNullOrEmpty(allSubsRaw)
            ? new Dictionary<string, RollingQuizSubmission>()
            : JsonSerializer.Deserialize<Dictionary<string, RollingQuizSubmission>>(allSubsRaw, JsonOpts)
              ?? new();

        var totalSubs = allSubs.Count;
        var correctCount = allSubs.Values.Count(s => s.IsCorrect);

        // Reveal — sent before the leaderboard push so the UI can
        // colour options first, then animate score deltas next.
        var reveal = new RollingQuizRevealed(
            QuestionId: q.Id,
            CorrectIndex: q.CorrectIndex,
            CorrectAnswer: q.Options[q.CorrectIndex],
            CorrectAnswerCount: correctCount,
            TotalSubmissionCount: totalSubs);
        await _chatHub.Clients.Group(GeneralSlug).SendAsync(
            "RollingQuizRevealed", reveal, ct);

        // Push the updated top-10 leaderboard. (Per-user "your row"
        // can't be computed in a single broadcast because each user
        // gets a different row; clients filter their own row from the
        // hash they already have, or we can send it on join.)
        var leaderboard = await ComputeLeaderboardAsync(q.SessionId);
        await _chatHub.Clients.Group(GeneralSlug).SendAsync(
            "RollingQuizLeaderboard", leaderboard, ct);

        // Best-effort cleanup of per-question scratch keys. Even if
        // these don't fire (Redis flaked etc.), their TTLs would have
        // expired anyway — set during RunRoundAsync via the current
        // state TTL.
        try { await _redis.DeleteKeyAsync(subsKey); } catch { /* swallow */ }
        try { await _redis.DeleteKeyAsync(orderKey); } catch { /* swallow */ }
    }

    private async Task<RollingQuizLeaderboard> ComputeLeaderboardAsync(string sessionId)
    {
        var statsRaw = await _redis.GetStringAsync(RedisKeys.RollingQuizStats(sessionId));
        var stats = string.IsNullOrEmpty(statsRaw)
            ? new Dictionary<string, RollingQuizUserStats>()
            : JsonSerializer.Deserialize<Dictionary<string, RollingQuizUserStats>>(statsRaw, JsonOpts)
              ?? new();

        var top = stats.Values
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Correct)
            .Take(10)
            .Select(s => new RollingQuizLeaderEntry(
                UserId: s.UserId,
                Username: s.Username,
                Score: s.Score,
                CorrectAnswers: s.Correct,
                TotalAttempts: s.Attempts))
            .ToList();

        return new RollingQuizLeaderboard(
            SessionId: sessionId,
            Top: top,
            YouRow: null); // per-user resolution happens in ChatHub.JoinRoom
    }

    // ─── Persisted state shapes (private to backend) ───────────────
    public sealed class RollingQuizState
    {
        public string Id { get; set; } = "";
        public string Question { get; set; } = "";
        public List<string> Options { get; set; } = new();
        public int CorrectIndex { get; set; }
        public string Category { get; set; } = "";
        public string Difficulty { get; set; } = "";
        public DateTime DeadlineUtc { get; set; }
        public string SessionId { get; set; } = "";
    }

    public sealed class RollingQuizSubmission
    {
        public int ChoiceIndex { get; set; }
        public long AtTicks { get; set; }
        public bool IsCorrect { get; set; }
    }

    public sealed class RollingQuizUserStats
    {
        public string UserId { get; set; } = "";
        public string Username { get; set; } = "";
        public int Score { get; set; }
        public int Correct { get; set; }
        public int Attempts { get; set; }
    }
}
