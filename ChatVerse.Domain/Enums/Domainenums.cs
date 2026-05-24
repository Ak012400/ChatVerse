namespace ChatVerse.Domain.Enums;

// ── user_auth schema ─────────────────────────────────────────
public enum UserStatus
{
    Active,
    Suspended,
    Banned,
    Deactivated
}

public enum OtpPurpose
{
    EmailVerification,
    PasswordReset,
    AgeDeclaration
}

// ── iam schema ───────────────────────────────────────────────
public enum AgeVerifyMethod
{
    Tenure,
    Document
}

public enum DocVerifyStatus
{
    Pending,
    Approved,
    Rejected
}

// ── trust schema ─────────────────────────────────────────────
public enum TrustEventType
{
    MsgFlagged,       // -5
    MsgBlocked,       // -15
    VideoNsfw,        // -25
    ReportedValid,    // -20
    ReportedInvalid,  // +2
    OtpVerified,      // +10
    SubscriptionPaid, // +15
    SessionCompleted, // +1
    ManualBan,        // -100
    ManualBoost       // admin custom
}

public enum TrustBand
{
    New,         // 0-20
    Restricted,  // 21-40
    Normal,      // 41-70
    Trusted,     // 71-90
    Elite        // 91-100
}

public enum ReportStatus
{
    Pending,
    ReviewedValid,
    ReviewedInvalid,
    Dismissed
}

// ── chat schema ──────────────────────────────────────────────
public enum BanType
{
    Temporary,
    Permanent
}

// ── billing schema ───────────────────────────────────────────
public enum PlanType
{
    Free,
    Basic,
    Pro,
    Elite
}

public enum SubscriptionStatus
{
    Active,
    Expired,
    Cancelled,
    Pending
}

public enum PaymentStatus
{
    Created,
    Paid,
    Failed,
    Refunded
}

// ── MongoDB ──────────────────────────────────────────────────
public enum MessageType
{
    Text,
    Image,
    System,
    Gif
}

public enum ModerationStatus
{
    Clean,
    Flagged,
    Blocked,
    Pending
}

public enum VideoSessionOutcome
{
    Clean,
    Warned,
    Banned,
    UnderReview
}