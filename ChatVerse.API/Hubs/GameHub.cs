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
        _logger.LogInformation(
            "GameHub: {Username} connected [{ConnectionId}]",
            username, Context.ConnectionId);
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
        if (session is not QuizSession quiz) return;
        var events = quiz.DrainEvents();
        foreach (var ev in events)
            await _ticker.BroadcastAsync(quiz.Slug, ev, ct);
    }
}
