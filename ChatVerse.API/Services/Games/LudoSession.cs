using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatVerse.API.Models.Games;
using ChatVerse.Domain.Constants;
using ChatVerse.Infrastructure.Persistence.Redis;

namespace ChatVerse.API.Services.Games;

// ============================================================
//  LudoSession — server-authoritative Ludo (Phase 3 Sprint B).
//
//  Rules (locked with Arun 2026-06-11):
//    • 2–4 players; host assigns colour seats (director mode — NO
//      auto-seating, not even the creator).
//    • Classic dice: 6 → extra roll; three consecutive 6s → turn
//      forfeited; a token leaves base only on a 6.
//    • Capture (landing on an unsafe square holding opponent tokens)
//      sends them ALL back to base and grants an extra roll.
//    • 30s turn timer — no roll/move in time → turn auto-skips, so
//      one AFK player can never stall the room ("no lagging").
//
//  Authority model:
//    Unlike chess (client validates via chess.js, server relays),
//    Ludo is fully validated SERVER-side — dice are rolled here,
//    legality is computed here, clients only render. This is the
//    "clean and bug free" guarantee: there is no client state that
//    can drift; every mutation re-broadcasts the full board, which
//    is tiny (16 token ints + seats).
//
//  Position encoding (per token, an int "steps walked"):
//    -1        in base
//     0..50    on the shared 52-square track; absolute square =
//              (startSquare[colour] + steps) % 52
//    51..55    own home column (safe, capture-免)
//    56        HOME — finished
//  A token needs an exact roll to land on 56 (overshoot = illegal).
//
//  Safe squares (absolute): the 4 start squares {0,13,26,39} and
//  the 4 star squares {8,21,34,47} — no captures there.
// ============================================================

public sealed class LudoSession : IGameSession
{
    private readonly RedisService _redis;
    private readonly ILogger<LudoSession> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private LudoPersistedState _state = null!;
    private QuizRoomMeta _meta = null!;
    private bool _initialised;

    private readonly ConcurrentQueue<GameEvent> _pendingEvents = new();

    public const int TurnSeconds = 30;
    private const int FinishedStep = 56;
    private const int LastTrackStep = 50;

    private static readonly LudoColor[] TurnOrder =
        { LudoColor.Red, LudoColor.Green, LudoColor.Yellow, LudoColor.Blue };

    private static readonly Dictionary<LudoColor, int> StartSquare = new()
    {
        [LudoColor.Red] = 0,
        [LudoColor.Green] = 13,
        [LudoColor.Yellow] = 26,
        [LudoColor.Blue] = 39,
    };

    private static readonly HashSet<int> SafeSquares = new() { 0, 13, 26, 39, 8, 21, 34, 47 };

    public LudoSession(
        string slug,
        QuizRoomMeta meta,
        RedisService redis,
        ILogger<LudoSession> logger)
    {
        Slug = slug;
        _meta = meta;
        _redis = redis;
        _logger = logger;
    }

    public string Slug { get; }
    public GameType Type => GameType.Ludo;
    public GameStatus Status => _state?.Status ?? GameStatus.Lobby;
    public string HostUserId => _meta.HostUserId;
    public int PlayerCount => _state?.Seats.Count ?? 0;
    public int SpectatorCount =>
        Math.Max(0, (_state?.Participants.Count ?? 0) - (_state?.Seats.Count ?? 0));

    /// <summary>In-memory activity stamp — see IGameSession docs.</summary>
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
                    _state = JsonSerializer.Deserialize<LudoPersistedState>(raw, JsonOpts)
                             ?? FreshState();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Ludo state for {Slug} was corrupt, starting fresh", Slug);
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

    private LudoPersistedState FreshState() => new()
    {
        Slug = Slug,
        Status = GameStatus.Lobby,
        Seats = new(),
        Tokens = new(),
        Participants = new(),
        ChatTail = new(),
        SeatRequests = new(),
    };

    // ───────────────────────────────────────────────────────────────
    //  Participants (director mode — EVERYONE enters as Spectator)
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

