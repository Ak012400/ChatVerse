using ChatVerse.API.Models.Games;

namespace ChatVerse.API.Services.Games;

// ============================================================
//  IGameSession — abstract contract for any game in the Gaming Hall.
//
//  The hub doesn't know about Quiz specifics — it routes events to
//  the right IGameSession (looked up by slug) and lets the session
//  handle game logic. This makes adding Chess / Tic-Tac-Toe / Ludo a
//  matter of writing a new IGameSession impl + a category entry on
//  GameSessionFactory, without touching hub/controller code.
//
//  Why not a generic <T>?
//    Different games have wildly different move payloads (a Quiz
//    answer is an int; a chess move is a from/to/promotion tuple;
//    Ludo needs a dice + token). The hub receives moves as opaque
//    JsonElement and lets each session deserialise — keeps the hub
//    surface stable across future game types.
//
//  Threading:
//    Implementations MUST be safe to call concurrently from multiple
//    SignalR connection threads. Typical pattern: SemaphoreSlim
//    inside each session for state mutation.
//
//  Persistence:
//    Sessions are Redis-backed (not memory-backed) so a Render dyno
//    restart mid-quiz doesn't kill the round. Hydration is done in
//    InitializeAsync; mutation is persisted at every state change.
// ============================================================

public interface IGameSession : IAsyncDisposable
{
    string Slug { get; }
    GameType Type { get; }
    GameStatus Status { get; }
    string HostUserId { get; }
    int PlayerCount { get; }
    int SpectatorCount { get; }

    /// <summary>
    /// Hydrate from Redis if state exists, else set up a fresh session.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    Task InitializeAsync(CancellationToken ct);

    /// <summary>
    /// Add a user. Server enforces:
    ///   - Player cap (MaxPlayers from room meta) — overflow becomes spectator
    ///   - Already-joined users get their existing role back
    ///   - Banned trust-score users rejected
    /// </summary>
    Task<JoinResult> JoinAsync(
        string userId,
        string username,
        GameRole requestedRole,
        CancellationToken ct);

    /// <summary>
    /// Remove a user (explicit Leave or disconnect cleanup). If the host
    /// leaves mid-game, the session ends — no host-transfer for v1, keeps
    /// the lifecycle simple.
    /// </summary>
    Task LeaveAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Begin the game. Only the host can call this; only valid from
    /// Lobby status. Returns Accepted=false with a reason otherwise.
    /// </summary>
    Task<ActionResult> StartAsync(string requestingUserId, CancellationToken ct);

    /// <summary>
    /// Process a player move. Payload shape is game-specific:
    ///   - Quiz: <c>{"questionId":"...", "choiceIndex":2}</c>
    ///   - Chess: <c>{"from":"e2","to":"e4"}</c>
    /// Implementations validate type, scope, deadline, and update state.
    /// Spectator submits are auto-rejected by the hub before reaching here.
    /// </summary>
    Task<ActionResult> SubmitMoveAsync(
        string userId,
        System.Text.Json.JsonElement payload,
        CancellationToken ct);

    /// <summary>
    /// Snapshot for a freshly-joining client or for "reconnect →
    /// catch up". The view shape varies with role — players see
    /// in-flight question, spectators see masked-options only.
    /// </summary>
    Task<GameRoomSnapshot> GetSnapshotAsync(string forUserId, CancellationToken ct);

    /// <summary>
    /// Driven by GameTickerService once per second. Sessions use this
    /// to advance deadline-based state (e.g. push next quiz question
    /// when the current one's timer expires). Returns true if state
    /// changed and the hub should re-broadcast.
    ///
    /// Keeping the tick external means we don't need a Timer per
    /// session — one shared background loop handles every active game,
    /// which scales much better when 100+ rooms are running.
    /// </summary>
    Task<bool> TickAsync(DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Append a chat message. Returns the persisted message (with
    /// server-assigned Id + timestamp) so the hub can broadcast it.
    /// </summary>
    Task<GameChatMessage> AppendChatAsync(
        string senderId,
        string senderUsername,
        GameRole senderRole,
        string text,
        CancellationToken ct);

    /// <summary>
    /// Pop accumulated GameEvent items emitted since the previous
    /// call. The hub + ticker poll this after every state-changing
    /// invocation and broadcast each event to the room's SignalR
    /// group. Implementations must guarantee each event is yielded
    /// exactly once (typically via a ConcurrentQueue).
    /// </summary>
    IReadOnlyList<GameEvent> DrainEvents();
}

/// <summary>
/// Outcome of a join attempt. Role may differ from requested role if
/// the player slots were full and the user fell back to spectator.
/// </summary>
public record JoinResult(
    bool Accepted,
    GameRole AssignedRole,
    string? Reason = null);

public record ActionResult(
    bool Accepted,
    string? Reason = null,
    object? Payload = null);
