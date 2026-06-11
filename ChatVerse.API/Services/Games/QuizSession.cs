using ChatVerse.API.Models.Games;
using ChatVerse.Domain.Constants;
using ChatVerse.Infrastructure.Persistence.Redis;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatVerse.API.Services.Games;

// ============================================================
//  QuizSession — multiplayer trivia state machine.
//
//  Single source of truth for one quiz room. Owns the full
//  lifecycle (Lobby → Playing → Ended), holds participant +
//  answer + score state, validates submissions, computes scores
//  with a speed bonus, and emits typed GameEvents back to the
//  hub/ticker for broadcast.
//
//  Why Redis-backed instead of memory-backed?
//    Render dynos can restart at any time (free tier sleeps after
//    15 min inactivity, redeploys happen daily). A pure memory
//    state machine would wipe every in-flight quiz on restart —
//    awful UX. Persisting after every state change means the
//    very worst case is "30s of replay needed", not "game lost".
//
//  Concurrency model:
//    All mutating methods acquire _lock (SemaphoreSlim). Reads
//    that aren't on the hot path acquire it too — the cost is
//    negligible at the scale we care about (a single quiz has
//    ≤ 8 players + maybe 50 spectators, all submitting at most
//    once per 15s).
//
//  Scoring formula (per question):
//    base       = 100 if correct, else 0
//    speedBonus = 100 × max(0, 1 − responseMs / (timeoutMs × 0.9))
//    total      = base + speedBonus, max 200
//
//  Speed bonus tops out fast (zero by ~90% of the timer) so
//  beating the buzzer doesn't dominate over being right.
// ============================================================

public sealed class QuizSession : IGameSession
{
    private readonly RedisService _redis;
    private readonly QuizQuestionProvider _questions;
    private readonly ILogger<QuizSession> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Backing state. Hydrated from Redis on InitializeAsync, persisted
    // back on every mutation. We keep a strongly-typed snapshot in
    // memory so hot reads (e.g. participant count, status) don't pay
    // a Redis round-trip cost.
    private QuizPersistedState _state = null!;
    private QuizRoomMeta _meta = null!;
    private bool _initialised;

    // Per-call event queue. Mutating methods append to this and the
    // hub drains it on return — keeps event emission off the lock's
    // hot path. The ticker accesses it via TickAsync's return value.
    // ConcurrentQueue not strictly required (we always emit under
    // the lock), but cheap insurance.
    private readonly ConcurrentQueue<GameEvent> _pendingEvents = new();

    public QuizSession(
        string slug,
        QuizRoomMeta meta,
        RedisService redis,
        QuizQuestionProvider questions,
        ILogger<QuizSession> logger)
    {
        Slug = slug;
        _meta = meta;
        _redis = redis;
        _questions = questions;
        _logger = logger;
    }

    public string Slug { get; }
    public GameType Type => GameType.Quiz;
    public GameStatus Status => _state?.Status ?? GameStatus.Lobby;
    public string HostUserId => _meta.HostUserId;
    public int PlayerCount => _state?.Participants.Count(p => p.Role == GameRole.Player) ?? 0;
    public int SpectatorCount => _state?.Participants.Count(p => p.Role == GameRole.Spectator) ?? 0;

    /// <summary>In-memory activity stamp — see IGameSession docs. Updated
    /// on every state persist + chat append; drives ticker idle close.</summary>
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;

