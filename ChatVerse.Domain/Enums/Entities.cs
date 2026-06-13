using ChatVerse.Domain.Enums;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ChatVerse.Domain.Entities;

// ============================================================
//  PostgreSQL entities — user_auth schema
// ============================================================

public class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = default!;
    public string? Email { get; set; }
    public string? PasswordHash { get; set; }
    public bool IsGuest { get; set; } = true;
    public bool IsEmailVerified { get; set; } = false;
    public string? AvatarUrl { get; set; }
    public UserStatus Status { get; set; } = UserStatus.Active;

    // Trust
    public short TrustScore { get; set; } = 50;

    // Age verification flags
    public bool AgeSelfDeclared { get; set; } = false;
    public DateTime? AgeDeclaredAt { get; set; }
    public bool AiMaturityPassed { get; set; } = false;
    public short? AiMaturityScore { get; set; }
    public bool AgeVerified { get; set; } = false;
    public DateTime? AgeVerifiedAt { get; set; }
    public AgeVerifyMethod? AgeVerifyMethod { get; set; }

    // Tenure tracking
    public short ActiveDaysCount { get; set; } = 0;
    public DateOnly? LastActiveDate { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class OtpCode
{
    public Guid Id { get; set; }
    public string Email { get; set; } = default!;
    public string Code { get; set; } = default!;
    public OtpPurpose Purpose { get; set; }
    public bool Used { get; set; } = false;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ── iam schema ───────────────────────────────────────────────

public class AgeDeclaration
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateOnly Dob { get; set; }
    public bool Declared18Plus { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AiMaturitySession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? SessionRef { get; set; }
    public string QuestionsAsked { get; set; } = "[]"; // JSON
    public short FinalScore { get; set; }
    public bool Passed { get; set; }
    public short AttemptNumber { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class DocumentVerification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string DocType { get; set; } = default!;
    public string CloudinaryPublicId { get; set; } = default!;
    public string CloudinaryUrl { get; set; } = default!;
    public DocVerifyStatus Status { get; set; } = DocVerifyStatus.Pending;
    public Guid? ReviewedBy { get; set; }
    public string? RejectReason { get; set; }
    public DateTime SubmittedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
}

public class TenureCheck
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateTime ClearedAt { get; set; }
    public short ActiveDays { get; set; }
}

// ── trust schema ─────────────────────────────────────────────

public class TrustEvent
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public TrustEventType EventType { get; set; }
    public short Delta { get; set; }
    public string? Reason { get; set; }
    public Guid? RefId { get; set; }
    public string? RefSource { get; set; }
    public Guid? AppliedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UserReport
{
    public Guid Id { get; set; }
    public Guid ReporterId { get; set; }
    public Guid ReportedId { get; set; }
    public string Reason { get; set; } = default!;
    public string? Description { get; set; }
    public ReportStatus Status { get; set; } = ReportStatus.Pending;
    public Guid? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
}

// ── chat schema ──────────────────────────────────────────────

public class RoomBan
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string RoomSlug { get; set; } = default!;
    public BanType BanType { get; set; }
    public string? Reason { get; set; }
    public Guid? BannedBy { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ── billing schema ───────────────────────────────────────────

public class Subscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public PlanType PlanType { get; set; }
    public SubscriptionStatus Status { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Payment
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid SubscriptionId { get; set; }
    public string? RazorpayOrderId { get; set; }
    public string? RazorpayPaymentId { get; set; }
    public int AmountPaise { get; set; }
    public string Currency { get; set; } = "INR";
    public PaymentStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ============================================================
//  MongoDB entities
// ============================================================

public class Message
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string RoomId { get; set; } = default!;
    public string SenderId { get; set; } = default!;
    public string SenderName { get; set; } = default!;
    public int SenderTrustScore { get; set; }
    public string? SenderAvatarUrl { get; set; }
    public string Content { get; set; } = default!;
    public string Type { get; set; } = "text";
    public string? MediaUrl { get; set; }
    public MessageModeration Moderation { get; set; } = new();
    public Dictionary<string, List<string>> Reactions { get; set; } = new();
    public string? ReplyTo { get; set; }
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }
    public DateTime? EditedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    // Rich link preview — populated server-side when the message body
    // contains a Spotify track/album/playlist/episode URL. Null when no
    // recognisable link is present. Lets the frontend render an inline
    // Spotify iframe without doing its own URL parsing.
    public SpotifyEmbed? Spotify { get; set; }
}

public class MessageModeration
{
    public string Status { get; set; } = "pending";
    public string? CheckedBy { get; set; }
    public string? FlagReason { get; set; }
    public double? Confidence { get; set; }
}

public class SpotifyEmbed
{
    /// <summary>track | album | playlist | episode | show | artist</summary>
    public string Kind { get; set; } = default!;
    public string SpotifyId { get; set; } = default!;
    /// <summary>Ready-to-iframe URL — https://open.spotify.com/embed/{kind}/{id}</summary>
    public string EmbedUrl { get; set; } = default!;
    /// <summary>Canonical web URL — original Spotify link the user pasted.</summary>
    public string WebUrl { get; set; } = default!;
}

public class Room
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string Slug { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string? Description { get; set; }
    public string Category { get; set; } = "public";
    public string? IconEmoji { get; set; }
    public List<string> Rules { get; set; } = new();
    public string? CreatedBy { get; set; }
    public RoomStats Stats { get; set; } = new();
    public bool IsActive { get; set; } = true;
    /// <summary>
    /// Hidden from the public room list. Visible only to users who
    /// joined explicitly via an invite token. User-created rooms are
    /// private by default.
    /// </summary>
    public bool IsPrivate { get; set; } = false;
    /// <summary>
    /// Shareable token for invite-based joining. NULL for seeded public
    /// rooms (they don't need one). Stored as a non-guessable GUID.
    /// </summary>
    public string? InviteToken { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class RoomStats
{
    public int TotalMessages { get; set; } = 0;
    public int ActiveNow { get; set; } = 0;
}

public class ModerationLog
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonRepresentation(BsonType.ObjectId)]
    public string MessageId { get; set; } = default!;
    public string RoomId { get; set; } = default!;
    public string SenderId { get; set; } = default!;
    public string OriginalContent { get; set; } = default!;
    public string Action { get; set; } = default!;
    public string Source { get; set; } = default!;
    public object? OpenaiResponse { get; set; }
    public int? TrustDelta { get; set; }
    public string? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
}

public class VideoSession
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string SessionId { get; set; } = default!;
    public List<Participant> Participants { get; set; } = new();
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int? DurationSeconds { get; set; }
    public string? EndReason { get; set; }
    public List<NsfwFlag> NsfwFlags { get; set; } = new();
    public string? ReportedBy { get; set; }
    public string Outcome { get; set; } = "clean";
}

public class Participant
{
    public string UserId { get; set; } = default!;
    public string Username { get; set; } = default!;
    public int TrustScore { get; set; }
    public bool AgeVerified { get; set; }
    public string? PeerId { get; set; }
}

public class NsfwFlag
{
    public DateTime DetectedAt { get; set; }
    public string UserId { get; set; } = default!;
    public string Label { get; set; } = default!;
    public double Confidence { get; set; }
}

// ============================================================
//  DM (direct message) entity. Lives in dm_messages collection.
//  ConversationId is deterministic — see DmService.ConvIdFor.
// ============================================================
public class DmMessage
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>Stable id derived from sorted (sender, recipient) pair.</summary>
    public string ConversationId { get; set; } = default!;
    public string SenderId { get; set; } = default!;
    public string SenderName { get; set; } = default!;
    public string RecipientId { get; set; } = default!;
    public string Content { get; set; } = default!;
    public string Type { get; set; } = "text";
    public string? MediaUrl { get; set; }
    public bool IsRead { get; set; } = false;
    public bool IsDeleted { get; set; } = false;
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
    // Same Spotify enrichment as room messages.
    public SpotifyEmbed? Spotify { get; set; }
}