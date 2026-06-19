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

    // ── oEmbed-enriched metadata ─────────────────────────────────
    //  Populated by SpotifyOEmbedService during ChatHub.SendMessage.
    //  Null when the enricher couldn't reach Spotify or the URL is
    //  malformed — frontend falls back to the generic "Spotify {kind}"
    //  label in that case.
    public string? Title { get; set; }
    public string? ThumbnailUrl { get; set; }
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
/// <summary>
/// Directed block — blocker chose to silence blocked. Standard
/// Instagram-style semantics: the blocked party gets no explicit
/// notification, calls/DMs from them silently fail to reach the
/// blocker. Stored in Mongo because the relationship is many-to-many
/// and the read pattern is "is X blocked by Y?" which is a trivial
/// indexed lookup.
/// </summary>
public class UserBlock
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>User who initiated the block.</summary>
    public string BlockerId { get; set; } = default!;

    /// <summary>User being silenced.</summary>
    public string BlockedId { get; set; } = default!;

    /// <summary>Optional reason — surfaced only to the blocker.</summary>
    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; }
}

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

// ============================================================
//  TimeCapsule — Phase 2 sticky feature
//
//  Author writes a message TODAY → system delivers it to a RANDOM
//  anonymous recipient 7/14/30 days later. Recipient can reply ONCE
//  (delivered back to author 3 days later).
//
//  Why this design:
//    • Anonymous-by-default — AuthorUserId stored but never revealed
//      unless author explicitly opted to sign their capsule.
//    • Recipient chosen AT DELIVERY TIME, not write time, so the
//      pool of active users is freshest.
//    • Single reply max — keeps it a "moment", not a thread.
//    • TTL via the existing maintenance webjob (90 days post-delivery).
// ============================================================

// ============================================================
//  Persona Roulette — Phase 2 signature daily feature
//
//  At 00:00 UTC each day the PersonaResetService generates a new
//  Persona row per active user. The Persona is what other users
//  see when DMing them through the persona-roulette surface. The
//  real ↔ persona mapping NEVER leaves the server; client only
//  ever sees the persona's display fields.
//
//  Streak tracking lives in PersonaStreak, keyed by the (RealUserA,
//  RealUserB) tuple sorted canonically so we don't double-insert
//  when A messages B vs B messages A.
//
//  Why split into 3 collections vs one fat doc:
//    • personas — daily-disposable, indexed by (UserId, Date)
//    • persona_conversations — per-day rollup of persona ↔ persona DM
//      activity, used by the reset service to decide whether the day
//      counts toward a streak
//    • persona_streaks — long-lived per-real-user-pair tracker
// ============================================================

public class Persona
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>Real user this persona belongs to. Server-only —
    /// never serialised to other clients.</summary>
    public string UserId { get; set; } = default!;

    /// <summary>YYYY-MM-DD (UTC) of the day this persona is valid for.
    /// Indexed alongside UserId so "get today's persona for user X"
    /// is one hit.</summary>
    public string Date { get; set; } = default!;

    /// <summary>What other users see — e.g. "Velvet Comet". Generated
    /// from an adjective + noun word list, with a small numeric tail
    /// added if a collision is detected within the same day.</summary>
    public string DisplayName { get; set; } = default!;

    /// <summary>Seed for dicebear-style avatar generation. Client
    /// resolves seed → image URL. Stable for the day.</summary>
    public string AvatarSeed { get; set; } = default!;

    /// <summary>One-line bio shown on the persona card. Picked from
    /// a curated pool of poetic / cryptic / playful one-liners.</summary>
    public string Bio { get; set; } = default!;

    /// <summary>Short label like "playful" / "wistful" / "curious"
    /// — drives UI accent colour on the persona card.</summary>
    public string Mood { get; set; } = default!;

    /// <summary>UTC midnight of the NEXT day. PersonaResetService
    /// uses this for batch-archival when generating the next day's
    /// personas.</summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class PersonaConversation
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>YYYY-MM-DD of the day these personas interacted. The
    /// (PersonaIdA, PersonaIdB, Date) tuple is unique per day.</summary>
    public string Date { get; set; } = default!;

    public string PersonaIdA { get; set; } = default!;
    public string PersonaIdB { get; set; } = default!;

    /// <summary>Underlying real user IDs, sorted ascending. This is
    /// the canonical key the streak tracker reads — it doesn't care
    /// who messaged first, only that the same real pair conversed.</summary>
    public string RealUserA { get; set; } = default!;
    public string RealUserB { get; set; } = default!;

    public int MessagesCount { get; set; }
    public DateTime LastInteraction { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class PersonaStreak
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>Real user IDs sorted ascending so the pair has ONE
    /// canonical key regardless of who initiated. Indexed unique.</summary>
    public string RealUserA { get; set; } = default!;
    public string RealUserB { get; set; } = default!;

    /// <summary>Consecutive UTC days these two have conversed (at
    /// least one PersonaConversation row per day, regardless of
    /// their changing personas).</summary>
    public int ConsecutiveDays { get; set; }

    /// <summary>YYYY-MM-DD of the most recent day that counted toward
    /// the streak. If today &gt; LastDay + 1, the streak resets.</summary>
    public string LastDay { get; set; } = default!;

    /// <summary>Real user IDs that have tapped "Request unmask" on
    /// their side. The streak only flips to UnmaskedAt when BOTH
    /// RealUserA and RealUserB appear in this list — single-tap
    /// accidents can't reveal identities.</summary>
    public List<string> UnmaskRequestedBy { get; set; } = new();

    /// <summary>Set when BOTH sides accepted the Mutual Unmask offer
    /// (only available at ConsecutiveDays >= 7). Once set, the two
    /// real usernames become visible to each other inside the
    /// Persona Roulette surface.</summary>
    public DateTime? UnmaskedAt { get; set; }

    /// <summary>True when ConsecutiveDays first crossed 30 — flips
    /// the conversation into the Memory Vault archive.</summary>
    public DateTime? VaultedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class TimeCapsule
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>Author's user id. NULL if posted anonymously
    /// (author opted to hide identity even from server logs).</summary>
    public string? AuthorUserId { get; set; }

    /// <summary>Author display name at time of write. Frozen here so
    /// rename later doesn't retroactively change old capsules.</summary>
    public string? AuthorUsername { get; set; }

    /// <summary>True if author wants their name shown on delivery.
    /// False means recipient sees "Anonymous voyager".</summary>
    public bool AuthorRevealed { get; set; } = false;

    public string Content { get; set; } = default!;
    public string Type { get; set; } = "text";  // text / image / audio
    public string? MediaUrl { get; set; }

    /// <summary>Days the author chose: 7 / 14 / 30.</summary>
    public int DeliveryWindowDays { get; set; }

    /// <summary>The actual delivery target time. ScheduledFor + small jitter
    /// so 100 capsules written at noon don't all fire at the same second.</summary>
    public DateTime ScheduledFor { get; set; }

    public DateTime? DeliveredAt { get; set; }
    public string? RecipientUserId { get; set; }
    public string? RecipientUsername { get; set; }

    /// <summary>Single-shot reply from recipient. Null until they choose
    /// to reply. Replies have their own scheduledReplyDeliveryAt so
    /// they arrive 3 days later, not instantly.</summary>
    public string? ReplyContent { get; set; }
    public DateTime? RepliedAt { get; set; }
    public DateTime? ScheduledReplyDeliveryAt { get; set; }
    public DateTime? ReplyDeliveredAt { get; set; }

    public DateTime CreatedAt { get; set; }
}