    // ───────────────────────────────────────────────────────────────
    //  Hydration
    // ───────────────────────────────────────────────────────────────

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_initialised) return;

            var raw = await _redis.GetStringAsync(RedisKeys.GameRoomState(Slug));
            if (!string.IsNullOrEmpty(raw))
            {
                try
                {
                    _state = JsonSerializer.Deserialize<QuizPersistedState>(raw, JsonOpts)
                             ?? FreshState();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Quiz state for {Slug} was corrupt, starting fresh", Slug);
                    _state = FreshState();
                }
            }
            else
            {
                _state = FreshState();
                await PersistStateUnsafeAsync();
            }
            _initialised = true;
        }
        finally { _lock.Release(); }
    }

    private QuizPersistedState FreshState() => new()
    {
        Slug = Slug,
        Status = GameStatus.Lobby,
        Settings = _meta.Settings,
        Questions = new(),
        CurrentIndex = -1,
        CurrentDeadlineUtc = null,
        Answers = new(),
        Scores = new(),
        Participants = new(),
        ChatTail = new(),
        StartedAtUtc = null,
    };

    // ───────────────────────────────────────────────────────────────
    //  Participant management
    // ───────────────────────────────────────────────────────────────

    public async Task<JoinResult> JoinAsync(
        string userId, string username, GameRole requestedRole, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            // Already in the room? Return their existing role. This is
            // important for refresh / reconnect — we don't want a refresh
            // to bump them from Player to Spectator (or vice-versa).
            var existing = _state.Participants.FirstOrDefault(p => p.UserId == userId);
            if (existing is not null)
            {
                existing.IsOnline = true;
                await PersistStateUnsafeAsync();
                return new JoinResult(true, existing.Role);
            }

            // Block joins once the game has ended — read-only mode.
            if (_state.Status == GameStatus.Ended)
                return new JoinResult(false, requestedRole, "Game has ended.");

            // Player cap check. Overflow falls back to Spectator
            // (we keep them in the room rather than rejecting) unless
            // they explicitly asked for Spectator anyway.
            var assignedRole = requestedRole;
            if (requestedRole == GameRole.Player &&
                PlayerCount >= _meta.Settings.MaxPlayers)
            {
                assignedRole = GameRole.Spectator;
            }

            // Block new Player joins mid-game — keeps scoring fair.
            // Spectators can join any time.
            if (assignedRole == GameRole.Player && _state.Status == GameStatus.Playing)
                assignedRole = GameRole.Spectator;

            var participant = new ParticipantState
            {
                UserId = userId,
                Username = username,
                Role = assignedRole,
                IsHost = userId == _meta.HostUserId,
                IsOnline = true,
                JoinedAtUtc = DateTime.UtcNow,
            };
            _state.Participants.Add(participant);

            // Initialise score row for new players so the scoreboard
            // shows them at 0 immediately (not "missing").
            if (assignedRole == GameRole.Player && !_state.Scores.ContainsKey(userId))
            {
                _state.Scores[userId] = new ScoreState
                {
                    UserId = userId,
                    Username = username,
                    Score = 0,
                    Correct = 0,
                    Answered = 0,
                    TotalResponseMs = 0,
                };
            }

            await PersistStateUnsafeAsync();
            await TrackRoomMembershipAsync(userId, assignedRole, joining: true);

            _pendingEvents.Enqueue(new ParticipantJoinedEvent(
                new GameParticipant(userId, username, assignedRole, participant.IsHost, true)));

            var reasonNote = assignedRole != requestedRole
                ? "Players full; joined as spectator."
                : null;
            return new JoinResult(true, assignedRole, reasonNote);
        }
        finally { _lock.Release(); }
    }

    public async Task LeaveAsync(string userId, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            var p = _state.Participants.FirstOrDefault(x => x.UserId == userId);
            if (p is null) return;

            _state.Participants.Remove(p);
            await PersistStateUnsafeAsync();
            await TrackRoomMembershipAsync(userId, p.Role, joining: false);

            _pendingEvents.Enqueue(new ParticipantLeftEvent(userId));

            // If host left mid-game, end the session. Avoids stranded
            // rooms where everyone's waiting on a host that won't return.
            if (p.IsHost && _state.Status == GameStatus.Playing)
            {
                await EndGameUnsafeAsync("Host left the room.");
            }
        }
        finally { _lock.Release(); }
    }

    // ───────────────────────────────────────────────────────────────
    //  Game start
    // ───────────────────────────────────────────────────────────────

    public async Task<ActionResult> StartAsync(string requestingUserId, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            if (requestingUserId != _meta.HostUserId)
                return new ActionResult(false, "Only the host can start the quiz.");
            if (_state.Status != GameStatus.Lobby)
                return new ActionResult(false, "Quiz already in progress.");
            if (PlayerCount < 1)
                return new ActionResult(false, "Need at least one player.");

            // Fetch questions outside the lock? No — initialisation only
            // happens once per game, and FetchAsync is cancellable. Doing
            // it under the lock prevents two concurrent Start calls from
            // each fetching their own batch.
            _state.Questions = await _questions.FetchAsync(
                _meta.Settings.Category,
                _meta.Settings.Difficulty,
                _meta.Settings.QuestionCount,
                ct);

            if (_state.Questions.Count == 0)
                return new ActionResult(false, "Could not load any questions.");

            _state.Status = GameStatus.Playing;
            _state.StartedAtUtc = DateTime.UtcNow;
            _state.CurrentIndex = 0;
            _state.CurrentDeadlineUtc = DateTime.UtcNow.AddSeconds(_meta.Settings.SecondsPerQuestion);

            await PersistStateUnsafeAsync();

            _pendingEvents.Enqueue(new QuestionPushedEvent(BuildPublicQuestionUnsafe()));
            return new ActionResult(true);
        }
        finally { _lock.Release(); }
    }

    // ───────────────────────────────────────────────────────────────
    //  Submit answer
    // ───────────────────────────────────────────────────────────────

    public async Task<ActionResult> SubmitMoveAsync(
        string userId, JsonElement payload, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            if (_state.Status != GameStatus.Playing)
                return new ActionResult(false, "Quiz is not in progress.");

            var player = _state.Participants.FirstOrDefault(p => p.UserId == userId);
            if (player is null || player.Role != GameRole.Player)
                return new ActionResult(false, "Only players can submit answers.");

            // Payload validation — be defensive, clients are untrusted.
            QuizAnswerSubmit? submit = null;
            try { submit = payload.Deserialize<QuizAnswerSubmit>(JsonOpts); }
            catch { /* fall through */ }
            if (submit is null || string.IsNullOrEmpty(submit.QuestionId))
                return new ActionResult(false, "Invalid submission.");

            var currentQ = _state.Questions[_state.CurrentIndex];
            if (submit.QuestionId != currentQ.Id)
                return new ActionResult(false, "Stale question id.");

            if (DateTime.UtcNow > _state.CurrentDeadlineUtc)
                return new ActionResult(false, "Deadline passed.");

            if (submit.ChoiceIndex < 0 || submit.ChoiceIndex >= currentQ.Options.Count)
                return new ActionResult(false, "Choice index out of range.");

            // Already answered? Reject (single submit per Q).
            if (!_state.Answers.TryGetValue(currentQ.Id, out var perQ))
            {
                perQ = new();
                _state.Answers[currentQ.Id] = perQ;
            }
            if (perQ.ContainsKey(userId))
                return new ActionResult(false, "Already answered.");

            // Compute response time. We use the question's deadline as
            // the anchor (not StartedAtUtc) because the deadline is
            // already plant in Redis and survives restarts cleanly.
            var deadline = _state.CurrentDeadlineUtc!.Value;
            var elapsed = TimeSpan.FromSeconds(_meta.Settings.SecondsPerQuestion) - (deadline - DateTime.UtcNow);
            var responseMs = Math.Max(0, (int)elapsed.TotalMilliseconds);

            var correct = submit.ChoiceIndex == currentQ.CorrectIndex;
            var score = correct ? ComputeScore(responseMs) : 0;

            perQ[userId] = new AnswerState
            {
                ChoiceIndex = submit.ChoiceIndex,
                ResponseTimeMs = responseMs,
                IsCorrect = correct,
                AwardedPoints = score,
            };

            // Update aggregate score.
            var sc = _state.Scores[userId];
            sc.Answered++;
            if (correct) sc.Correct++;
            sc.Score += score;
            sc.TotalResponseMs += responseMs;

            await PersistStateUnsafeAsync();

            _pendingEvents.Enqueue(new ScoreUpdatedEvent(BuildScoreboardUnsafe()));

            // If every player has answered, advance immediately —
            // don't keep them watching a dead timer.
            if (AllPlayersAnsweredUnsafe(currentQ.Id))
            {
                await AdvanceUnsafeAsync();
            }
            return new ActionResult(true);
        }
        finally { _lock.Release(); }
    }

    private int ComputeScore(int responseMs)
    {
        const int basePoints = 100;
        var timeoutMs = _meta.Settings.SecondsPerQuestion * 1000;
        var fraction = 1.0 - (double)responseMs / (timeoutMs * 0.9);
        var bonus = (int)Math.Max(0, Math.Round(fraction * 100));
        return basePoints + Math.Min(bonus, 100);
    }

    private bool AllPlayersAnsweredUnsafe(string questionId)
    {
        var players = _state.Participants.Where(p => p.Role == GameRole.Player).ToList();
        if (players.Count == 0) return true;
        if (!_state.Answers.TryGetValue(questionId, out var perQ)) return false;
        return players.All(p => perQ.ContainsKey(p.UserId));
    }

    // ───────────────────────────────────────────────────────────────
    //  Tick — deadline-driven advancement
    // ───────────────────────────────────────────────────────────────

    public async Task<bool> TickAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (!_initialised) return false;
        if (Status != GameStatus.Playing) return false;
        if (_state.CurrentDeadlineUtc is null) return false;
        if (nowUtc < _state.CurrentDeadlineUtc.Value) return false;

        await _lock.WaitAsync(ct);
        try
        {
            // Double-check under the lock — another submit may have
            // already advanced past this question.
            if (_state.Status != GameStatus.Playing) return false;
            if (_state.CurrentDeadlineUtc is null) return false;
            if (nowUtc < _state.CurrentDeadlineUtc.Value) return false;

            await AdvanceUnsafeAsync();
            return true;
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Reveals the current question, then either pushes the next one
    /// or ends the game. Caller must hold _lock.
    /// </summary>
    private async Task AdvanceUnsafeAsync()
    {
        var currentQ = _state.Questions[_state.CurrentIndex];
        var reveal = BuildRevealUnsafe(currentQ);
        _pendingEvents.Enqueue(new QuestionRevealedEvent(reveal, BuildScoreboardUnsafe()));

        // Move to next question or end.
        if (_state.CurrentIndex + 1 >= _state.Questions.Count)
        {
            await EndGameUnsafeAsync("All questions answered.");
            return;
        }

        _state.CurrentIndex++;
        _state.CurrentDeadlineUtc = DateTime.UtcNow.AddSeconds(_meta.Settings.SecondsPerQuestion);
        await PersistStateUnsafeAsync();

        _pendingEvents.Enqueue(new QuestionPushedEvent(BuildPublicQuestionUnsafe()));
    }

    private async Task EndGameUnsafeAsync(string reason)
    {
        _state.Status = GameStatus.Ended;
        _state.CurrentDeadlineUtc = null;
        await PersistStateUnsafeAsync();
        _pendingEvents.Enqueue(new GameEndedEvent(BuildScoreboardUnsafe(), reason));
    }

    // ───────────────────────────────────────────────────────────────
    //  Snapshot for clients (join / reconnect catch-up)
    // ───────────────────────────────────────────────────────────────

    public async Task<GameRoomSnapshot> GetSnapshotAsync(string forUserId, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            var viewer = _state.Participants.FirstOrDefault(p => p.UserId == forUserId);
            var role = viewer?.Role;

            QuizQuestionPublic? currentQ = null;
            if (_state.Status == GameStatus.Playing && _state.CurrentIndex >= 0)
                currentQ = BuildPublicQuestionUnsafe();

            return new GameRoomSnapshot(
                Room: BuildRoomDtoUnsafe(),
                ViewerRole: role,
                CurrentQuestion: currentQ,
                Scoreboard: BuildScoreboardUnsafe(),
                Participants: _state.Participants.Select(p => new GameParticipant(
                    p.UserId, p.Username, p.Role, p.IsHost, p.IsOnline)).ToList(),
                RecentChat: _state.ChatTail.ToList());
        }
        finally { _lock.Release(); }
    }

    // ───────────────────────────────────────────────────────────────
    //  Chat
    // ───────────────────────────────────────────────────────────────

    public async Task<GameChatMessage> AppendChatAsync(
        string senderId, string senderUsername, GameRole senderRole,
        string text, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            // Chat doesn't persist through PersistStateUnsafeAsync (ring
            // buffer key), so stamp activity here explicitly.
            LastActivityUtc = DateTime.UtcNow;
            // Light moderation: trim + length cap. Heavier moderation
            // (NSFW / spam) is handled by the existing ModerationOrchestrator
            // wired into ChatHub — we'll route game chat through it in
            // Phase 2 once we know which throttling profile fits.
            var clean = (text ?? "").Trim();
            if (clean.Length > 280) clean = clean[..280];

            var msg = new GameChatMessage(
                Id: Guid.NewGuid().ToString("N")[..12],
                SenderId: senderId,
                SenderUsername: senderUsername,
                SenderRole: senderRole,
                Text: clean,
                AtUtc: DateTime.UtcNow);

            _state.ChatTail.Add(msg);
            // Keep the tail bounded — recent 100 lines is plenty for
            // reconnect catch-up, and 100 × 280B ≈ 28KB so Redis is fine.
            const int MaxTail = 100;
            if (_state.ChatTail.Count > MaxTail)
                _state.ChatTail.RemoveRange(0, _state.ChatTail.Count - MaxTail);

            await PersistStateUnsafeAsync();
            return msg;
        }
        finally { _lock.Release(); }
    }

    // ───────────────────────────────────────────────────────────────
    //  Helpers
    // ───────────────────────────────────────────────────────────────

    private async Task EnsureInitialisedAsync(CancellationToken ct)
    {
        if (!_initialised) await InitializeAsync(ct);
    }

    private QuizQuestionPublic BuildPublicQuestionUnsafe()
    {
        var q = _state.Questions[_state.CurrentIndex];
        return new QuizQuestionPublic(
            Id: q.Id,
            Category: q.Category,
            Difficulty: q.Difficulty,
            Question: q.Question,
            Options: q.Options,
            QuestionNumber: _state.CurrentIndex + 1,
            TotalQuestions: _state.Questions.Count,
            DeadlineUtc: _state.CurrentDeadlineUtc!.Value);
    }

    private QuizAnswerReveal BuildRevealUnsafe(QuizQuestion q)
    {
        var choices = _state.Answers.TryGetValue(q.Id, out var perQ)
            ? perQ.ToDictionary(
                kv => kv.Key,
                kv => new PlayerChoice(kv.Value.ChoiceIndex, kv.Value.ResponseTimeMs))
            : new Dictionary<string, PlayerChoice>();
        return new QuizAnswerReveal(
            QuestionId: q.Id,
            CorrectIndex: q.CorrectIndex,
            CorrectAnswer: q.Options[q.CorrectIndex],
            ChoicesByPlayer: choices,
            RoundDurationMs: _meta.Settings.SecondsPerQuestion * 1000);
    }

    private List<ScoreEntry> BuildScoreboardUnsafe()
    {
        return _state.Scores.Values
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Correct)
            .Select(s => new ScoreEntry(
                UserId: s.UserId,
                Username: s.Username,
                Score: s.Score,
                CorrectAnswers: s.Correct,
                AnsweredCount: s.Answered,
                AverageResponseMs: s.Answered == 0 ? 0 : s.TotalResponseMs / (double)s.Answered))
            .ToList();
    }

    private GameRoomDto BuildRoomDtoUnsafe() => new(
        Slug: Slug,
        Name: _meta.Name,
        Type: GameType.Quiz,
        Status: _state.Status,
        PlayerCount: PlayerCount,
        MaxPlayers: _meta.Settings.MaxPlayers,
        SpectatorCount: SpectatorCount,
        HostUsername: _meta.HostUsername,
        CreatedAtUtc: _meta.CreatedAtUtc);

    private async Task PersistStateUnsafeAsync()
    {
        // Every state mutation funnels through here — single choke
        // point for the idle-close activity stamp.
        LastActivityUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(_state, JsonOpts);
        await _redis.SetStringAsync(
            RedisKeys.GameRoomState(Slug), json, RedisTTL.GameRoom);
    }

    private async Task TrackRoomMembershipAsync(string userId, GameRole role, bool joining)
    {
        // We could use Redis sets here for true cross-replica visibility
        // of who's in which room, but for v1 the in-state Participants
        // list is enough — GamingHallPage reads counts off the meta DTO.
        // Stub kept so future "spectator presence ping" can hook in.
        _ = userId; _ = role; _ = joining;
        await Task.CompletedTask;
    }

    /// <summary>
    /// Drain accumulated events. Called by the hub after every Join /
    /// Submit and by the GameTickerService after each tick. Empties
    /// the queue, so each event is broadcast exactly once.
    /// </summary>
    public IReadOnlyList<GameEvent> DrainEvents()
    {
        var drained = new List<GameEvent>();
        while (_pendingEvents.TryDequeue(out var ev)) drained.Add(ev);
        return drained;
    }

    public async ValueTask DisposeAsync()
    {
        _lock.Dispose();
        await Task.CompletedTask;
    }

    // ───────────────────────────────────────────────────────────────
    //  JSON options — shared across (de)serialisation calls
    // ───────────────────────────────────────────────────────────────
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Hub serialises submissions with default (PascalCase) options.
        // Allowing case-insensitive reads lets us deserialise either
        // casing — important for forward-compat too, if a future client
        // sends snake_case from a non-.NET caller.
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // ───────────────────────────────────────────────────────────────
    //  Internal persisted shapes (private to this file — we serialise
    //  these as-is to Redis, so renames need a versioning story
    //  before any future change ships).
    // ───────────────────────────────────────────────────────────────

    public sealed class QuizPersistedState
    {
        public string Slug { get; set; } = "";
        public GameStatus Status { get; set; }
        public QuizSettings Settings { get; set; } = new();
        public List<QuizQuestion> Questions { get; set; } = new();
        public int CurrentIndex { get; set; } = -1;
        public DateTime? CurrentDeadlineUtc { get; set; }
        public Dictionary<string, Dictionary<string, AnswerState>> Answers { get; set; } = new();
        public Dictionary<string, ScoreState> Scores { get; set; } = new();
        public List<ParticipantState> Participants { get; set; } = new();
        public List<GameChatMessage> ChatTail { get; set; } = new();
        public DateTime? StartedAtUtc { get; set; }
    }

    public sealed class ParticipantState
    {
        public string UserId { get; set; } = "";
        public string Username { get; set; } = "";
        public GameRole Role { get; set; }
        public bool IsHost { get; set; }
        public bool IsOnline { get; set; }
        public DateTime JoinedAtUtc { get; set; }
    }

    public sealed class AnswerState
    {
        public int ChoiceIndex { get; set; }
        public int ResponseTimeMs { get; set; }
        public bool IsCorrect { get; set; }
        public int AwardedPoints { get; set; }
    }

    public sealed class ScoreState
    {
        public string UserId { get; set; } = "";
        public string Username { get; set; } = "";
        public int Score { get; set; }
        public int Correct { get; set; }
        public int Answered { get; set; }
        public long TotalResponseMs { get; set; }
    }
}

