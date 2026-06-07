using ChatVerse.API.Models.Games;
using ChatVerse.Domain.Constants;
using ChatVerse.Infrastructure.Persistence.Redis;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatVerse.API.Services.Games;

// ============================================================
//  JokesSession — multiplayer "react to dad jokes" game.
//
//  Lifecycle mirrors QuizSession exactly:
//   Lobby   → host configures + invites
//   Playing → bot pushes a joke every N seconds, players react
//             with emoji buttons. Last reaction wins (changeable).
//   Ended   → final laughter board: ranked by laugh-count per joke.
//
//  Why no scoring per player?
//    Jokes is pure vibes — chasing points would push players to
//    react to maximise their score instead of reacting honestly,
//    and the chart becomes meaningless. By design the only winner
//    is "the joke that got the most laughs", not a player.
//
//  Same threading + Redis persistence pattern as QuizSession.
// ============================================================

public sealed class JokesSession : IGameSession
{
    private readonly RedisService _redis;
    private readonly JokesProvider _jokes;
    private readonly ILogger<JokesSession> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private QuizRoomMeta _meta = null!;
    private JokesPersistedState _state = null!;
    private bool _initialised;

    private readonly ConcurrentQueue<GameEvent> _pendingEvents = new();

    public JokesSession(
        string slug,
        QuizRoomMeta meta,
        RedisService redis,
        JokesProvider jokes,
        ILogger<JokesSession> logger)
    {
        Slug = slug;
        _meta = meta;
        _redis = redis;
        _jokes = jokes;
        _logger = logger;
    }

    public string Slug { get; }
    public GameType Type => GameType.Jokes;
    public GameStatus Status => _state?.Status ?? GameStatus.Lobby;
    public string HostUserId => _meta.HostUserId;
    public int PlayerCount => _state?.Participants.Count(p => p.Role == GameRole.Player) ?? 0;
    public int SpectatorCount => _state?.Participants.Count(p => p.Role == GameRole.Spectator) ?? 0;

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
                    _state = JsonSerializer.Deserialize<JokesPersistedState>(raw, JsonOpts)
                             ?? FreshState();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Jokes state for {Slug} corrupt, resetting", Slug);
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

    private JokesPersistedState FreshState() => new()
    {
        Slug = Slug,
        Status = GameStatus.Lobby,
        Settings = _meta.Settings,
        Jokes = new(),
        CurrentIndex = -1,
        CurrentDeadlineUtc = null,
        Reactions = new(),
        Participants = new(),
        ChatTail = new(),
    };

