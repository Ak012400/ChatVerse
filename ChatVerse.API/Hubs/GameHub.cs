using ChatVerse.API.Extensions;
using ChatVerse.API.Models.Games;
using ChatVerse.API.Services.Games;
using ChatVerse.Infrastructure.Persistence.Redis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Text.Json;

namespace ChatVerse.API.Hubs;

// ============================================================
//  GameHub — SignalR endpoint for Gaming Hall rooms.
//
//  Surface (client → server):
//    JoinRoom(slug)           — actually attaches to the SignalR group;
//                                actual session join already happened via
//                                POST /api/game-rooms/{slug}/join.
//    StartQuiz(slug)          — host starts the round
//    SubmitAnswer(slug,...)   — player submits
//    SendChat(slug, text)     — anyone in room
//    LeaveRoom(slug)          — explicit leave
//
//  Surface (server → client):
//    RoomSnapshot, QuestionPushed, QuestionRevealed,
//    ScoreUpdated, GameEnded, ParticipantJoined, ParticipantLeft,
//    ChatMessage
//
//  Why the REST + Hub split for joining?
//    REST handles authorization + cap checks + persisting the new
//    participant. The hub then just routes the user's connection
//    to the right group. This split keeps the hub thin (no DB
//    writes on the connection path) and means a transient SignalR
//    disconnect doesn't make the user "leave" the room — they
//    can reconnect and re-attach to the same membership.
//
//  Connection lifetime:
//    The slug a user "is in" is tracked in Redis (conn:game:{cid})
//    so OnDisconnectedAsync can mark them offline without a scan.
// ============================================================

[Authorize]
public class GameHub : Hub
{
    private readonly GameSessionRegistry _registry;
    private readonly GameTickerService _ticker;
    private readonly RedisService _redis;
    private readonly ILogger<GameHub> _logger;

    public GameHub(
        GameSessionRegistry registry,
        GameTickerService ticker,
        RedisService redis,
        ILogger<GameHub> logger)
    {
        _registry = registry;
        _ticker = ticker;
        _redis = redis;
        _logger = logger;
    }

    /// <summary>
    /// SignalR group name for a room. Centralised so hub + ticker
    /// can't drift on naming.
    /// </summary>
    public static string RoomGroup(string slug) => $"game:room:{slug}";

