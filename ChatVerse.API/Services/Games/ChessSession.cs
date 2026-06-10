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

            // Resume grace timers for any seated Players who went
            // offline before the last persist. Either we re-run the
            // remaining wait, or — if it already elapsed — we fire
            // the timeout immediately on a background task so the
            // seat doesn't stay frozen after a server restart.
            ResumeGraceTimersUnsafe();
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Restart fire-and-forget grace tasks for offline seated players
    /// found in the persisted state. Caller must hold _lock.
    /// </summary>
    private void ResumeGraceTimersUnsafe()
    {
        foreach (var p in _state.Participants)
        {
            if (p.IsOnline || !p.LeftAtUtc.HasValue) continue;
            var isSeated = p.UserId == _state.WhitePlayerId || p.UserId == _state.BlackPlayerId;
            if (!isSeated) continue;

            var remaining = TimeSpan.FromSeconds(GraceSeconds) - (DateTime.UtcNow - p.LeftAtUtc.Value);
            var uid = p.UserId;

            if (remaining <= TimeSpan.Zero)
            {
                // Grace already elapsed — schedule timeout on a fresh
                // task so it runs after we've released the init lock.
                _ = Task.Run(async () =>
                {
                    try { await OnGraceExpiredAsync(uid, CancellationToken.None); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Resumed-stale grace timeout failed for {U} in {S}", uid, Slug);
                    }
                });
            }
            else
            {
                p.GraceCts = new CancellationTokenSource();
                var token = p.GraceCts.Token;
                var delay = remaining;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delay, token);
                        await OnGraceExpiredAsync(uid, CancellationToken.None);
                    }
                    catch (OperationCanceledException) { /* player returned */ }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Resumed grace task crashed for {U} in {S}", uid, Slug);
                    }
                });
            }
        }
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
            // If they were in a grace window (seated Player who walked
            // away), cancel the timeout task so the seat stays theirs.
            var existing = _state.Participants.FirstOrDefault(p => p.UserId == userId);
            if (existing is not null)
            {
                var wasInGrace = existing.GraceCts is not null && !existing.IsOnline;
                existing.IsOnline = true;
                existing.LeftAtUtc = null;
                if (existing.GraceCts is not null)
                {
                    try { existing.GraceCts.Cancel(); existing.GraceCts.Dispose(); }
                    catch { /* already disposed — fine */ }
                    existing.GraceCts = null;
                }
                await PersistStateUnsafeAsync();
                if (wasInGrace)
                {
                    _pendingEvents.Enqueue(new PlayerReturnedEvent(existing.UserId, existing.Username));
                }
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
    /// Director-mode admission: everyone — including the host — enters
    /// as a Spectator. Seats are assigned EXPLICITLY by the host via
    /// AssignSeatAsync. No auto-seating, no silent upgrades.
    ///
    /// Why: previously the first Player auto-took White, the second
    /// Black. That created the "guest somehow ended up on Black" UX
    /// bug — silent assignment beat any explicit intent the host had.
    /// Now: empty room = both seats null. Host decides who plays.
    /// </summary>
    private async Task<JoinResult> AdmitUnsafeAsync(
        string userId, string username, GameRole requestedRole)
    {
        if (_state.Status == GameStatus.Ended)
            return new JoinResult(false, requestedRole, "Game has ended.");

        // Everyone joins as Spectator. Host elevates them to Player by
        // assigning a seat. Even the room creator starts as Spectator —
        // they self-assign if they want to play.
        var assignedRole = GameRole.Spectator;

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

    /// <summary>
    /// Spectator asks the host for a Player seat. ALWAYS queues — the
    /// host decides both whether to admit AND which colour to give.
    /// No silent auto-seating even if a seat is empty.
    ///
    /// Decision tree:
    ///   1. Already a Player → no-op success.
    ///   2. Not in room → reject (must be a spectator first).
    ///   3. Otherwise → enqueue as JoinRequest. Host picks colour
    ///      when approving via AssignSeatAsync.
    /// </summary>
    public async Task<ActionResult> RequestPlayerSeatAsync(
        string userId, string username, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            var p = _state.Participants.FirstOrDefault(x => x.UserId == userId);
            if (p is null)
                return new ActionResult(false, "Join the room first.");
            if (p.Role == GameRole.Player)
                return new ActionResult(true, "You're already a player.");

            // Always queue — host gets the colour choice. Even with an
            // empty seat, we wait for explicit assignment. (Previous
            // silent-admit path caused the "guest sneaks into Black"
            // behaviour that director-mode is fixing.)
            if (_state.PendingRequests.Any(r => r.UserId == userId && r.Status == JoinRequestStatus.Pending))
                return new ActionResult(true, "Request already pending.");

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
                new JoinRequest(req.Id, req.UserId, req.Username, req.RequestedAtUtc, req.Status)));
            return new ActionResult(true, "Request sent to host.");
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Director-mode seat assignment. Host picks who plays which colour.
    /// Target can be:
    ///   • the host themselves (self-assign — "I want to play White")
    ///   • an existing Spectator in the room
    ///   • a Spectator whose RequestPlayerSeatAsync request is pending
    ///     (we auto-resolve the request so the host's notification list
    ///     stays clean)
    ///
    /// Behaviour:
    ///   • Lobby only — once status == Playing, seats are locked.
    ///     Reassigning mid-game would discard board state silently;
    ///     instead the host should let the game finish or end it first.
    ///   • Target seat must be empty. To replace someone, host calls
    ///     UnassignSeatAsync first then this.
    /// </summary>
    public async Task<ActionResult> AssignSeatAsync(
        string hostUserId, string targetUserId, ChessColor color, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            if (hostUserId != _meta.HostUserId)
                return new ActionResult(false, "Only the host can assign seats.");
            if (_state.Status != GameStatus.Lobby)
                return new ActionResult(false, "Seats are locked once the game starts.");

            var target = _state.Participants.FirstOrDefault(p => p.UserId == targetUserId);
            if (target is null)
                return new ActionResult(false, "Target user is not in the room.");

            var (currentId, currentName) = color == ChessColor.White
                ? (_state.WhitePlayerId, _state.WhitePlayerName)
                : (_state.BlackPlayerId, _state.BlackPlayerName);
            if (currentId is not null)
                return new ActionResult(false, $"{color} seat is occupied. Empty it first.");

            // Apply.
            if (color == ChessColor.White)
            {
                _state.WhitePlayerId = target.UserId;
                _state.WhitePlayerName = target.Username;
            }
            else
            {
                _state.BlackPlayerId = target.UserId;
                _state.BlackPlayerName = target.Username;
            }
            target.Role = GameRole.Player;

            // If they had a pending RequestPlayerSeat, auto-resolve it
            // so the host's pending-list cleans up.
            var pending = _state.PendingRequests
                .FirstOrDefault(r => r.UserId == target.UserId && r.Status == JoinRequestStatus.Pending);
            if (pending is not null)
            {
                pending.Status = JoinRequestStatus.Approved;
                _state.PendingRequests.Remove(pending);
                _state.ApprovedUserIds.Add(target.UserId);
                _pendingEvents.Enqueue(new JoinRequestResolvedEvent(
                    new JoinRequestResolved(pending.Id, target.UserId, JoinRequestStatus.Approved)));
            }

            await PersistStateUnsafeAsync();
            // Broadcast the updated seat layout via the standard chess
            // snapshot event so clients re-render labels + orientation.
            _pendingEvents.Enqueue(new ChessSeatChangedEvent(BuildSnapshotUnsafe()));
            return new ActionResult(true, $"{target.Username} seated as {color}.");
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Frees a seat (host-only, lobby-only). The unseated player goes
    /// back to Spectator role and can re-request or be reassigned.
    /// </summary>
    public async Task<ActionResult> UnassignSeatAsync(
        string hostUserId, ChessColor color, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            if (hostUserId != _meta.HostUserId)
                return new ActionResult(false, "Only the host can unseat players.");
            if (_state.Status != GameStatus.Lobby)
                return new ActionResult(false, "Seats are locked once the game starts.");

            var seatedId = color == ChessColor.White ? _state.WhitePlayerId : _state.BlackPlayerId;
            if (seatedId is null)
                return new ActionResult(false, $"{color} seat is already empty.");

            var seated = _state.Participants.FirstOrDefault(p => p.UserId == seatedId);
            if (color == ChessColor.White) { _state.WhitePlayerId = null; _state.WhitePlayerName = null; }
            else                           { _state.BlackPlayerId = null; _state.BlackPlayerName = null; }
            if (seated is not null) seated.Role = GameRole.Spectator;

            await PersistStateUnsafeAsync();
            _pendingEvents.Enqueue(new ChessSeatChangedEvent(BuildSnapshotUnsafe()));
            return new ActionResult(true, $"{color} seat cleared.");
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
    //  LEAVE — grace-aware
    //
    //  Behaviour:
    //   • Seated Player (White / Black) leaves → DO NOT remove from
    //     participants, DO NOT free seat. Mark IsOnline = false, stamp
    //     LeftAtUtc, start a fire-and-forget 60s grace timer. Host and
    //     opponent see a countdown banner with optional "skip-wait".
    //   • If the player returns inside 60s, JoinAsync cancels the timer
    //     and reseats them — board state preserved.
    //   • If 60s elapses, OnGraceExpiredAsync runs: removes them, frees
    //     the seat, and if it was mid-game resets the board to start +
    //     status back to Lobby (per user's "chess will restart" intent).
    //   • Spectators are removed immediately — they don't hold state.
    //   • Host walking away from lobby still kills the room (creator
    //     intent is unambiguous).
    // ───────────────────────────────────────────────────────────────
    public async Task LeaveAsync(string userId, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            var p = _state.Participants.FirstOrDefault(x => x.UserId == userId);
            if (p is null) return;

            var isSeatedPlayer = p.Role == GameRole.Player &&
                (userId == _state.WhitePlayerId || userId == _state.BlackPlayerId);
            var hostLeft = p.IsHost;

            if (isSeatedPlayer && _state.Status != GameStatus.Ended)
            {
                // Grace path — keep seat, start countdown.
                p.IsOnline = false;
                p.LeftAtUtc = DateTime.UtcNow;
                // Cancel any prior grace token (shouldn't happen, but
                // defensive) before creating a new one.
                try { p.GraceCts?.Cancel(); p.GraceCts?.Dispose(); } catch { }
                p.GraceCts = new CancellationTokenSource();
                var graceToken = p.GraceCts.Token;
                var leftUserId = userId;
                var seatColor = userId == _state.WhitePlayerId ? ChessColor.White : ChessColor.Black;

                await PersistStateUnsafeAsync();
                _pendingEvents.Enqueue(new PlayerDisconnectedEvent(
                    leftUserId, p.Username, seatColor, GraceSeconds));

                // Fire-and-forget grace timer. Per user's async/cancellable
                // rule: caller doesn't await, exception swallowed, and the
                // CTS lets either side cancel cleanly.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(GraceSeconds), graceToken);
                        // Grace elapsed without a return.
                        await OnGraceExpiredAsync(leftUserId, CancellationToken.None);
                    }
                    catch (OperationCanceledException)
                    {
                        // Player returned — JoinAsync cancelled us. Nothing
                        // to do; the resume path already broadcast the
                        // "back" event.
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Grace task for {UserId} in {Slug} crashed.", leftUserId, Slug);
                    }
                });

                return; // do NOT remove participant, do NOT free seat
            }

            // Spectator OR ended-game seated player → remove immediately.
            _state.Participants.Remove(p);

            // HOST-LEAVES POLICY: lobby → kill room (creator-only destroy
            // rule still holds — if they walked, room is dead). This stays
            // outside the grace window because the host explicitly created
            // the room; we don't speculate about their return.
            if (hostLeft && _state.Status == GameStatus.Lobby)
            {
                _state.Status = GameStatus.Ended;
                _state.Result = ChessResult.Aborted;
                _pendingEvents.Enqueue(new GameEndedEvent(
                    new List<ScoreEntry>(),
                    "Host disconnected before the game began."));
            }

            await PersistStateUnsafeAsync();
            _pendingEvents.Enqueue(new ParticipantLeftEvent(userId));
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Called by the grace task after the timeout window elapses without
    /// a reconnect. Frees the seat, removes the participant, resets the
    /// board to the start position if a game was in progress (mid-game
    /// timeout → back to Lobby, NOT a forfeit, per user's intent of
    /// "chess will restart and host can assign the seat to any different
    /// person").
    /// </summary>
    private async Task OnGraceExpiredAsync(string userId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var p = _state.Participants.FirstOrDefault(x => x.UserId == userId);
            if (p is null) return;             // already cleaned up
            if (p.IsOnline) return;            // returned right at the edge — leave them be

            var seatColor = userId == _state.WhitePlayerId ? ChessColor.White
                          : userId == _state.BlackPlayerId ? ChessColor.Black
                          : (ChessColor?)null;

            // Free the seat.
            if (userId == _state.WhitePlayerId) { _state.WhitePlayerId = null; _state.WhitePlayerName = null; }
            if (userId == _state.BlackPlayerId) { _state.BlackPlayerId = null; _state.BlackPlayerName = null; }

            // Reset board + back to lobby if mid-game. The remaining
            // player isn't punished with a forfeit; instead the host
            // gets to assign a fresh opponent and a new game starts.
            if (_state.Status == GameStatus.Playing)
            {
                _state.Status = GameStatus.Lobby;
                _state.Fen = StartingFen;
                _state.Turn = ChessColor.White;
                _state.Result = ChessResult.InProgress;
                _state.MoveHistory.Clear();
            }

            _state.Participants.Remove(p);
            try { p.GraceCts?.Dispose(); } catch { }
            p.GraceCts = null;

            await PersistStateUnsafeAsync();
            if (seatColor.HasValue)
            {
                _pendingEvents.Enqueue(new SeatTimedOutEvent(
                    userId, p.Username, seatColor.Value, BuildSnapshotUnsafe()));
            }
            _pendingEvents.Enqueue(new ParticipantLeftEvent(userId));
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Host-only override: skip the remaining grace wait, free the seat
    /// now. Useful when it's obvious the disconnected player isn't
    /// coming back and the room has eager spectators waiting.
    /// </summary>
    public async Task<ActionResult> OverrideGraceWaitAsync(
        string hostUserId, string targetUserId, CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);

        // Validate + capture under the lock; cancel + process timeout
        // OUTSIDE the lock so OnGraceExpiredAsync can take it cleanly
        // and exceptions don't double-release the semaphore.
        CancellationTokenSource? cts;
        await _lock.WaitAsync(ct);
        try
        {
            if (hostUserId != _meta.HostUserId)
                return new ActionResult(false, "Only the host can skip the wait.");
            var p = _state.Participants.FirstOrDefault(x => x.UserId == targetUserId);
            if (p is null) return new ActionResult(false, "Player not in room.");
            if (p.IsOnline) return new ActionResult(false, "Player is online — nothing to override.");
            if (p.GraceCts is null) return new ActionResult(false, "No active grace timer.");
            cts = p.GraceCts;
        }
        finally { _lock.Release(); }

        try { cts.Cancel(); } catch { /* already disposed — fine */ }
        await OnGraceExpiredAsync(targetUserId, ct);
        return new ActionResult(true);
    }

    /// <summary>Default grace window before a seat auto-frees.</summary>
    public const int GraceSeconds = 60;

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

        /// <summary>
        /// When the player went offline. Persisted so a server restart
        /// during a grace window can still compute remaining time and
        /// either time out or reseat on return.
        /// </summary>
        public DateTime? LeftAtUtc { get; set; }

        /// <summary>
        /// Active grace timer for an offline seated Player. NOT persisted
        /// (in-memory only). Cancelled by JoinAsync if the player returns
        /// in time, or fires naturally to free the seat.
        /// </summary>
        [JsonIgnore]
        public CancellationTokenSource? GraceCts { get; set; }
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

/// <summary>Seat assignment / unassignment by host — clients should
/// re-render labels + board orientation from the snapshot.</summary>
public sealed record ChessSeatChangedEvent(ChessStateSnapshot Snapshot) : GameEvent;

/// <summary>A seated Player went offline. Clients show countdown +
/// host's "skip wait" button.</summary>
public sealed record PlayerDisconnectedEvent(
    string UserId, string Username, ChessColor SeatColor, int GraceSeconds) : GameEvent;

/// <summary>A previously-disconnected Player returned inside the grace
/// window. Clients dismiss the countdown banner.</summary>
public sealed record PlayerReturnedEvent(string UserId, string Username) : GameEvent;

/// <summary>Grace window expired without a return. Seat is freed; if
/// the timeout interrupted a live game, snapshot reflects board reset
/// to start + status back to Lobby.</summary>
public sealed record SeatTimedOutEvent(
    string UserId, string Username, ChessColor SeatColor, ChessStateSnapshot Snapshot) : GameEvent;