// ============================================================
//  PersonaMessage — a single line of text between two personas.
//
//  Stored against the REAL user pair (sorted canonically) so the
//  thread survives daily persona rotation: A and B keep chatting
//  even though both their visible names change at 00:00 UTC.
//
//  Privacy:
//    • Client never sees raw real user IDs — server resolves
//      "who is the other party today?" via PersonaStreak +
//      today's Persona.
//    • Sender's persona display fields are SNAPSHOTTED into the
//      message at send time. That way the recipient sees who
//      sent it AT THE TIME, even if the sender's persona has
//      since rolled over.
// ============================================================

// ============================================================
//  Story Chain — Phase 1 sticky creative-engagement loop.
//
//  Daily 3pm IST: StoryChainService spawns a new chain with a
//  prompt sentence (curated bank). Users join a turn queue; the
//  active turn-holder has 10 min to submit ONE sentence. After
//  50 unique contributions the chain locks and is published to
//  the public Stories archive. At midnight IST any still-active
//  chain locks too — partial stories ship.
//
//  One sentence per user per chain (enforced via the canonical
//  ContributorUserIds set). The same user can contribute again
//  to tomorrow's chain.
// ============================================================

public class StoryChain
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>The IST date this chain belongs to (YYYY-MM-DD).
    /// One chain per day — unique-indexed.</summary>
    public string PromptDate { get; set; } = default!;

    /// <summary>Opening line of the story — drawn from the curated
    /// prompt bank at chain creation. Frozen for the chain's life.</summary>
    public string Prompt { get; set; } = default!;

    /// <summary>Ordered list of contributions. Position 0 is the
    /// first contributor's sentence; new ones append.</summary>
    public List<StoryContribution> Sentences { get; set; } = new();

    /// <summary>Canonical set of user IDs who've already contributed
    /// — used for the "one sentence per user" check.</summary>
    public List<string> ContributorUserIds { get; set; } = new();

    /// <summary>"active" | "locked" | "published". Locked happens
    /// before published — locking freezes contributions, publishing
    /// is what surfaces it on the archive feed.</summary>
    public string Status { get; set; } = "active";

    public DateTime? LockedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class StoryContribution
{
    public string SentenceText { get; set; } = default!;
    public string AuthorUserId { get; set; } = default!;
    public string AuthorUsername { get; set; } = default!;
    public DateTime AddedAt { get; set; }
}

public class PersonaMessage
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>Sorted real-user pair, lower id first. Same key the
    /// PersonaStreak uses. Indexed for thread queries.</summary>
    public string RealUserA { get; set; } = default!;
    public string RealUserB { get; set; } = default!;

    /// <summary>Which side sent this message — must be one of
    /// (RealUserA, RealUserB).</summary>
    public string SenderRealUserId { get; set; } = default!;

    /// <summary>Snapshot of sender's persona AT SEND TIME. Frozen so
    /// the message bubble displays the right name even after the
    /// persona expires.</summary>
    public string SenderPersonaId { get; set; } = default!;
    public string SenderDisplayName { get; set; } = default!;
    public string SenderAvatarSeed { get; set; } = default!;

    public string Content { get; set; } = default!;

    public DateTime CreatedAt { get; set; }
}