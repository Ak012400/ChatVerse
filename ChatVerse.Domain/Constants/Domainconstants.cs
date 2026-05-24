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
}

public static class RedisTTL
{
    public static readonly TimeSpan Session = TimeSpan.FromHours(24);
    public static readonly TimeSpan UserOnline = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RoomCount = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan OtpRateLimit = TimeSpan.FromMinutes(15);
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