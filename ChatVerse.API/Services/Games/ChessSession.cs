using ChatVerse.API.Models.Games;
using ChatVerse.Domain.Constants;
using ChatVerse.Infrastructure.Persistence.Redis;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatVerse.API.Services.Games;

// ============================================================
//  ChessSession — 2-player real-time chess.
//
//  Architecture choice for v1 (move trust):
//   • Clients use chess.js to validate moves before submitting.
//   • Server tracks turn order + game state via the FEN + move
//     list payload the client sends along.
//   • Server does NOT independently validate move legality.
//   • Tradeoff: a malicious client could send an illegal move
//     but the OTHER client's chess.js would reject the resulting
//     FEN as inconsistent — so a desync surfaces immediately.
//   • v2 will swap this for server-side validation via a NuGet
//     chess library if casual play hits a cheating problem.
//
//  Seating: first joining Player auto-becomes White. Second
//  becomes Black. Beyond 2 → Spectators automatically. Host
//  joins as White on Create.
//
//  Game end: client tells us "this move was checkmate/stalemate/
//  etc." via ChessMoveSubmit. We trust the flag for the result
//  and record it. If chess.js says checkmate but the FEN doesn't
//  agree, both clients would catch the inconsistency on render.
//
//  Join requests: private rooms gate joiners through the host's
//  approval list (see JoinRequest in GameModels). Public rooms
//  bypass this entirely — same behaviour as QuizSession.
// ============================================================

public sealed class ChessSession : IGameSession
{
    public const string StartingFen =
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    private readonly RedisService _redis;
    private readonly ILogger<ChessSession> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private QuizRoomMeta _meta = null!;
    private ChessPersistedState _state = null!;
    private bool _initialised;

    private readonly ConcurrentQueue<GameEvent> _pendingEvents = new();

    public ChessSession(
        string slug,
        QuizRoomMeta meta,
        RedisService redis,
        ILogger<ChessSession> logger)
    {
        Slug = slug;
        _meta = meta;
        _redis = redis;
        _logger = logger;
    }

