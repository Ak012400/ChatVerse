using System.Text.Json.Serialization;

namespace ChatVerse.API.Models.Games;

// NOTE on enum serialisation:
// System.Text.Json's default behaviour ships enums as integers. That
// works fine inside the .NET process but is a terrible wire format
// for a React client — every "status === 2" check would be opaque.
// Each enum below carries a [JsonConverter(typeof(JsonStringEnumConverter))]
// attribute so it flows over both REST and SignalR as PascalCase
// strings ("Playing", "Spectator", etc.). Adding the attribute on
// each enum is safer than configuring a global MVC option because
// it doesn't touch any of the existing controllers' wire formats.

// ============================================================
//  GAMING HALL — shared types
//
//  This file defines the public contract between the GameHub /
//  GameRoomController and the React frontend. Anything the client
//  reads or writes lives here; internal session state lives next
//  to the IGameSession implementations.
//
//  Convention: enums are serialised as strings (see JsonStringEnumConverter
//  on Program.cs JSON options) so the React side gets readable values
//  like "quiz" instead of numbers — easier to debug.
// ============================================================

/// <summary>
/// Which game a room is hosting. Adding a new game type means:
///   1) add an enum member here
///   2) implement IGameSession
///   3) register the implementation in GameSessionFactory
/// The hub + controller don't need to change.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameType
{
    Quiz,
    Jokes,
    Trivia,
    Chess,
    Ludo,
}

/// <summary>
/// Players actively participate (submit answers, make moves). Spectators
/// only watch and chat. Splitting the role at join time lets us cap
/// players without limiting audience size — critical for "viral" rooms
/// where one quiz might attract 50+ watchers.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameRole
{
    Player,
    Spectator,
}