    // ───────────────────────────────────────────────────────────────
    //  Participant management — semantically identical to Quiz
    // ───────────────────────────────────────────────────────────────
    public async Task<JoinResult> JoinAsync(
        string userId, string username, GameRole requestedRole, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            var existing = _state.Participants.FirstOrDefault(p => p.UserId == userId);
            if (existing is not null)
            {
                existing.IsOnline = true;
                await PersistStateUnsafeAsync();
                return new JoinResult(true, existing.Role);
            }

            if (_state.Status == GameStatus.Ended)
                return new JoinResult(false, requestedRole, "Game has ended.");

            var assignedRole = requestedRole;
            if (requestedRole == GameRole.Player && PlayerCount >= _meta.Settings.MaxPlayers)
                assignedRole = GameRole.Spectator;
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

            await PersistStateUnsafeAsync();

            _pendingEvents.Enqueue(new ParticipantJoinedEvent(
                new GameParticipant(userId, username, assignedRole, participant.IsHost, true)));

            var note = assignedRole != requestedRole ? "Players full; joined as spectator." : null;
            return new JoinResult(true, assignedRole, note);
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
            _pendingEvents.Enqueue(new ParticipantLeftEvent(userId));

            if (p.IsHost && _state.Status == GameStatus.Playing)
                await EndGameUnsafeAsync("Host left the room.");
        }
        finally { _lock.Release(); }
    }

    // ───────────────────────────────────────────────────────────────
    //  Game start — load jokes, push first one
    // ───────────────────────────────────────────────────────────────
    public async Task<ActionResult> StartAsync(string requestingUserId, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            if (requestingUserId != _meta.HostUserId)
                return new ActionResult(false, "Only the host can start the game.");
            if (_state.Status != GameStatus.Lobby)
                return new ActionResult(false, "Game already in progress.");
            if (PlayerCount < 1)
                return new ActionResult(false, "Need at least one player.");

            _state.Jokes = await _jokes.FetchAsync(_meta.Settings.QuestionCount, ct);
            if (_state.Jokes.Count == 0)
                return new ActionResult(false, "Could not load any jokes.");

            _state.Status = GameStatus.Playing;
            _state.CurrentIndex = 0;
            _state.CurrentDeadlineUtc = DateTime.UtcNow.AddSeconds(_meta.Settings.SecondsPerQuestion);

            await PersistStateUnsafeAsync();
            _pendingEvents.Enqueue(new JokePushedEvent(BuildJokePushedUnsafe()));
            return new ActionResult(true);
        }
        finally { _lock.Release(); }
    }

    // ───────────────────────────────────────────────────────────────
    //  Submit reaction
    // ───────────────────────────────────────────────────────────────
    public async Task<ActionResult> SubmitMoveAsync(
        string userId, JsonElement payload, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            if (_state.Status != GameStatus.Playing)
                return new ActionResult(false, "Game is not in progress.");

            var player = _state.Participants.FirstOrDefault(p => p.UserId == userId);
            if (player is null || player.Role != GameRole.Player)
                return new ActionResult(false, "Only players can react.");

            JokeReactSubmit? submit = null;
            try { submit = payload.Deserialize<JokeReactSubmit>(JsonOpts); }
            catch { /* fall through */ }
            if (submit is null || string.IsNullOrEmpty(submit.JokeId))
                return new ActionResult(false, "Invalid reaction payload.");

            var currentJoke = _state.Jokes[_state.CurrentIndex];
            if (submit.JokeId != currentJoke.Id)
                return new ActionResult(false, "Stale joke id.");

            if (DateTime.UtcNow > _state.CurrentDeadlineUtc)
                return new ActionResult(false, "Deadline passed.");

            // Last-write-wins for reactions (players can change their mind).
            if (!_state.Reactions.TryGetValue(currentJoke.Id, out var perJoke))
            {
                perJoke = new();
                _state.Reactions[currentJoke.Id] = perJoke;
            }
            perJoke[userId] = submit.Reaction;

            await PersistStateUnsafeAsync();
            _pendingEvents.Enqueue(new ReactionsUpdatedEvent(BuildReactionUpdateUnsafe(currentJoke.Id)));

            return new ActionResult(true);
        }
        finally { _lock.Release(); }
    }

    // ───────────────────────────────────────────────────────────────
    //  Tick — advance on deadline
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
            if (_state.Status != GameStatus.Playing) return false;
            if (_state.CurrentDeadlineUtc is null) return false;
            if (nowUtc < _state.CurrentDeadlineUtc.Value) return false;

            await AdvanceUnsafeAsync();
            return true;
        }
        finally { _lock.Release(); }
    }

    private async Task AdvanceUnsafeAsync()
    {
        var currentJoke = _state.Jokes[_state.CurrentIndex];
        // Reveal: send full counts + the "top reaction" for the bar chart.
        var counts = CountReactionsUnsafe(currentJoke.Id);
        var topReaction = counts.OrderByDescending(kv => kv.Value).FirstOrDefault().Key;
        _pendingEvents.Enqueue(new JokeRevealedEvent(new JokeRevealed(
            JokeId: currentJoke.Id,
            Text: currentJoke.Text,
            Counts: counts,
            TopReaction: topReaction)));

        if (_state.CurrentIndex + 1 >= _state.Jokes.Count)
        {
            await EndGameUnsafeAsync("All jokes delivered.");
            return;
        }

        _state.CurrentIndex++;
        _state.CurrentDeadlineUtc = DateTime.UtcNow.AddSeconds(_meta.Settings.SecondsPerQuestion);
        await PersistStateUnsafeAsync();
        _pendingEvents.Enqueue(new JokePushedEvent(BuildJokePushedUnsafe()));
    }

    private async Task EndGameUnsafeAsync(string reason)
    {
        _state.Status = GameStatus.Ended;
        _state.CurrentDeadlineUtc = null;
        await PersistStateUnsafeAsync();

        // Final stats — ranked by laugh count.
        var finalStats = _state.Jokes
            .Select(j =>
            {
                var counts = CountReactionsUnsafe(j.Id);
                var laughs = counts.GetValueOrDefault(JokeReactionType.Laugh);
                var total = counts.Values.Sum();
                return new JokeFinalStat(j.Id, j.Text, laughs, total);
            })
            .OrderByDescending(s => s.LaughCount)
            .ThenByDescending(s => s.TotalReactions)
            .ToList();
        _pendingEvents.Enqueue(new JokesFinishedEvent(finalStats, reason));
    }

    // ───────────────────────────────────────────────────────────────
    //  Snapshot
    // ───────────────────────────────────────────────────────────────
    public async Task<GameRoomSnapshot> GetSnapshotAsync(string forUserId, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            var viewer = _state.Participants.FirstOrDefault(p => p.UserId == forUserId);
            var role = viewer?.Role;

            // We piggyback on the existing GameRoomSnapshot shape but
            // set CurrentQuestion = null and Scoreboard = []. Jokes-
            // specific live data flows through dedicated events
            // (JokePushed / ReactionsUpdated / JokesFinished).
            return new GameRoomSnapshot(
                Room: BuildRoomDtoUnsafe(),
                ViewerRole: role,
                CurrentQuestion: null,
                Scoreboard: new List<ScoreEntry>(),
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

    private JokePushed BuildJokePushedUnsafe()
    {
        var j = _state.Jokes[_state.CurrentIndex];
        return new JokePushed(
            Id: j.Id,
            Text: j.Text,
            JokeNumber: _state.CurrentIndex + 1,
            TotalJokes: _state.Jokes.Count,
            DeadlineUtc: _state.CurrentDeadlineUtc!.Value);
    }

    private JokeReactionsUpdated BuildReactionUpdateUnsafe(string jokeId)
    {
        var counts = CountReactionsUnsafe(jokeId);
        return new JokeReactionsUpdated(jokeId, counts, counts.Values.Sum());
    }

    private Dictionary<JokeReactionType, int> CountReactionsUnsafe(string jokeId)
    {
        // Initialize all enum keys to zero so the UI bar chart doesn't
        // have to defensively check for missing keys.
        var counts = new Dictionary<JokeReactionType, int>
        {
            [JokeReactionType.Laugh] = 0,
            [JokeReactionType.Meh] = 0,
            [JokeReactionType.Skull] = 0,
            [JokeReactionType.EyeRoll] = 0,
        };
        if (_state.Reactions.TryGetValue(jokeId, out var perJoke))
        {
            foreach (var kv in perJoke)
                counts[kv.Value] = counts.GetValueOrDefault(kv.Value) + 1;
        }
        return counts;
    }

    private GameRoomDto BuildRoomDtoUnsafe() => new(
        Slug: Slug,
        Name: _meta.Name,
        Type: GameType.Jokes,
        Status: _state.Status,
        PlayerCount: PlayerCount,
        MaxPlayers: _meta.Settings.MaxPlayers,
        SpectatorCount: SpectatorCount,
        HostUsername: _meta.HostUsername,
        CreatedAtUtc: _meta.CreatedAtUtc);

    private async Task PersistStateUnsafeAsync()
    {
        var json = JsonSerializer.Serialize(_state, JsonOpts);
        await _redis.SetStringAsync(
            RedisKeys.GameRoomState(Slug), json, RedisTTL.GameRoom);
    }

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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // ───────────────────────────────────────────────────────────────
    //  Persisted state (private to this file)
    // ───────────────────────────────────────────────────────────────
    public sealed class JokesPersistedState
    {
        public string Slug { get; set; } = "";
        public GameStatus Status { get; set; }
        public QuizSettings Settings { get; set; } = new();
        public List<JokeItem> Jokes { get; set; } = new();
        public int CurrentIndex { get; set; } = -1;
        public DateTime? CurrentDeadlineUtc { get; set; }
        // jokeId -> playerId -> reaction. Last write wins.
        public Dictionary<string, Dictionary<string, JokeReactionType>> Reactions { get; set; } = new();
        public List<ParticipantState> Participants { get; set; } = new();
        public List<GameChatMessage> ChatTail { get; set; } = new();
    }

    // Shared participant shape — declared in QuizSession too. We keep
    // a copy here rather than reaching into QuizSession's private nested
    // type because JSON serialisation needs the type to be in scope
    // for this state shape's deserialiser.
    public sealed class ParticipantState
    {
        public string UserId { get; set; } = "";
        public string Username { get; set; } = "";
        public GameRole Role { get; set; }
        public bool IsHost { get; set; }
        public bool IsOnline { get; set; }
        public DateTime JoinedAtUtc { get; set; }
    }
}

// ============================================================
//  Jokes-specific events — extend the shared GameEvent hierarchy.
// ============================================================

public sealed record JokePushedEvent(JokePushed Joke) : GameEvent;

public sealed record ReactionsUpdatedEvent(JokeReactionsUpdated Update) : GameEvent;

public sealed record JokeRevealedEvent(JokeRevealed Reveal) : GameEvent;

public sealed record JokesFinishedEvent(
    IReadOnlyList<JokeFinalStat> FinalStats,
    string Reason) : GameEvent;