    public string Slug { get; }
    public GameType Type => GameType.Chess;
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
                    _state = JsonSerializer.Deserialize<ChessPersistedState>(raw, JsonOpts)
                             ?? FreshState();
                }
                catch
                {
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

    private ChessPersistedState FreshState() => new()
    {
        Slug = Slug,
        Status = GameStatus.Lobby,
        Settings = _meta.Settings,
        Fen = StartingFen,
        Turn = ChessColor.White,
        Result = ChessResult.InProgress,
        MoveHistory = new(),
        Participants = new(),
        ChatTail = new(),
        WhitePlayerId = null,
        WhitePlayerName = null,
        BlackPlayerId = null,
        BlackPlayerName = null,
        PendingRequests = new(),
    };

    // ───────────────────────────────────────────────────────────────
    //  JOIN — gated by isPublic + (for private) request approval
    // ───────────────────────────────────────────────────────────────
    public async Task<JoinResult> JoinAsync(
        string userId, string username, GameRole requestedRole, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            // Returning user — refresh online flag, give them their seat.
            var existing = _state.Participants.FirstOrDefault(p => p.UserId == userId);
            if (existing is not null)
            {
                existing.IsOnline = true;
                await PersistStateUnsafeAsync();
                return new JoinResult(true, existing.Role);
            }

            // Private room + not the host → must request first.
            if (!_meta.IsPublic && userId != _meta.HostUserId)
            {
                // Auto-approve if already in the approved-history list.
                var alreadyApproved = _state.ApprovedUserIds.Contains(userId);
                if (!alreadyApproved)
                {
                    var req = new JoinRequestRecord
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        UserId = userId,
                        Username = username,
                        RequestedAtUtc = DateTime.UtcNow,
                        Status = JoinRequestStatus.Pending,
                    };
                    _state.PendingRequests.Add(req);
                    await PersistStateUnsafeAsync();
                    _pendingEvents.Enqueue(new JoinRequestedEvent(
                        new JoinRequest(req.Id, req.UserId, req.Username,
                                        req.RequestedAtUtc, req.Status)));

                    // Hand a "pending" result back — the page knows to show
                    // a "waiting for approval" view until JoinRequestResolved
                    // arrives.
                    return new JoinResult(false, requestedRole,
                        "Join request sent to host. Waiting for approval…");
                }
            }

            // Public room OR private with prior approval — admit.
            return await AdmitUnsafeAsync(userId, username, requestedRole);
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Inserts a user into the participant list, assigning Player or
    /// Spectator as space allows. Also seats them as White / Black if
    /// those slots are open. Caller must hold _lock.
    /// </summary>
    private async Task<JoinResult> AdmitUnsafeAsync(
        string userId, string username, GameRole requestedRole)
    {
        if (_state.Status == GameStatus.Ended)
            return new JoinResult(false, requestedRole, "Game has ended.");

        var assignedRole = requestedRole;
        // Cap at 2 Players for chess. Anyone after = Spectator.
        if (assignedRole == GameRole.Player && PlayerCount >= 2)
            assignedRole = GameRole.Spectator;
        // Block new Player joins mid-game.
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

        // Assign White / Black seats. First Player taking it = White
        // (so host who creates + auto-joins becomes White).
        if (assignedRole == GameRole.Player)
        {
            if (_state.WhitePlayerId is null)
            {
                _state.WhitePlayerId = userId;
                _state.WhitePlayerName = username;
            }
            else if (_state.BlackPlayerId is null && _state.WhitePlayerId != userId)
            {
                _state.BlackPlayerId = userId;
                _state.BlackPlayerName = username;
            }
        }

        await PersistStateUnsafeAsync();

        _pendingEvents.Enqueue(new ParticipantJoinedEvent(
            new GameParticipant(userId, username, assignedRole, participant.IsHost, true)));

        return new JoinResult(true, assignedRole);
    }

    /// <summary>
    /// Host approves a pending request — moves the user from
    /// PendingRequests into Participants. Broadcasts the resolution
    /// so the requester's "waiting" view can transition into the
    /// real game.
    /// </summary>
    public async Task<ActionResult> ApproveJoinRequestAsync(
        string requestingUserId, string requestId, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            if (requestingUserId != _meta.HostUserId)
                return new ActionResult(false, "Only the host can approve.");

            var req = _state.PendingRequests.FirstOrDefault(r => r.Id == requestId);
            if (req is null) return new ActionResult(false, "Request not found.");
            if (req.Status != JoinRequestStatus.Pending)
                return new ActionResult(false, "Already resolved.");

            req.Status = JoinRequestStatus.Approved;
            _state.PendingRequests.Remove(req);
            _state.ApprovedUserIds.Add(req.UserId);

            var join = await AdmitUnsafeAsync(req.UserId, req.Username, GameRole.Player);

            _pendingEvents.Enqueue(new JoinRequestResolvedEvent(
                new JoinRequestResolved(req.Id, req.UserId, JoinRequestStatus.Approved)));

            return new ActionResult(true, join.Reason);
        }
        finally { _lock.Release(); }
    }

    public async Task<ActionResult> DeclineJoinRequestAsync(
        string requestingUserId, string requestId, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            if (requestingUserId != _meta.HostUserId)
                return new ActionResult(false, "Only the host can decline.");

            var req = _state.PendingRequests.FirstOrDefault(r => r.Id == requestId);
            if (req is null) return new ActionResult(false, "Request not found.");

            _state.PendingRequests.Remove(req);
            await PersistStateUnsafeAsync();

            _pendingEvents.Enqueue(new JoinRequestResolvedEvent(
                new JoinRequestResolved(req.Id, req.UserId, JoinRequestStatus.Declined)));

            return new ActionResult(true);
        }
        finally { _lock.Release(); }
    }

    // ───────────────────────────────────────────────────────────────
    //  LEAVE
    // ───────────────────────────────────────────────────────────────
    public async Task LeaveAsync(string userId, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            var p = _state.Participants.FirstOrDefault(x => x.UserId == userId);
            if (p is null) return;

            _state.Participants.Remove(p);
            // Vacate seat if a Player walks away — counts as a resign
            // mid-game (the other side wins by default), or just frees
            // the seat if still in lobby.
            if (p.Role == GameRole.Player && _state.Status == GameStatus.Playing)
            {
                if (userId == _state.WhitePlayerId)
                {
                    _state.Result = ChessResult.BlackWins;
                }
                else if (userId == _state.BlackPlayerId)
                {
                    _state.Result = ChessResult.WhiteWins;
                }
                _state.Status = GameStatus.Ended;
                _pendingEvents.Enqueue(new GameEndedEvent(
                    new List<ScoreEntry>(), $"{p.Username} disconnected — game forfeited."));
            }
            else if (p.Role == GameRole.Player)
            {
                // Lobby disconnect — just free the seat.
                if (userId == _state.WhitePlayerId) { _state.WhitePlayerId = null; _state.WhitePlayerName = null; }
                if (userId == _state.BlackPlayerId) { _state.BlackPlayerId = null; _state.BlackPlayerName = null; }
            }
            await PersistStateUnsafeAsync();
            _pendingEvents.Enqueue(new ParticipantLeftEvent(userId));
        }
        finally { _lock.Release(); }
    }

    // ───────────────────────────────────────────────────────────────
    //  START — needs 2 seated players
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
                return new ActionResult(false, "Game already started.");
            if (_state.WhitePlayerId is null || _state.BlackPlayerId is null)
                return new ActionResult(false, "Need two players seated.");

            _state.Status = GameStatus.Playing;
            await PersistStateUnsafeAsync();

            _pendingEvents.Enqueue(new ChessGameStartedEvent(BuildSnapshotUnsafe()));
            return new ActionResult(true);
        }
        finally { _lock.Release(); }
    }

    // ───────────────────────────────────────────────────────────────
    //  SUBMIT MOVE
    // ───────────────────────────────────────────────────────────────
    public async Task<ActionResult> SubmitMoveAsync(
        string userId, JsonElement payload, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            if (_state.Status != GameStatus.Playing)
                return new ActionResult(false, "Game not in progress.");
            if (_state.Result != ChessResult.InProgress)
                return new ActionResult(false, "Game has ended.");

            ChessMoveSubmit? move = null;
            try { move = payload.Deserialize<ChessMoveSubmit>(JsonOpts); }
            catch { /* fall through */ }
            if (move is null || string.IsNullOrEmpty(move.San) || string.IsNullOrEmpty(move.Uci))
                return new ActionResult(false, "Malformed move payload.");

            // Turn enforcement — server's authoritative bit.
            var expectedPlayerId = _state.Turn == ChessColor.White
                ? _state.WhitePlayerId
                : _state.BlackPlayerId;
            if (userId != expectedPlayerId)
                return new ActionResult(false, "Not your turn.");

            var who = _state.Turn;
            var newMove = new ChessMove(
                San: move.San,
                Uci: move.Uci,
                FenAfter: move.FenAfter,
                By: who,
                AtUtc: DateTime.UtcNow);
            _state.MoveHistory.Add(newMove);
            _state.Fen = move.FenAfter;
            _state.Turn = who == ChessColor.White ? ChessColor.Black : ChessColor.White;

            // Lightweight end-game detection from FEN. chess.js sends
            // FEN with the move number; we don't bother reproducing
            // checkmate detection here — clients pass a `result` field
            // when they detect it. v2 will validate server-side.
            // For now, detect by an explicit signal at the submit layer:
            //   FEN trailing flag "#" in San = checkmate
            //   FEN trailing flag "=" in San = draw offer (not in scope)
            ChessResult result = ChessResult.InProgress;
            string? detail = null;
            if (move.San.EndsWith("#"))
            {
                result = who == ChessColor.White ? ChessResult.WhiteWins : ChessResult.BlackWins;
                detail = "Checkmate";
                _state.Status = GameStatus.Ended;
            }
            _state.Result = result;

            await PersistStateUnsafeAsync();

            _pendingEvents.Enqueue(new ChessMovePushedEvent(
                new ChessMovePushed(newMove, _state.MoveHistory.Count, _state.Turn, result, detail)));

            return new ActionResult(true);
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Resign — explicit hub method (not a normal move). Loses for
    /// the resigning side. Anyone seated as a Player can resign.
    /// </summary>
    public async Task<ActionResult> ResignAsync(string userId, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            if (_state.Status != GameStatus.Playing)
                return new ActionResult(false, "Not in progress.");
            if (userId != _state.WhitePlayerId && userId != _state.BlackPlayerId)
                return new ActionResult(false, "Only seated players can resign.");

            _state.Result = userId == _state.WhitePlayerId
                ? ChessResult.BlackWins
                : ChessResult.WhiteWins;
            _state.Status = GameStatus.Ended;
            await PersistStateUnsafeAsync();

            // Synthetic "move" so the broadcast pipeline still works.
            // We use SAN "1-0" / "0-1" to make UI rendering uniform.
            var loser = userId == _state.WhitePlayerId ? ChessColor.White : ChessColor.Black;
            var winnerScore = loser == ChessColor.White ? "0-1" : "1-0";
            _pendingEvents.Enqueue(new ChessMovePushedEvent(
                new ChessMovePushed(
                    new ChessMove(winnerScore, winnerScore, _state.Fen, loser, DateTime.UtcNow),
                    _state.MoveHistory.Count + 1,
                    _state.Turn,
                    _state.Result,
                    "Resigned")));
            return new ActionResult(true);
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> TickAsync(DateTime nowUtc, CancellationToken ct)
    {
        // No time controls in v1 — nothing to advance on a clock tick.
        await Task.CompletedTask;
        return false;
    }

    // ───────────────────────────────────────────────────────────────
    //  SNAPSHOT (for late joiners + reconnect catch-up)
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
                Scoreboard: new List<ScoreEntry>(),
                Participants: _state.Participants.Select(p => new GameParticipant(
                    p.UserId, p.Username, p.Role, p.IsHost, p.IsOnline)).ToList(),
                RecentChat: _state.ChatTail.ToList());
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Chess-specific snapshot — call this from the hub after the
    /// normal GetSnapshotAsync so the client has board state too.
    /// </summary>
    public async Task<ChessStateSnapshot> GetChessSnapshotAsync(CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try { return BuildSnapshotUnsafe(); }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<JoinRequest>> GetPendingRequestsAsync(CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            return _state.PendingRequests
                .Select(r => new JoinRequest(r.Id, r.UserId, r.Username, r.RequestedAtUtc, r.Status))
                .ToList();
        }
        finally { _lock.Release(); }
    }

    // ───────────────────────────────────────────────────────────────
    //  CHAT
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
            if (_state.ChatTail.Count > 100)
                _state.ChatTail.RemoveRange(0, _state.ChatTail.Count - 100);
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

    private ChessStateSnapshot BuildSnapshotUnsafe() => new(
        Fen: _state.Fen,
        Turn: _state.Turn,
        Result: _state.Result,
        MoveHistory: _state.MoveHistory.ToList(),
        WhitePlayerId: _state.WhitePlayerId,
        WhitePlayerName: _state.WhitePlayerName,
        BlackPlayerId: _state.BlackPlayerId,
        BlackPlayerName: _state.BlackPlayerName);

    private GameRoomDto BuildRoomDtoUnsafe() => new(
        Slug: Slug,
        Name: _meta.Name,
        Type: GameType.Chess,
        Status: _state.Status,
        PlayerCount: PlayerCount,
        MaxPlayers: 2,
        SpectatorCount: SpectatorCount,
        HostUsername: _meta.HostUsername,
        CreatedAtUtc: _meta.CreatedAtUtc)
    {
        IsPublic = _meta.IsPublic,
        IsRandom = _meta.IsRandom,
        SourceChatSlug = _meta.SourceChatSlug,
    };

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
    //  Persisted shapes
    // ───────────────────────────────────────────────────────────────
    public sealed class ChessPersistedState
    {
        public string Slug { get; set; } = "";
        public GameStatus Status { get; set; }
        public QuizSettings Settings { get; set; } = new();
        public string Fen { get; set; } = StartingFen;
        public ChessColor Turn { get; set; }
        public ChessResult Result { get; set; }
        public List<ChessMove> MoveHistory { get; set; } = new();
        public List<ParticipantState> Participants { get; set; } = new();
        public List<GameChatMessage> ChatTail { get; set; } = new();
        public string? WhitePlayerId { get; set; }
        public string? WhitePlayerName { get; set; }
        public string? BlackPlayerId { get; set; }
        public string? BlackPlayerName { get; set; }
        public List<JoinRequestRecord> PendingRequests { get; set; } = new();
        /// <summary>Users who were once approved — auto-admit on
        /// reconnect without re-prompting the host.</summary>
        public HashSet<string> ApprovedUserIds { get; set; } = new();
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

    public sealed class JoinRequestRecord
    {
        public string Id { get; set; } = "";
        public string UserId { get; set; } = "";
        public string Username { get; set; } = "";
        public DateTime RequestedAtUtc { get; set; }
        public JoinRequestStatus Status { get; set; }
    }
}

// ============================================================
//  Chess-specific events for the broadcast pipeline.
// ============================================================

public sealed record ChessGameStartedEvent(ChessStateSnapshot Snapshot) : GameEvent;

public sealed record ChessMovePushedEvent(ChessMovePushed Move) : GameEvent;

public sealed record JoinRequestedEvent(JoinRequest Request) : GameEvent;

public sealed record JoinRequestResolvedEvent(JoinRequestResolved Resolution) : GameEvent;