    // ───────────────────────────────────────────────────────────────
    //  Connection lifecycle
    // ───────────────────────────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        var username = JwtService.GetUsername(Context.User!);
        var userId = JwtService.GetUserId(Context.User!).ToString();
        // Richer connection-arrival log — the userId helps cross-reference
        // with REST audit trails when debugging multi-tab / multi-device
        // sessions, and the explicit "GameHub" prefix makes filtering in
        // Render's log search a single-keyword job.
        _logger.LogInformation(
            "GameHub: {Username} (userId={UserId}) connected [conn={ConnectionId}]",
            username, userId, Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Tracked-slug bookkeeping — clean up Redis pointers so the
        // user doesn't appear "online" in a room they were in last
        // session.
        var slug = await _redis.GetStringAsync($"conn:game:{Context.ConnectionId}");
        if (!string.IsNullOrEmpty(slug))
        {
            await _redis.DeleteKeyAsync($"conn:game:{Context.ConnectionId}");
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(slug));
            // We do NOT call session.LeaveAsync here — a refresh /
            // reconnect should NOT make the user lose their slot.
            // Explicit LeaveRoom is the only path that frees a slot.
        }
        await base.OnDisconnectedAsync(exception);
    }

    // ───────────────────────────────────────────────────────────────
    //  JoinRoom — attach SignalR connection + push initial snapshot
    // ───────────────────────────────────────────────────────────────

    public async Task JoinRoom(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return;

        var userId = JwtService.GetUserId(Context.User!).ToString();
        var session = await _registry.GetOrLoadAsync(slug, Context.ConnectionAborted);
        if (session is null)
        {
            await Clients.Caller.SendAsync("Error",
                new { message = "Room not found or has expired." });
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(slug));
        await _redis.SetStringAsync(
            $"conn:game:{Context.ConnectionId}", slug, TimeSpan.FromHours(2));

        var snapshot = await session.GetSnapshotAsync(userId, Context.ConnectionAborted);
        await Clients.Caller.SendAsync("RoomSnapshot", snapshot);

        // Flush any events the session has queued (e.g. a recent
        // ParticipantJoined from the REST join that hadn't broadcast
        // yet). Spec-wise the ticker would catch them within 1s but
        // doing it here gives instant feedback.
        await FlushSessionEventsAsync(session, Context.ConnectionAborted);
    }

    // ───────────────────────────────────────────────────────────────
    //  StartQuiz — host-only
    // ───────────────────────────────────────────────────────────────

    public async Task StartQuiz(string slug)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var session = await _registry.GetOrLoadAsync(slug, Context.ConnectionAborted);
        if (session is null)
        {
            await Clients.Caller.SendAsync("Error",
                new { message = "Room not found." });
            return;
        }

        var result = await session.StartAsync(userId, Context.ConnectionAborted);
        if (!result.Accepted)
        {
            await Clients.Caller.SendAsync("Error", new { message = result.Reason });
            return;
        }
        await FlushSessionEventsAsync(session, Context.ConnectionAborted);
    }

    // ───────────────────────────────────────────────────────────────
    //  SubmitAnswer
    // ───────────────────────────────────────────────────────────────

    public async Task SubmitAnswer(string slug, string questionId, int choiceIndex)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var session = await _registry.GetOrLoadAsync(slug, Context.ConnectionAborted);
        if (session is null) return;

        var payload = JsonSerializer.SerializeToElement(
            new QuizAnswerSubmit(questionId, choiceIndex));
        var result = await session.SubmitMoveAsync(userId, payload, Context.ConnectionAborted);

        // Personal ack — lets the client lock the UI in if accepted
        // without having to wait for the next ScoreUpdated broadcast.
        await Clients.Caller.SendAsync("AnswerAck",
            new { accepted = result.Accepted, reason = result.Reason });

        await FlushSessionEventsAsync(session, Context.ConnectionAborted);
    }

    // ───────────────────────────────────────────────────────────────
    //  ReactToJoke — Jokes-mode equivalent of SubmitAnswer.
    //
    //  Reaction is last-write-wins, so players can change their mind
    //  inside the 30-second window. The session's broadcast cadence
    //  handles UI updates; this method just bridges the RPC.
    // ───────────────────────────────────────────────────────────────
    public async Task ReactToJoke(string slug, string jokeId, string reaction)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var session = await _registry.GetOrLoadAsync(slug, Context.ConnectionAborted);
        if (session is null) return;

        // Defensive enum parse — don't crash the hub if a client sends
        // a malformed reaction name, just reject with an ack.
        if (!Enum.TryParse<JokeReactionType>(reaction, ignoreCase: true, out var parsed))
        {
            await Clients.Caller.SendAsync("AnswerAck",
                new { accepted = false, reason = "Unknown reaction." });
            return;
        }

        var payload = JsonSerializer.SerializeToElement(
            new JokeReactSubmit(jokeId, parsed));
        var result = await session.SubmitMoveAsync(userId, payload, Context.ConnectionAborted);

        await Clients.Caller.SendAsync("AnswerAck",
            new { accepted = result.Accepted, reason = result.Reason });

        await FlushSessionEventsAsync(session, Context.ConnectionAborted);
    }

    // ───────────────────────────────────────────────────────────────
    //  CHESS — move submission, resign, snapshot pull, join requests
    //
    //  All chess actions guard against guest auth — we re-check the
    //  JWT's "is_guest" claim before any mutating call.
    // ───────────────────────────────────────────────────────────────

    public async Task SubmitChessMove(string slug, string san, string uci, string fenAfter)
    {
        // Guests can connect to the hub and chat, but mutations are
        // strictly registered-only.
        if (IsGuest()) {
            await Clients.Caller.SendAsync("Error",
                new { message = "Sign in to make moves." });
            return;
        }

        var userId = JwtService.GetUserId(Context.User!).ToString();
        var session = await _registry.GetOrLoadAsync(slug, Context.ConnectionAborted);
        if (session is null) return;

        var payload = JsonSerializer.SerializeToElement(new ChessMoveSubmit(san, uci, fenAfter));
        var result = await session.SubmitMoveAsync(userId, payload, Context.ConnectionAborted);

        await Clients.Caller.SendAsync("ChessMoveAck",
            new { accepted = result.Accepted, reason = result.Reason });
        await FlushSessionEventsAsync(session, Context.ConnectionAborted);
    }

    public async Task ResignChess(string slug)
    {
        if (IsGuest()) return;
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var session = await _registry.GetOrLoadAsync(slug, Context.ConnectionAborted);
        if (session is not ChessSession chess) return;

        var result = await chess.ResignAsync(userId, Context.ConnectionAborted);
        await Clients.Caller.SendAsync("ChessMoveAck",
            new { accepted = result.Accepted, reason = result.Reason });
        await FlushSessionEventsAsync(session, Context.ConnectionAborted);
    }

    /// <summary>
    /// Late-joiner pulls full chess board state (FEN + history +
    /// seating) so the page can paint without waiting for the next
    /// broadcast.
    /// </summary>
    public async Task GetChessState(string slug)
    {
        var session = await _registry.GetOrLoadAsync(slug, Context.ConnectionAborted);
        if (session is not ChessSession chess) return;
        var snap = await chess.GetChessSnapshotAsync(Context.ConnectionAborted);
        await Clients.Caller.SendAsync("ChessStateSnapshot", snap);
    }

    /// <summary>
    /// Host pulls the current pending-request list (e.g. on opening
    /// the requests panel).
    /// </summary>
    public async Task GetPendingRequests(string slug)
    {
        var session = await _registry.GetOrLoadAsync(slug, Context.ConnectionAborted);
        if (session is not ChessSession chess) return;
        var list = await chess.GetPendingRequestsAsync(Context.ConnectionAborted);
        await Clients.Caller.SendAsync("PendingRequests", list);
    }

    public async Task ApproveJoinRequest(string slug, string requestId)
    {
        if (IsGuest()) return;
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var session = await _registry.GetOrLoadAsync(slug, Context.ConnectionAborted);
        if (session is not ChessSession chess) return;

        var result = await chess.ApproveJoinRequestAsync(userId, requestId, Context.ConnectionAborted);
        await Clients.Caller.SendAsync("JoinRequestAck",
            new { accepted = result.Accepted, reason = result.Reason });
        await FlushSessionEventsAsync(session, Context.ConnectionAborted);
    }

    public async Task DeclineJoinRequest(string slug, string requestId)
    {
        if (IsGuest()) return;
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var session = await _registry.GetOrLoadAsync(slug, Context.ConnectionAborted);
        if (session is not ChessSession chess) return;

        var result = await chess.DeclineJoinRequestAsync(userId, requestId, Context.ConnectionAborted);
        await Clients.Caller.SendAsync("JoinRequestAck",
            new { accepted = result.Accepted, reason = result.Reason });
        await FlushSessionEventsAsync(session, Context.ConnectionAborted);
    }

    // Pull "is_guest" from the JWT. Existing JwtClaims constant
    // already defines this — see Domainconstants.cs.
    private bool IsGuest()
    {
        var claim = Context.User?.FindFirst(ChatVerse.Domain.Constants.JwtClaims.IsGuest)?.Value;
        return string.Equals(claim, "true", StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────────────────────────────────────────
    //  Spectator → Player seat request
    //
    //  Works for any IGameSession that implements the upgrade path.
    //  Chess uses it; Quiz/Jokes fall back to a friendly "wait for
    //  next round" message until we add similar logic to them.
    // ───────────────────────────────────────────────────────────────
    public async Task RequestPlayerSeat(string slug)
    {
        if (IsGuest()) {
            await Clients.Caller.SendAsync("Error",
                new { message = "Sign in to request a player seat." });
            return;
        }

        var userId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);
        var session = await _registry.GetOrLoadAsync(slug, Context.ConnectionAborted);
        if (session is null) return;

        if (session is ChessSession chess)
        {
            var result = await chess.RequestPlayerSeatAsync(userId, username, Context.ConnectionAborted);
            await Clients.Caller.SendAsync("SeatRequestAck",
                new { accepted = result.Accepted, reason = result.Reason });
            await FlushSessionEventsAsync(session, Context.ConnectionAborted);
            return;
        }

        await Clients.Caller.SendAsync("SeatRequestAck",
            new { accepted = false, reason = "Wait for the next round to join as a player." });
    }

    // ───────────────────────────────────────────────────────────────
    //  Host invites
    //
    //  Host sends a targeted notification; recipient gets a toast
    //  with Accept/Decline; Accept resolves the invite token and
    //  returns the slug so the page can navigate to /play/{slug}.
    //  10-min TTL on Redis token; one-shot consumption.
    // ───────────────────────────────────────────────────────────────
    public async Task InviteToGameRoom(string targetUserId, string slug)
    {
        if (IsGuest()) return;
        var fromUserId = JwtService.GetUserId(Context.User!).ToString();
        var fromUsername = JwtService.GetUsername(Context.User!);

        var session = await _registry.GetOrLoadAsync(slug, Context.ConnectionAborted);
        if (session is null)
        {
            await Clients.Caller.SendAsync("Error",
                new { message = "Room not found." });
            return;
        }
        if (session.HostUserId != fromUserId)
        {
            await Clients.Caller.SendAsync("Error",
                new { message = "Only the host can invite." });
            return;
        }

        var snap = await session.GetSnapshotAsync(fromUserId, Context.ConnectionAborted);
        var inviteId = Guid.NewGuid().ToString("N")[..12];
        var token = $"{fromUserId}:{targetUserId}:{slug}";
        await _redis.SetStringAsync(
            ChatVerse.Domain.Constants.RedisKeys.GameInvite(inviteId),
            token,
            TimeSpan.FromMinutes(10));

        var invite = new GameRoomInvite(
            InviteId: inviteId,
            Slug: slug,
            RoomName: snap.Room.Name,
            Type: snap.Room.Type,
            FromUsername: fromUsername,
            SentAtUtc: DateTime.UtcNow);

        // Per-user push — only the targeted user sees this. SignalR's
        // Clients.User(...) routes by the NameIdentifier claim, which
        // our JwtService already populates with the user UUID.
        await Clients.User(targetUserId).SendAsync("GameRoomInvite", invite);
        await Clients.Caller.SendAsync("InviteSent",
            new { inviteId, targetUserId });
    }

    /// <summary>
    /// Explicit leave — removes the caller from the participant list.
    /// If the LEAVING user is the host, treats the leave as an implicit
    /// room close: broadcasts RoomClosed, drops the session. Otherwise
    /// only the caller is removed; the room stays alive for others.
    ///
    /// Important: We do this here (not in OnDisconnectedAsync) because
    /// a disconnect could be transient — a refresh, network blip, etc.
    /// LeaveRoom is the user's deliberate intent to abandon the room.
    /// </summary>
    public async Task LeaveRoom(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return;
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var session = await _registry.GetOrLoadAsync(slug, Context.ConnectionAborted);
        if (session is null) return;

        var hostLeaving = session.HostUserId == userId;

        // Tell the session to clean up state for this user first; the
        // ChessSession.LeaveAsync also handles host-leaves-lobby-aborts
        // already (see that file).
        await session.LeaveAsync(userId, Context.ConnectionAborted);
        await FlushSessionEventsAsync(session, Context.ConnectionAborted);

        // Caller leaves the SignalR group so they stop receiving room
        // events. Done after the FlushSessionEvents so we still get the
        // ParticipantLeft broadcast back to the caller (for symmetry).
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(slug));
        await _redis.DeleteKeyAsync($"conn:game:{Context.ConnectionId}");

        // Host leaving = room closes. Broadcast + drop registry entry
        // so the Active Games panel + ListActive endpoint stop showing
        // it. Fire-and-forget the drop so the caller's Leave RPC returns
        // immediately.
        if (hostLeaving)
        {
            await Clients.Group(RoomGroup(slug)).SendAsync(
                "RoomClosed",
                new { slug, reason = "Host left the room." },
                Context.ConnectionAborted);

            _ = Task.Run(async () =>
            {
                try { await _registry.DropAsync(slug); }
                catch (Exception ex) { _logger.LogWarning(ex, "DropAsync after host-leave failed for {Slug}", slug); }
            });
        }
    }

    /// <summary>
    /// Creator explicitly closes the room. Broadcasts a "RoomClosed"
    /// event so all members of the SignalR group can navigate back to
    /// chat. Then drops the session from the registry so it disappears
    /// from the Active Games panel.
    /// </summary>
    public async Task EndRoom(string slug)
    {
        if (IsGuest()) return;
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var session = await _registry.GetOrLoadAsync(slug, Context.ConnectionAborted);
        if (session is null) return;
        if (session.HostUserId != userId)
        {
            await Clients.Caller.SendAsync("Error",
                new { message = "Only the host can end the room." });
            return;
        }

        // Tell everyone in the room to bounce.
        await Clients.Group(RoomGroup(slug)).SendAsync(
            "RoomClosed",
            new { slug, reason = "Host closed the room." },
            Context.ConnectionAborted);

        // Fire-and-forget drop — we don't want the caller to wait on
        // the Redis cleanup. The broadcast above has already informed
        // every client.
        _ = Task.Run(async () =>
        {
            try { await _registry.DropAsync(slug); }
            catch (Exception ex) { _logger.LogWarning(ex, "DropAsync failed for {Slug}", slug); }
        });
    }

    public async Task AcceptInvite(string inviteId)
    {
        if (IsGuest()) return;
        var userId = JwtService.GetUserId(Context.User!).ToString();

        var token = await _redis.GetStringAsync(
            ChatVerse.Domain.Constants.RedisKeys.GameInvite(inviteId));
        if (string.IsNullOrEmpty(token))
        {
            await Clients.Caller.SendAsync("InviteAck",
                new { accepted = false, reason = "Invite expired or invalid." });
            return;
        }
        var parts = token.Split(':');
        if (parts.Length != 3 || parts[1] != userId)
        {
            await Clients.Caller.SendAsync("InviteAck",
                new { accepted = false, reason = "Invite was not addressed to you." });
            return;
        }
        var slug = parts[2];
        await _redis.DeleteKeyAsync(ChatVerse.Domain.Constants.RedisKeys.GameInvite(inviteId));

        await Clients.Caller.SendAsync("InviteAck",
            new { accepted = true, slug });
    }

    // ───────────────────────────────────────────────────────────────
    //  SendChat — players + spectators
    // ───────────────────────────────────────────────────────────────

    public async Task SendChat(string slug, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var userId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);
        var session = await _registry.GetOrLoadAsync(slug, Context.ConnectionAborted);
        if (session is null) return;

        // Look up the sender's role in the session so we can tag the
        // message. Cheaper than asking the session — we read the
        // snapshot, but session caches it in memory already.
        var snapshot = await session.GetSnapshotAsync(userId, Context.ConnectionAborted);
        var role = snapshot.ViewerRole ?? GameRole.Spectator;

        var msg = await session.AppendChatAsync(
            userId, username, role, text, Context.ConnectionAborted);

        await Clients.Group(RoomGroup(slug)).SendAsync("ChatMessage", msg);
    }

    // ───────────────────────────────────────────────────────────────
    //  LeaveRoom — explicit leave (vs disconnect)
    // ───────────────────────────────────────────────────────────────

    public async Task LeaveRoom(string slug)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var session = await _registry.GetOrLoadAsync(slug, Context.ConnectionAborted);
        if (session is null) return;

        await session.LeaveAsync(userId, Context.ConnectionAborted);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(slug));
        await _redis.DeleteKeyAsync($"conn:game:{Context.ConnectionId}");

        await FlushSessionEventsAsync(session, Context.ConnectionAborted);
    }

    // ───────────────────────────────────────────────────────────────
    //  Drain + broadcast pending session events
    // ───────────────────────────────────────────────────────────────

    private async Task FlushSessionEventsAsync(IGameSession session, CancellationToken ct)
    {
        // DrainEvents is part of the IGameSession contract — every
        // concrete session (Quiz, Jokes, future Chess…) exposes it.
        foreach (var ev in session.DrainEvents())
            await _ticker.BroadcastAsync(session.Slug, ev, ct);
    }
}