/// <summary>
/// Lifecycle of a single game session.
///   Lobby   — room exists, waiting for host to press Start
///   Playing — round in progress; submits accepted
///   Ended   — final scoreboard frozen; room read-only until reset
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameStatus
{
    Lobby,
    Playing,
    Ended,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuizDifficulty
{
    Any,
    Easy,
    Medium,
    Hard,
}

/// <summary>
/// How quiz points are awarded per question.
///   Speed        — every correct answer scores 100 + speed bonus (v1 behaviour).
///   FirstCorrect — ONLY the first correct answer scores, flat +1.
///                  Cutthroat buzzer-style — Arun's Quiz v2 default.
/// Additive: old rooms / old clients omit the field and deserialise
/// to Speed, so nothing existing changes behaviour.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScoringMode
{
    Speed,
    FirstCorrect,
}

/// <summary>
/// OpenTriviaDB categories we expose. Keep this list curated rather
/// than dumping all 24 — too many options paralyse first-time users.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuizCategory
{
    Any,
    General,
    Books,
    Film,
    Music,
    Sports,
    Geography,
    History,
    Politics,
    Science,
    Computers,
    Mythology,
    Animals,
}

// ─── REST request/response shapes ──────────────────────────────

public record CreateGameRoomRequest(
    string Name,
    GameType Type,
    int MaxPlayers,
    QuizCategory Category,
    QuizDifficulty Difficulty,
    int QuestionCount,
    int SecondsPerQuestion)
{
    /// <summary>
    /// Public rooms appear in the chat's Active Games panel — anyone
    /// in the source chat can browse + join. Private rooms are hidden
    /// from the panel and only discoverable via the slug-URL the host
    /// explicitly shares. Defaults to public for backward-compat with
    /// pre-Phase-2 callers that don't send this field.
    /// </summary>
    public bool IsPublic { get; init; } = true;

    /// <summary>
    /// Scoring rules for this room. Defaults to Speed so pre-v2
    /// clients (and Jokes/Chess creates, which ignore it) are
    /// unaffected. The launcher sends FirstCorrect for new quizzes.
    /// </summary>
    public ScoringMode ScoringMode { get; init; } = ScoringMode.Speed;

    /// <summary>
    /// The chat-room slug this game was launched from (e.g.
    /// "gaming-lounge"). Active-games discovery filters on this so
    /// games started in Gaming Lounge don't bleed into Mini Game's
    /// panel and vice-versa. Null for direct /games-page creates.
    /// </summary>
    public string? SourceChatSlug { get; init; }
}

public record JoinGameRoomRequest(GameRole Role);

/// <summary>
/// Lightweight room descriptor for the GamingHallPage list view —
/// designed to be cheap to compute from Redis only (no DB round-trip).
/// </summary>
public record GameRoomDto(
    string Slug,
    string Name,
    GameType Type,
    GameStatus Status,
    int PlayerCount,
    int MaxPlayers,
    int SpectatorCount,
    string HostUsername,
    DateTime CreatedAtUtc)
{
    /// <summary>
    /// Mirrors the meta flag. Frontend filters here too, defensively —
    /// the backend already drops private rooms from ListActive, but
    /// future direct-link previews can still distinguish the pill.
    /// </summary>
    public bool IsPublic { get; init; } = true;

    /// <summary>
    /// Marks the always-on random room so the UI can show a special
    /// "🎲 Always on" badge and treat it differently (e.g. "Join" vs
    /// "Watch" depending on slot availability).
    /// </summary>
    public bool IsRandom { get; init; }

    /// <summary>Source chat slug — surfaced so the panel can verify match.</summary>
    public string? SourceChatSlug { get; init; }
}

// ─── Quiz-specific payloads ────────────────────────────────────

/// <summary>
/// Server-side representation. Carries the CorrectIndex which is NEVER
/// sent to clients — we strip it on the way out via <see cref="QuizQuestionPublic"/>.
/// </summary>
public record QuizQuestion(
    string Id,
    string Category,
    string Difficulty,
    string Question,
    IReadOnlyList<string> Options,
    int CorrectIndex);

/// <summary>
/// What the client receives when a new question is pushed. The
/// CorrectIndex is omitted — clients only learn the answer when the
/// round ends (so spectators can't whisper the answer to players in
/// the chat panel).
/// </summary>
public record QuizQuestionPublic(
    string Id,
    string Category,
    string Difficulty,
    string Question,
    IReadOnlyList<string> Options,
    int QuestionNumber,
    int TotalQuestions,
    DateTime DeadlineUtc);

public record QuizAnswerSubmit(string QuestionId, int ChoiceIndex);

/// <summary>
/// Sent to everyone when a question's deadline passes (or all players
/// have submitted, whichever is first). Reveals correct answer +
/// what each player chose — drives the post-question recap animation.
/// </summary>
public record QuizAnswerReveal(
    string QuestionId,
    int CorrectIndex,
    string CorrectAnswer,
    Dictionary<string, PlayerChoice> ChoicesByPlayer,
    int RoundDurationMs);

public record PlayerChoice(int ChoiceIndex, int ResponseTimeMs);

public record ScoreEntry(
    string UserId,
    string Username,
    int Score,
    int CorrectAnswers,
    int AnsweredCount,
    double AverageResponseMs,
    // Current consecutive-correct run — drives the 🔥 badge. Default
    // keeps older positional constructions compiling unchanged.
    int Streak = 0);

/// <summary>
/// Snapshot a late-joining player or spectator receives on connect.
/// Replaces the need for them to ask "what's happening?" — they get
/// the full picture (current question if mid-round, scoreboard,
/// participants, chat tail).
/// </summary>
public record GameRoomSnapshot(
    GameRoomDto Room,
    GameRole? ViewerRole,
    QuizQuestionPublic? CurrentQuestion,
    IReadOnlyList<ScoreEntry> Scoreboard,
    IReadOnlyList<GameParticipant> Participants,
    IReadOnlyList<GameChatMessage> RecentChat,
    // Quiz v2 director mode: userIds of spectators who raised a hand
    // for a seat. Default keeps Jokes/Chess snapshot builders compiling
    // unchanged; null on the wire for non-quiz rooms.
    IReadOnlyList<string>? SeatRequests = null);

public record GameParticipant(
    string UserId,
    string Username,
    GameRole Role,
    bool IsHost,
    bool IsOnline);

public record GameChatMessage(
    string Id,
    string SenderId,
    string SenderUsername,
    GameRole SenderRole,
    string Text,
    DateTime AtUtc);

public record SendChatRequest(string Text);

// ============================================================
//  JOKES MODE — DTOs for the second game type.
//
//  Reuses QuizRoomMeta + QuizSettings on the meta side (QuestionCount
//  doubles as JokeCount, SecondsPerQuestion as SecondsPerJoke) so the
//  REST controller and registry need zero schema changes. Only the
//  in-flight event payloads are unique to Jokes.
// ============================================================

/// <summary>
/// Four-emoji reaction palette for jokes. Wide enough that everyone
/// finds a button that fits their taste, narrow enough that the bar
/// chart stays readable.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JokeReactionType
{
    Laugh,      // 😂
    Meh,        // 😐
    Skull,      // 💀  ("dead"-funny / cringe — context-dependent)
    EyeRoll,    // 🙄
}

/// <summary>
/// What the client sees when a fresh joke is pushed by the bot.
/// </summary>
public record JokePushed(
    string Id,
    string Text,
    int JokeNumber,
    int TotalJokes,
    DateTime DeadlineUtc);

/// <summary>
/// Live reaction counts — broadcast on every submission so the bar
/// chart animates in near-real-time. Keys are reaction enum names.
/// </summary>
public record JokeReactionsUpdated(
    string JokeId,
    Dictionary<JokeReactionType, int> Counts,
    int TotalReactions);

/// <summary>
/// Per-player reaction submission. Last write wins — players can
/// change their pick until the deadline.
/// </summary>
public record JokeReactSubmit(string JokeId, JokeReactionType Reaction);

/// <summary>
/// What the client sees when the joke is finished — final counts
/// plus the "winner" reaction so the UI can show "Most people: 😂".
/// </summary>
public record JokeRevealed(
    string JokeId,
    string Text,
    Dictionary<JokeReactionType, int> Counts,
    JokeReactionType TopReaction);

/// <summary>
/// One row in the final Funny Stats leaderboard. Sort by LaughCount
/// (descending) to declare the "funniest joke" of the round.
/// </summary>
public record JokeFinalStat(
    string JokeId,
    string Text,
    int LaughCount,
    int TotalReactions);

// ============================================================
//  AMBIENT QUESTIONS — the background trivia ticker that runs
//  in gameable chat rooms (Gaming Lounge, Mini Game) to seed
//  conversation. NOT tied to game sessions — these are just
//  one-shot prompts pushed via the existing ChatHub.
// ============================================================

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AmbientQuestionMode
{
    /// <summary>4-option MCQ. Tapping an option seeds a chat reply.</summary>
    Mcq,
    /// <summary>Open-ended prompt. Users reply via normal chat.</summary>
    Discussion,
}

public record AmbientQuestion(
    string Id,
    AmbientQuestionMode Mode,
    string Text,
    IReadOnlyList<string>? Options,
    string Category,
    DateTime EmittedAtUtc);

// ============================================================
//  ROLLING QUIZ — the always-on, scored quiz that runs in
//  #general. Unlike AmbientQuestion (passive, no scoring) and
//  unlike QuizSession (started/stopped game flow), this is a
//  background ticker with a per-day leaderboard scoped to the
//  #general room itself.
//
//  Scoring:
//   1st correct  → 100 points
//   2nd correct  → 70
//   3rd correct  → 50
//   others       → 30
//   wrong/late   → 0
//
//  Session = UTC day. Leaderboard resets at midnight UTC so
//  there's a fresh start every day — no permanent dominance
//  by users who happened to be early.
// ============================================================

/// <summary>
/// What the client sees when a new rolling-quiz question goes live.
/// CorrectIndex is deliberately omitted — only revealed at deadline
/// so users can't peek by inspecting the network payload.
/// </summary>
public record RollingQuizQuestion(
    string Id,
    string Category,
    string Difficulty,
    string Question,
    IReadOnlyList<string> Options,
    DateTime DeadlineUtc,
    string SessionId);

public record RollingQuizAnswerSubmit(string QuestionId, int ChoiceIndex);

/// <summary>
/// What the server pushes when a player submits a correct answer
/// before the deadline. The rank is the 1-based position of THIS
/// answer in the correct-answers ordering (1 = first to answer).
/// Clients use it to render the "🥇 / 🥈 / 🥉" badges live.
/// </summary>
public record RollingQuizScored(
    string UserId,
    string Username,
    int Rank,
    int PointsAwarded,
    int RunningTotal);

/// <summary>
/// Final reveal at deadline. Includes the correct answer so the
/// UI can colour options in retrospect. Counts let us show "5 got
/// it right out of 12 who answered".
/// </summary>
public record RollingQuizRevealed(
    string QuestionId,
    int CorrectIndex,
    string CorrectAnswer,
    int CorrectAnswerCount,
    int TotalSubmissionCount);

public record RollingQuizLeaderEntry(
    string UserId,
    string Username,
    int Score,
    int CorrectAnswers,
    int TotalAttempts);

/// <summary>
/// Sent on join + after every scoring update so late-joiners see
/// the up-to-date board. Top 10 only — anyone outside top-10 can
/// see their own row separately via the "you" pin.
/// </summary>
public record RollingQuizLeaderboard(
    string SessionId,
    IReadOnlyList<RollingQuizLeaderEntry> Top,
    RollingQuizLeaderEntry? YouRow);

// ============================================================
//  CHESS — DTOs for the 2-player real-time chess game.
//
//  Server is move-RELAY authoritative (turn order + state
//  tracking) but NOT validation-authoritative for v1 — clients
//  use chess.js for move legality. If cheating becomes an
//  issue, v2 will add server-side validation via a NuGet
//  chess library. Casual two-friend matches don't need this yet.
// ============================================================

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChessColor
{
    White,
    Black,
}

/// <summary>The four Ludo seats, in turn order.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LudoColor
{
    Red,
    Green,
    Yellow,
    Blue,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChessResult
{
    /// <summary>Game is still in progress.</summary>
    InProgress,
    WhiteWins,
    BlackWins,
    Draw,
    Aborted,
}

/// <summary>
/// One half-ply move. SAN ("e4", "Nxf3", "O-O") is what chess.js
/// exposes for display + history; UCI ("e2e4", "g1f3", "e1g1") is
/// what we feed back into chess.js on reconnect to replay state.
/// We send both rather than reconstructing one from the other
/// because both libraries already produce both for free.
/// </summary>
public record ChessMove(
    string San,
    string Uci,
    /// <summary>FEN string AFTER this move was applied. Lets new
    /// joiners paint the current position instantly without
    /// replaying every move.</summary>
    string FenAfter,
    /// <summary>Who made this move — for spectators rendering
    /// "Alice played e4".</summary>
    ChessColor By,
    DateTime AtUtc);

/// <summary>
/// Pushed to all room members when a move is accepted. Includes
/// updated game-end state if the move triggered checkmate /
/// stalemate / threefold / 50-move.
/// </summary>
public record ChessMovePushed(
    ChessMove Move,
    int MoveNumber,
    /// <summary>Whose turn it is AFTER this move.</summary>
    ChessColor TurnAfter,
    ChessResult Result,
    /// <summary>Reason text when Result != InProgress
    /// (e.g. "Checkmate", "Stalemate", "Resigned").</summary>
    string? ResultDetail);

public record ChessMoveSubmit(string San, string Uci, string FenAfter);

/// <summary>
/// Snapshot the client paints from on join / reconnect. Empty
/// MoveHistory + starting FEN means the game hasn't begun.
/// </summary>
public record ChessStateSnapshot(
    /// <summary>Current FEN. The "starting" FEN for an empty board
    /// is the standard "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1".</summary>
    string Fen,
    ChessColor Turn,
    ChessResult Result,
    IReadOnlyList<ChessMove> MoveHistory,
    /// <summary>userId of the White player. Null if not yet seated.</summary>
    string? WhitePlayerId,
    string? WhitePlayerName,
    string? BlackPlayerId,
    string? BlackPlayerName);

// ============================================================
//  JOIN REQUEST FLOW — private rooms gate joiners through the
//  host's approval list. Public rooms skip this entirely.
//
//  Lifecycle:
//   1. User clicks Join on a private room
//   2. Server enqueues JoinRequest, broadcasts JoinRequested
//   3. Host sees a "Requests" panel, taps Approve / Decline
//   4. Server updates state, broadcasts JoinRequestResolved
//   5. On Approve: requesting user is moved into participants
//                  as a Player (or Spectator if seats full)
//
//  Scope: per-room, per-session. Resolved requests aren't kept;
//  declined users can re-request after a cooldown (60s) to
//  avoid spam.
// ============================================================

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JoinRequestStatus
{
    Pending,
    Approved,
    Declined,
}

public record JoinRequest(
    string Id,
    string UserId,
    string Username,
    DateTime RequestedAtUtc,
    JoinRequestStatus Status);

public record JoinRequestResolved(
    string RequestId,
    string UserId,
    JoinRequestStatus Status);

// ─── Host invites (any game type) ──────────────────────────────

/// <summary>
/// Pushed to a SPECIFIC target user when a host invites them.
/// Frontend renders a toast/modal with "Accept" → navigates to
/// /play/{slug} and the JoinAsync flow admits them as Player
/// (the invite token short-circuits the private-room request gate).
/// </summary>
public record GameRoomInvite(
    string InviteId,
    string Slug,
    string RoomName,
    GameType Type,
    string FromUsername,
    DateTime SentAtUtc);