// ============================================================
//  Meta vs state separation:
//    QuizRoomMeta = immutable-after-creation (settings, host).
//    QuizPersistedState (above) = mutable per-round (questions,
//      participants, scores, chat).
//  Storing them under different keys (RedisKeys.GameRoomMeta vs
//  GameRoomState) means GamingHallPage's list endpoint can read
//  just the meta for every active room without dragging in the
//  whole question bank for each.
// ============================================================

public sealed class QuizRoomMeta
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string HostUserId { get; set; } = "";
    public string HostUsername { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public QuizSettings Settings { get; set; } = new();
    /// <summary>
    /// Game type — selected at create time, persisted with the meta so
    /// the registry can hydrate the right session impl on restart.
    /// Defaults to Quiz for backward compatibility with Phase 1 rooms
    /// that were persisted before this field existed.
    /// </summary>
    public GameType Type { get; set; } = GameType.Quiz;
    /// <summary>
    /// Public rooms show up in the chat's Active Games panel; private
    /// rooms only join via direct URL. Default true (legacy rooms had
    /// no concept of private so treating them as public is correct).
    /// </summary>
    public bool IsPublic { get; set; } = true;
    /// <summary>
    /// Marks the "always-on" random room. Exactly one random room
    /// per (chatSlug, gameType) tuple at any time, tracked via a
    /// pointer key in Redis (see <c>RedisKeys.GameRandomPointer</c>).
    /// </summary>
    public bool IsRandom { get; set; }
    /// <summary>
    /// Parent chat slug. Required for public-room discovery filter.
    /// Null = "global / not chat-bound" (currently created via the
    /// standalone Gaming Hall page, which we still keep alive).
    /// </summary>
    public string? SourceChatSlug { get; set; }
}

