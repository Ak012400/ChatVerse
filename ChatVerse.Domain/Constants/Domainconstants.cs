namespace ChatVerse.Domain.Constants;

public static class TrustDeltas
{
    public const int MsgFlagged = -5;
    public const int MsgBlocked = -15;
    public const int VideoNsfw = -25;
    public const int ReportedValid = -20;
    public const int ReportedInvalid = +2;
    public const int OtpVerified = +10;
    public const int SubscriptionPaid = +15;
    public const int SessionCompleted = +1;
    public const int ManualBan = -100;
}

public static class TrustBands
{
    public const int NewMax = 20;
    public const int RestrictedMax = 40;
    public const int NormalMax = 70;
    public const int TrustedMax = 90;
    public const int EliteMax = 100;
}

public static class RedisKeys
{
    // {userId}
    public static string Session(string userId) => $"session:{userId}";
    public static string UserOnline(string userId) => $"user:online:{userId}";

    // {roomSlug}
    public static string RoomActiveCount(string slug) => $"room:active:{slug}";

    // {email}
    public static string OtpRateLimit(string email) => $"otp:ratelimit:{email}";

    // ── Gaming Hall ────────────────────────────────────────────────
    // A "game room" is a thin wrapper around an IGameSession. Keys are
    // designed so the GameHub never has to scan: every read is a single
    // O(1) GET. State lives in Redis (not memory) so a Render restart
    // mid-quiz doesn't wipe the round.
    // {slug}
    public static string GameRoomMeta(string slug) => $"game:room:{slug}";
    public static string GameRoomState(string slug) => $"game:state:{slug}";
    public static string GameRoomChat(string slug) => $"game:chat:{slug}";
    // Sets — players and spectators tracked separately so we can cap
    // players (8 max) without throttling spectator joins.
    public static string GameRoomPlayers(string slug) => $"game:players:{slug}";
    public static string GameRoomSpectators(string slug) => $"game:spectators:{slug}";
    // Index of every active game room so GamingHallPage can list them
    // without scanning keyspace.
    public const string GameRoomIndex = "game:rooms:index";
    // OpenTriviaDB question cache. Key includes (category|difficulty)
    // so different settings don't poison each other.
    public static string QuizQuestionCache(string categorySlug, string difficultySlug)
        => $"quiz:cache:{categorySlug}:{difficultySlug}";

    // icanhazdadjoke cache. Single global pool — no per-difficulty
    // segmentation since dad jokes don't have difficulty levels.
    public const string JokesCache = "jokes:cache:global";

    // Random-room pointer. One pointer per (chatSlug, gameType) tuple
    // → slug of the currently-active random room. Set with NX so a
    // race between two simultaneous "Join Random" requests still
    // results in exactly one room being created.
    public static string GameRandomPointer(string chatSlug, string gameType)
        => $"game:random:{chatSlug}:{gameType.ToLowerInvariant()}";

    // Tech Talk news feed cache. Single global key — same feed for
    // every client of the Tech Talk room.
    public const string TechNewsCache = "tech-news:cache:global";

    // Rolling quiz — always-on quiz in #general. Sessions roll
    // daily by UTC date (yyyyMMdd) so leaderboards reset cleanly.
    /// <summary>Current live question (full state including correct answer).</summary>
    public const string RollingQuizCurrent = "rolling-quiz:current";
    /// <summary>Per-question submissions (hash: userId -> "choiceIndex|atTicks|isCorrect").</summary>
    public static string RollingQuizSubmissions(string questionId)
        => $"rolling-quiz:submissions:{questionId}";
    /// <summary>Ordered list of userIds who answered correctly (for rank-based scoring).</summary>
    public static string RollingQuizCorrectOrder(string questionId)
        => $"rolling-quiz:correct:{questionId}";
    /// <summary>Sorted set: userId → score for the day-bucket session.</summary>
    public static string RollingQuizLeaderboard(string sessionId)
        => $"rolling-quiz:leaderboard:{sessionId}";
    /// <summary>Hash: userId → "username|correct|attempts" for richer leader rows.</summary>
    public static string RollingQuizStats(string sessionId)
        => $"rolling-quiz:stats:{sessionId}";
}

public static class RedisTTL
{
    public static readonly TimeSpan Session = TimeSpan.FromHours(24);
    public static readonly TimeSpan UserOnline = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RoomCount = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan OtpRateLimit = TimeSpan.FromMinutes(15);

    // Game state TTL: long enough to outlive any reasonable session,
    // short enough that abandoned rooms get cleaned up automatically.
    // A quiz round of 10×15s = ~3min, plus lobby + reveal = ~10min real
    // user time, so 1hr is comfortably above that.
    public static readonly TimeSpan GameRoom = TimeSpan.FromHours(1);
    // Question cache — refresh every hour. OpenTriviaDB has 4000+ Q's,
    // so even with caching we get plenty of variety.
    public static readonly TimeSpan QuizCache = TimeSpan.FromHours(1);

    // Tech news cache — 10 min is the sweet spot between "fresh enough
    // to feel live" and "doesn't hammer upstream APIs for the same data".
    public static readonly TimeSpan TechNews = TimeSpan.FromMinutes(10);
}

public static class JwtClaims
{
    public const string UserId = "uid";
    public const string Username = "username";
    public const string IsGuest = "is_guest";
    public const string TrustScore = "trust_score";
    public const string IsEmailVerified = "email_verified";
    public const string AgeVerified = "age_verified";
}

public static class MongoCollections
{
    public const string Messages = "messages";
    public const string Rooms = "rooms";
    public const string ModerationLogs = "moderation_logs";
    public const string VideoSessions = "video_sessions";
    public const string DmMessages = "dm_messages";
}

public static class AgeVerification
{
    public const int RequiredActiveDays = 7;
    public const int AiPassScore = 70;
    public const int MaxAiAttempts = 3;
    public const int MinAgeAllowed = 13;
    public const int AdultAge = 18;
}

public static class Otp
{
    public const int ExpiryMinutes = 10;
    public const int MaxRequestsPerWindow = 3;   // per 15 min window
    public const int CodeLength = 6;
}

public static class Pagination
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;
}