            // Director mode: nobody auto-seats — not even the creator.
            // The host opens the room as a Spectator and assigns colours
            // (including their own) via AssignSeatAsync.
            var participant = new LudoParticipant
            {
                UserId = userId,
                Username = username,
                Role = GameRole.Spectator,
                IsHost = userId == _meta.HostUserId,
                IsOnline = true,
                JoinedAtUtc = DateTime.UtcNow,
            };
            _state.Participants.Add(participant);
            await PersistStateUnsafeAsync();

            _pendingEvents.Enqueue(new ParticipantJoinedEvent(
                new GameParticipant(userId, username, GameRole.Spectator,
                    participant.IsHost, true)));
            _pendingEvents.Enqueue(new LudoStateEvent(BuildLudoSnapshotUnsafe()));

            var note = requestedRole == GameRole.Player && userId != _meta.HostUserId
                ? "Host assigns the seats — raise a hand to play."
                : null;
            return new JoinResult(true, GameRole.Spectator, note);
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
            _state.SeatRequests.Remove(userId);

            // Seats persist through a leave: mid-game the 30s timer
            // skips their turns, and they can rejoin to continue.
            // In the LOBBY a leave frees the seat so the host can
            // hand it to someone else without hunting for Unassign.
            if (_state.Status == GameStatus.Lobby)
            {
                var seat = _state.Seats.FirstOrDefault(s => s.UserId == userId);
                if (seat is not null) _state.Seats.Remove(seat);
            }