public sealed class QuizSettings
{
    public QuizCategory Category { get; set; } = QuizCategory.Any;
    public QuizDifficulty Difficulty { get; set; } = QuizDifficulty.Any;
    public int QuestionCount { get; set; } = 10;
    public int SecondsPerQuestion { get; set; } = 15;
    public int MaxPlayers { get; set; } = 8;
}

// ============================================================
//  GameEvent hierarchy — emitted by sessions, consumed by hub.
//
//  Using a closed sum-type (sealed records on an abstract base)
//  rather than strings: the hub's broadcast switch becomes
//  exhaustive at compile time, so adding a new event type forces
//  every dispatch site to handle it.
// ============================================================

public abstract record GameEvent;

public sealed record QuestionPushedEvent(QuizQuestionPublic Question) : GameEvent;

public sealed record QuestionRevealedEvent(
    QuizAnswerReveal Reveal,
    IReadOnlyList<ScoreEntry> Scoreboard) : GameEvent;

public sealed record ScoreUpdatedEvent(IReadOnlyList<ScoreEntry> Scoreboard) : GameEvent;

public sealed record GameEndedEvent(
    IReadOnlyList<ScoreEntry> FinalScoreboard,
    string Reason) : GameEvent;

public sealed record ParticipantJoinedEvent(GameParticipant Participant) : GameEvent;

public sealed record ParticipantLeftEvent(string UserId) : GameEvent;
