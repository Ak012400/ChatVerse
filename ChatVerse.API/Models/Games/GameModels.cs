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
    int SecondsPerQuestion);

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
    DateTime CreatedAtUtc);

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
    double AverageResponseMs);

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
    IReadOnlyList<GameChatMessage> RecentChat);

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