            await PersistStateUnsafeAsync();
            _pendingEvents.Enqueue(new ParticipantLeftEvent(userId));
            _pendingEvents.Enqueue(new LudoStateEvent(BuildLudoSnapshotUnsafe()));
        }
        finally { _lock.Release(); }
    }

    // ───────────────────────────────────────────────────────────────
    //  Seats (host-only director controls)
    // ───────────────────────────────────────────────────────────────

    public async Task<ActionResult> AssignSeatAsync(
        string hostUserId, string targetUserId, LudoColor color, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            if (hostUserId != _meta.HostUserId)
                return new ActionResult(false, "Only the host can assign seats.");
            if (_state.Status != GameStatus.Lobby)
                return new ActionResult(false, "Seats can only change in the lobby.");

            var target = _state.Participants.FirstOrDefault(p => p.UserId == targetUserId);
            if (target is null)
                return new ActionResult(false, "User is not in this room.");
            if (_state.Seats.Any(s => s.Color == color))
                return new ActionResult(false, $"{color} seat is already taken.");
            if (_state.Seats.Any(s => s.UserId == targetUserId))
                return new ActionResult(false, "They already hold a seat — unassign it first.");

            _state.Seats.Add(new LudoSeat
            {
                Color = color,
                UserId = targetUserId,
                Username = target.Username,
            });
            target.Role = GameRole.Player;

            if (_state.SeatRequests.Remove(targetUserId))
            {
                // Raised hand resolved by seating.
            }

            await PersistStateUnsafeAsync();
            _pendingEvents.Enqueue(new ParticipantJoinedEvent(
                new GameParticipant(target.UserId, target.Username,
                    GameRole.Player, target.IsHost, target.IsOnline)));
            _pendingEvents.Enqueue(new LudoStateEvent(BuildLudoSnapshotUnsafe()));
            return new ActionResult(true);
        }
        finally { _lock.Release(); }
    }

    public async Task<ActionResult> UnassignSeatAsync(
        string hostUserId, LudoColor color, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            if (hostUserId != _meta.HostUserId)
                return new ActionResult(false, "Only the host can unassign seats.");
            if (_state.Status != GameStatus.Lobby)
                return new ActionResult(false, "Seats can only change in the lobby.");

            var seat = _state.Seats.FirstOrDefault(s => s.Color == color);
            if (seat is null) return new ActionResult(true);

            _state.Seats.Remove(seat);
            var p = _state.Participants.FirstOrDefault(x => x.UserId == seat.UserId);
            if (p is not null)
            {
                p.Role = GameRole.Spectator;
                _pendingEvents.Enqueue(new ParticipantJoinedEvent(
                    new GameParticipant(p.UserId, p.Username,
                        GameRole.Spectator, p.IsHost, p.IsOnline)));
            }

            await PersistStateUnsafeAsync();
            _pendingEvents.Enqueue(new LudoStateEvent(BuildLudoSnapshotUnsafe()));
            return new ActionResult(true);
        }
        finally { _lock.Release(); }
    }

    public async Task<ActionResult> RequestSeatAsync(string userId, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            var p = _state.Participants.FirstOrDefault(x => x.UserId == userId);
            if (p is null)
                return new ActionResult(false, "Join the room first.");
            if (_state.Seats.Any(s => s.UserId == userId))
                return new ActionResult(false, "You're already seated.");
            if (_state.SeatRequests.Contains(userId))
                return new ActionResult(true, "Request already sent — host has been notified.");

            _state.SeatRequests.Add(userId);
            await PersistStateUnsafeAsync();
            _pendingEvents.Enqueue(new LudoStateEvent(BuildLudoSnapshotUnsafe()));
            return new ActionResult(true, "Request sent — waiting for the host.");
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
                return new ActionResult(false, "Only the host can start the game.");
            if (_state.Status != GameStatus.Lobby)
                return new ActionResult(false, "Game already in progress.");
            if (_state.Seats.Count < 2)
                return new ActionResult(false, "Assign at least 2 seats to start.");

            // All 4 tokens of every seated colour begin in base.
            _state.Tokens = _state.Seats.ToDictionary(
                s => s.Color,
                _ => new[] { -1, -1, -1, -1 });

            _state.Status = GameStatus.Playing;
            _state.CurrentTurn = TurnOrder.First(c => _state.Seats.Any(s => s.Color == c));
            _state.PendingRoll = null;
            _state.SixStreak = 0;
            _state.LastRoll = null;
            _state.WinnerUserId = null;
            _state.TurnDeadlineUtc = DateTime.UtcNow.AddSeconds(TurnSeconds);

            await PersistStateUnsafeAsync();
            _pendingEvents.Enqueue(new LudoStateEvent(BuildLudoSnapshotUnsafe()));
            return new ActionResult(true);
        }
        finally { _lock.Release(); }
    }

    // ───────────────────────────────────────────────────────────────
    //  Moves — {"action":"roll"} or {"action":"move","token":0-3}
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

            var seat = _state.Seats.FirstOrDefault(s => s.UserId == userId);
            if (seat is null)
                return new ActionResult(false, "Only seated players can play.");
            if (_state.CurrentTurn != seat.Color)
                return new ActionResult(false, "Not your turn.");

            LudoMoveSubmit? submit = null;
            try { submit = payload.Deserialize<LudoMoveSubmit>(JsonOpts); }
            catch { /* fall through */ }
            if (submit is null || string.IsNullOrEmpty(submit.Action))
                return new ActionResult(false, "Invalid submission.");

            return submit.Action switch
            {
                "roll" => await RollUnsafeAsync(seat.Color),
                "move" => await MoveUnsafeAsync(seat.Color, submit.Token),
                _ => new ActionResult(false, "Unknown action."),
            };
        }
        finally { _lock.Release(); }
    }

    private async Task<ActionResult> RollUnsafeAsync(LudoColor color)
    {
        if (_state.PendingRoll is not null)
            return new ActionResult(false, "Move a token first.");

        var value = Random.Shared.Next(1, 7);
        _state.LastRoll = new LudoRoll { Color = color, Value = value };

        if (value == 6) _state.SixStreak++;
        else _state.SixStreak = 0;

        // Three consecutive sixes — classic forfeit.
        if (_state.SixStreak >= 3)
        {
            _pendingEvents.Enqueue(new LudoDiceRolledEvent(color, value, Forfeited: true));
            AdvanceTurnUnsafe();
            await PersistStateUnsafeAsync();
            _pendingEvents.Enqueue(new LudoStateEvent(BuildLudoSnapshotUnsafe()));
            return new ActionResult(true);
        }

        var legal = LegalTokenIndexesUnsafe(color, value);
        if (legal.Count == 0)
        {
            // Nothing can move — show the dice, pass the turn.
            // (A 6 with no legal move still keeps the extra roll.)
            _pendingEvents.Enqueue(new LudoDiceRolledEvent(color, value, Forfeited: false));
            if (value != 6) AdvanceTurnUnsafe();
            else _state.TurnDeadlineUtc = DateTime.UtcNow.AddSeconds(TurnSeconds);
            await PersistStateUnsafeAsync();
            _pendingEvents.Enqueue(new LudoStateEvent(BuildLudoSnapshotUnsafe()));
            return new ActionResult(true);
        }

        _state.PendingRoll = value;
        _state.TurnDeadlineUtc = DateTime.UtcNow.AddSeconds(TurnSeconds);
        await PersistStateUnsafeAsync();
        _pendingEvents.Enqueue(new LudoDiceRolledEvent(color, value, Forfeited: false));
        _pendingEvents.Enqueue(new LudoStateEvent(BuildLudoSnapshotUnsafe()));
        return new ActionResult(true);
    }

    private async Task<ActionResult> MoveUnsafeAsync(LudoColor color, int tokenIndex)
    {
        if (_state.PendingRoll is null)
            return new ActionResult(false, "Roll the dice first.");
        if (tokenIndex is < 0 or > 3)
            return new ActionResult(false, "Invalid token.");

        var roll = _state.PendingRoll.Value;
        if (!LegalTokenIndexesUnsafe(color, roll).Contains(tokenIndex))
            return new ActionResult(false, "That token can't move with this roll.");

        var tokens = _state.Tokens[color];
        var captured = false;

        if (tokens[tokenIndex] == -1)
        {
            // Leaving base (roll is guaranteed 6 by legality check).
            tokens[tokenIndex] = 0;
            captured = CaptureAtUnsafe(color, 0);
        }
        else
        {
            var newSteps = tokens[tokenIndex] + roll;
            tokens[tokenIndex] = newSteps;
            if (newSteps <= LastTrackStep)
                captured = CaptureAtUnsafe(color, newSteps);
        }

        _state.PendingRoll = null;

        // Win check — all four tokens home.
        if (tokens.All(t => t == FinishedStep))
        {
            var seat = _state.Seats.First(s => s.Color == color);
            _state.Status = GameStatus.Ended;
            _state.WinnerUserId = seat.UserId;
            _state.CurrentTurn = null;
            _state.TurnDeadlineUtc = null;
            await PersistStateUnsafeAsync();
            _pendingEvents.Enqueue(new LudoStateEvent(BuildLudoSnapshotUnsafe()));
            _pendingEvents.Enqueue(new GameEndedEvent(
                BuildScoreboardUnsafe(), $"{seat.Username} wins! 🏆"));
            return new ActionResult(true);
        }

        // Extra roll on a 6 or a capture; otherwise next player.
        if (roll == 6 || captured)
        {
            _state.TurnDeadlineUtc = DateTime.UtcNow.AddSeconds(TurnSeconds);
        }
        else
        {
            AdvanceTurnUnsafe();
        }

        await PersistStateUnsafeAsync();
        _pendingEvents.Enqueue(new LudoStateEvent(BuildLudoSnapshotUnsafe()));
        return new ActionResult(true);
    }

    /// <summary>Send every opponent token on my landing square back to
    /// base. Returns true if anything was captured. Safe squares and
    /// home columns are capture-free by construction.</summary>
    private bool CaptureAtUnsafe(LudoColor mover, int moverSteps)
    {
        var absolute = (StartSquare[mover] + moverSteps) % 52;
        if (SafeSquares.Contains(absolute)) return false;

        var captured = false;
        foreach (var (color, tokens) in _state.Tokens)
        {
            if (color == mover) continue;
            for (var i = 0; i < tokens.Length; i++)
            {
                if (tokens[i] is < 0 or > LastTrackStep) continue;
                var theirAbsolute = (StartSquare[color] + tokens[i]) % 52;
                if (theirAbsolute == absolute)
                {
                    tokens[i] = -1;
                    captured = true;
                }
            }
        }
        return captured;
    }

    private List<int> LegalTokenIndexesUnsafe(LudoColor color, int roll)
    {
        var result = new List<int>();
        if (!_state.Tokens.TryGetValue(color, out var tokens)) return result;
        for (var i = 0; i < tokens.Length; i++)
        {
            var steps = tokens[i];
            if (steps == FinishedStep) continue;
            if (steps == -1)
            {
                if (roll == 6) result.Add(i);
                continue;
            }
            if (steps + roll <= FinishedStep) result.Add(i);
        }
        return result;
    }

    private void AdvanceTurnUnsafe()
    {
        _state.SixStreak = 0;
        _state.PendingRoll = null;
        if (_state.CurrentTurn is null) return;

        var seated = TurnOrder.Where(c => _state.Seats.Any(s => s.Color == c)).ToList();
        var idx = seated.IndexOf(_state.CurrentTurn.Value);
        _state.CurrentTurn = seated[(idx + 1) % seated.Count];
        _state.TurnDeadlineUtc = DateTime.UtcNow.AddSeconds(TurnSeconds);
    }

    // ───────────────────────────────────────────────────────────────
    //  Tick — 30s turn auto-skip ("no lagging" guarantee)
    // ───────────────────────────────────────────────────────────────

    public async Task<bool> TickAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (!_initialised) return false;
        if (Status != GameStatus.Playing) return false;
        if (_state.TurnDeadlineUtc is null) return false;
        if (nowUtc < _state.TurnDeadlineUtc.Value) return false;

        await _lock.WaitAsync(ct);
        try
        {
            if (_state.Status != GameStatus.Playing) return false;
            if (_state.TurnDeadlineUtc is null) return false;
            if (nowUtc < _state.TurnDeadlineUtc.Value) return false;

            var skipped = _state.CurrentTurn;
            AdvanceTurnUnsafe();
            await PersistStateUnsafeAsync();
            if (skipped is not null)
                _pendingEvents.Enqueue(new LudoTurnSkippedEvent(skipped.Value));
            _pendingEvents.Enqueue(new LudoStateEvent(BuildLudoSnapshotUnsafe()));
            return true;
        }
        finally { _lock.Release(); }
    }

    // ───────────────────────────────────────────────────────────────
    //  Snapshots
    // ───────────────────────────────────────────────────────────────

    public async Task<GameRoomSnapshot> GetSnapshotAsync(string forUserId, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            var viewer = _state.Participants.FirstOrDefault(p => p.UserId == forUserId);
            return new GameRoomSnapshot(
                Room: BuildRoomDtoUnsafe(),
                ViewerRole: viewer?.Role,
                CurrentQuestion: null,
                Scoreboard: BuildScoreboardUnsafe(),
                Participants: _state.Participants.Select(p => new GameParticipant(
                    p.UserId, p.Username, p.Role, p.IsHost, p.IsOnline)).ToList(),
                RecentChat: _state.ChatTail.ToList(),
                SeatRequests: _state.SeatRequests.ToList());
        }
        finally { _lock.Release(); }
    }

    /// <summary>Full board snapshot — broadcast after every mutation
    /// and fetchable via GameHub.GetLudoState for late joiners.</summary>
    public async Task<LudoStateSnapshot> GetLudoStateAsync(CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try { return BuildLudoSnapshotUnsafe(); }
        finally { _lock.Release(); }
    }

    private LudoStateSnapshot BuildLudoSnapshotUnsafe() => new(
        Status: _state.Status,
        Seats: _state.Seats
            .Select(s => new LudoSeatDto(s.Color, s.UserId, s.Username))
            .ToList(),
        Tokens: _state.Tokens.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.ToArray()),
        CurrentTurn: _state.CurrentTurn,
        PendingRoll: _state.PendingRoll,
        LastRoll: _state.LastRoll is null
            ? null
            : new LudoRollDto(_state.LastRoll.Color, _state.LastRoll.Value),
        TurnDeadlineUtc: _state.TurnDeadlineUtc,
        SeatRequests: _state.SeatRequests.ToList(),
        WinnerUserId: _state.WinnerUserId,
        TurnSeconds: TurnSeconds);

    /// <summary>Scoreboard = finished-token count per seat (reuses the
    /// shared ScoreEntry shape so GameEnded toasts render unchanged).</summary>
    private List<ScoreEntry> BuildScoreboardUnsafe()
    {
        return _state.Seats
            .Select(s =>
            {
                var finished = _state.Tokens.TryGetValue(s.Color, out var t)
                    ? t.Count(x => x == FinishedStep)
                    : 0;
                return new ScoreEntry(
                    UserId: s.UserId,
                    Username: s.Username,
                    Score: finished,
                    CorrectAnswers: finished,
                    AnsweredCount: 4,
                    AverageResponseMs: 0);
            })
            .OrderByDescending(e => e.Score)
            .ToList();
    }

    private GameRoomDto BuildRoomDtoUnsafe() => new(
        Slug: Slug,
        Name: _meta.Name,
        Type: GameType.Ludo,
        Status: _state.Status,
        PlayerCount: PlayerCount,
        MaxPlayers: 4,
        SpectatorCount: SpectatorCount,
        HostUsername: _meta.HostUsername,
        CreatedAtUtc: _meta.CreatedAtUtc);

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
            LastActivityUtc = DateTime.UtcNow;
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
            if (_state.ChatTail.Count > 30)
                _state.ChatTail.RemoveRange(0, _state.ChatTail.Count - 30);

            await PersistStateUnsafeAsync();
            return msg;
        }
        finally { _lock.Release(); }
    }

    // ───────────────────────────────────────────────────────────────
    //  Plumbing
    // ───────────────────────────────────────────────────────────────

    private async Task EnsureInitialisedAsync(CancellationToken ct)
    {
        if (!_initialised) await InitializeAsync(ct);
    }

    private async Task PersistStateUnsafeAsync()
    {
        LastActivityUtc = DateTime.UtcNow;
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
    //  Persisted shapes
    // ───────────────────────────────────────────────────────────────

    public sealed class LudoPersistedState
    {
        public string Slug { get; set; } = "";
        public GameStatus Status { get; set; }
        public List<LudoSeat> Seats { get; set; } = new();
        /// <summary>Colour → 4 token step-positions (see encoding doc).</summary>
        public Dictionary<LudoColor, int[]> Tokens { get; set; } = new();
        public LudoColor? CurrentTurn { get; set; }
        public int? PendingRoll { get; set; }
        public int SixStreak { get; set; }
        public LudoRoll? LastRoll { get; set; }
        public DateTime? TurnDeadlineUtc { get; set; }
        public List<LudoParticipant> Participants { get; set; } = new();
        public List<GameChatMessage> ChatTail { get; set; } = new();
        public List<string> SeatRequests { get; set; } = new();
        public string? WinnerUserId { get; set; }
    }

    public sealed class LudoSeat
    {
        public LudoColor Color { get; set; }
        public string UserId { get; set; } = "";
        public string Username { get; set; } = "";
    }

    public sealed class LudoParticipant
    {
        public string UserId { get; set; } = "";
        public string Username { get; set; } = "";
        public GameRole Role { get; set; }
        public bool IsHost { get; set; }
        public bool IsOnline { get; set; }
        public DateTime JoinedAtUtc { get; set; }
    }

    public sealed class LudoRoll
    {
        public LudoColor Color { get; set; }
        public int Value { get; set; }
    }

    private sealed record LudoMoveSubmit(string Action, int Token = -1);
}

// ─── Wire DTOs ──────────────────────────────────────────────────

public record LudoSeatDto(LudoColor Color, string UserId, string Username);

public record LudoRollDto(LudoColor Color, int Value);

/// <summary>Complete board state — small enough to broadcast whole on
/// every mutation, which makes client drift impossible.</summary>
public record LudoStateSnapshot(
    GameStatus Status,
    IReadOnlyList<LudoSeatDto> Seats,
    Dictionary<string, int[]> Tokens,
    LudoColor? CurrentTurn,
    int? PendingRoll,
    LudoRollDto? LastRoll,
    DateTime? TurnDeadlineUtc,
    IReadOnlyList<string> SeatRequests,
    string? WinnerUserId,
    int TurnSeconds);

// ─── Events ─────────────────────────────────────────────────────

public sealed record LudoStateEvent(LudoStateSnapshot Snapshot) : GameEvent;

public sealed record LudoDiceRolledEvent(
    LudoColor Color, int Value, bool Forfeited) : GameEvent;

public sealed record LudoTurnSkippedEvent(LudoColor Color) : GameEvent;
