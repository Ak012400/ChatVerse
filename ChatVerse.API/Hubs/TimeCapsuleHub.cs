using ChatVerse.API.Extensions;
using ChatVerse.API.Services;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Hubs;

// ============================================================
//  TimeCapsuleHub — author + recipient interactions.
//
//  Methods:
//   • WriteCapsule(content, type, mediaUrl, deliveryDays, revealAuthor)
//        Author submits a fresh capsule. ScheduledFor is computed
//        server-side as now + deliveryDays + random jitter (0-23 hours)
//        so we don't get a thundering-herd at noon delivery.
//
//   • ReplyToCapsule(capsuleId, replyContent)
//        Recipient sends their single-shot reply. The reply is queued
//        for delivery to the author 3 days later — patience-rewarding
//        UX, also gives moderation time to scan.
//
//   • GetMyInbox()  — capsules delivered TO me
//   • GetMySent()   — capsules I wrote (whether delivered or not)
//
//  Real-time push (events the server fires):
//   • "TimeCapsuleDelivered" — when the delivery service hands a
//        capsule to this user, fanned out via Clients.User(...).
//   • "TimeCapsuleReplyDelivered" — when a reply arrives back at the
//        original author.
//
//  Why a Hub vs REST controller?
//    Inbox needs to be REAL-TIME — when a capsule lands, the user
//    sees it appear without refresh. The delivery service holds an
//    IHubContext<TimeCapsuleHub> and pushes directly to the user
//    group on delivery.
// ============================================================

[Authorize]
public class TimeCapsuleHub : Hub
{
    private readonly MongoService _mongo;
    private readonly ILogger<TimeCapsuleHub> _logger;

    // Bounds — defensive, NOT business policy. Real validation belongs
    // upstream (UI) too, but a server-side cap stops abuse.
    private const int MaxContentChars = 2000;
    private const int MaxReplyChars = 1000;
    private static readonly int[] AllowedDeliveryDays = new[] { 7, 14, 30 };

    public TimeCapsuleHub(MongoService mongo, ILogger<TimeCapsuleHub> logger)
    {
        _mongo = mongo;
        _logger = logger;
    }

    public async Task<string> WriteCapsule(
        string content,
        string type,
        string? mediaUrl,
        int deliveryDays,
        bool revealAuthor)
    {
        var authorId = JwtService.GetUserId(Context.User!).ToString();
        var authorName = JwtService.GetUsername(Context.User!);

        if (string.IsNullOrWhiteSpace(content))
            throw new HubException("Capsule content cannot be empty.");
        if (content.Length > MaxContentChars)
            throw new HubException($"Capsule too long ({MaxContentChars} chars max).");
        if (!AllowedDeliveryDays.Contains(deliveryDays))
            throw new HubException("Delivery window must be 7, 14 or 30 days.");
        if (type != "text" && type != "image" && type != "audio")
            throw new HubException("Unknown capsule type.");

        // Jitter so 100 noon-writes don't all fire at the same delivery second.
        var jitterHours = Random.Shared.Next(0, 24);
        var scheduledFor = DateTime.UtcNow
            .AddDays(deliveryDays)
            .AddHours(jitterHours);

        var capsule = new TimeCapsule
        {
            AuthorUserId        = authorId,
            AuthorUsername      = authorName,
            AuthorRevealed      = revealAuthor,
            Content             = content.Trim(),
            Type                = type,
            MediaUrl            = mediaUrl,
            DeliveryWindowDays  = deliveryDays,
            ScheduledFor        = scheduledFor,
            CreatedAt           = DateTime.UtcNow,
        };

        await _mongo.InsertTimeCapsuleAsync(capsule);
        _logger.LogInformation(
            "TimeCapsule written by {Author} for delivery in {Days}d (capsule={Id})",
            authorName, deliveryDays, capsule.Id);

        return capsule.Id!;
    }

    public async Task<bool> ReplyToCapsule(string capsuleId, string replyContent)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        if (string.IsNullOrWhiteSpace(replyContent))
            throw new HubException("Reply cannot be empty.");
        if (replyContent.Length > MaxReplyChars)
            throw new HubException($"Reply too long ({MaxReplyChars} chars max).");

        var ok = await _mongo.SetCapsuleReplyAsync(capsuleId, meId, replyContent.Trim());
        if (!ok)
        {
            // Either the capsule wasn't delivered to me, or I already replied.
            // Don't reveal which, to keep the surface tight.
            throw new HubException("Cannot reply to this capsule.");
        }
        return true;
    }

    public async Task<object> GetMyInbox()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var items = await _mongo.GetCapsulesForRecipientAsync(meId);
        return new
        {
            count = items.Count,
            capsules = items.Select(ToInboxDto).ToList(),
        };
    }

    public async Task<object> GetMySent()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var items = await _mongo.GetCapsulesByAuthorAsync(meId);
        return new
        {
            count = items.Count,
            capsules = items.Select(ToSentDto).ToList(),
        };
    }

    // ─── DTO shaping. We DON'T return AuthorUserId on inbox unless
    //     the author opted to reveal. Same for sender side — they
    //     never see who their capsule was delivered to (privacy
    //     guarantee, not just convention).

    private static object ToInboxDto(TimeCapsule c) => new
    {
        id              = c.Id,
        content         = c.Content,
        type            = c.Type,
        mediaUrl        = c.MediaUrl,
        deliveryDays    = c.DeliveryWindowDays,
        authorName      = c.AuthorRevealed ? c.AuthorUsername : null,
        deliveredAt     = c.DeliveredAt,
        canReply        = c.RepliedAt == null,
        repliedAt       = c.RepliedAt,
    };

    private static object ToSentDto(TimeCapsule c) => new
    {
        id              = c.Id,
        content         = c.Content,
        type            = c.Type,
        deliveryDays    = c.DeliveryWindowDays,
        scheduledFor    = c.ScheduledFor,
        deliveredAt     = c.DeliveredAt,
        gotReply        = c.RepliedAt != null,
        replyContent    = c.ReplyDeliveredAt != null ? c.ReplyContent : null,
        replyDeliveredAt= c.ReplyDeliveredAt,
    };
}
