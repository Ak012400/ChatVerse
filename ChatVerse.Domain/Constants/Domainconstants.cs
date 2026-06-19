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

    // Game-room invitation token. Set with TTL = 10 min so links
    // shared but not used expire automatically.
    // {inviteId} → "fromUserId:targetUserId:slug"
    public static string GameInvite(string inviteId) => $"game:invite:{inviteId}";

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

    // ── User-state cache (trust score + age verified) ─────────────
    //  Hubs/controllers read through these so a banned user's gate
    //  flips within seconds instead of waiting for JWT expiry.
    public static string UserTrust(string userId) => $"user:trust:{userId}";
    public static string UserAgeVerified(string userId) => $"user:ageverified:{userId}";

    // ── ChatHub random match queue ────────────────────────────────
    //  Distributed replacement for the legacy static ConcurrentQueue.
    //  Separate key from the video matchmaker so the two flows don't
    //  pull each other's users.
    public const string ChatMatchQueue = "chat:match:queue";

    // ── NSFW report aggregation per (session, violator) ──────────
    //  SET of distinct reporter IDs with a short TTL — used to apply
    //  trust penalties only when ≥2 independent users report the same
    //  violator (or one reporter with very high model confidence).
    public static string NsfwReports(string sessionId, string violatorUserId)
        => $"nsfw:reports:{sessionId}:{violatorUserId}";
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

    // Trust/age cache TTL — short so bans + verifications propagate fast
    // (the price is one extra Postgres round-trip every 30s per active user).
    public static readonly TimeSpan UserState = TimeSpan.FromSeconds(30);

    // NSFW report aggregation window
    public static readonly TimeSpan NsfwReportWindow = TimeSpan.FromSeconds(60);
}

public static class NsfwModeration
{
    // Number of distinct reporters required before trust penalty is applied
    // (a single very-high-confidence report still triggers the penalty).
    public const int ReportThreshold = 2;
    public const double HighConfidenceAutoTrip = 0.95;
}

public static class EphemeralImage
{
    // Minimum trust score required to send vanish-mode images.
    // Keeps brand-new / penalised accounts out of the path entirely.
    public const int MinTrustScore = 41;   // Normal band or above
    public const int MaxBase64Bytes = 4 * 1024 * 1024;
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
    /// <summary>Directed user-block relationships. (blocker, blocked) pair.</summary>
    public const string UserBlocks = "user_blocks";

    // ── Phase 2 (locked-roadmap features) ──────────────────────
    /// <summary>Time Capsule — delayed-delivery messages to random recipients.</summary>
    public const string TimeCapsules = "time_capsules";

    // ── Persona Roulette ───────────────────────────────────────
    /// <summary>Per-user daily-disposable identity.</summary>
    public const string Personas = "personas";
    /// <summary>Per-day rollup of which two personas DM'd, with the
    /// underlying real users so the streak tracker can canonicalise.</summary>
    public const string PersonaConversations = "persona_conversations";
    /// <summary>Long-lived consecutive-day tracker between two real
    /// users (regardless of their changing personas).</summary>
    public const string PersonaStreaks = "persona_streaks";
    /// <summary>Persona-to-persona DM thread, keyed by sorted real
    /// user pair so it persists across daily rotations.</summary>
    public const string PersonaMessages = "persona_messages";

    // ── Story Chain ────────────────────────────────────────────
    /// <summary>Daily collaborative story — one chain per IST date.</summary>
    public const string StoryChains = "story_chains";
}

public static class Theater
{
    /// <summary>Max concurrent participants in a Theater (Watch Party) room.</summary>
    public const int MaxParticipants = 10;
    /// <summary>Empty room TTL in seconds — LiveKit closes after this idle window.</summary>
    public const int EmptyTimeoutSeconds = 300;
    /// <summary>Slug prefix so client-side routing can detect theater rooms.</summary>
    public const string SlugPrefix = "th-";
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