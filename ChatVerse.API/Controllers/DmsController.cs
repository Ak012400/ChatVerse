using ChatVerse.API.Extensions;
using ChatVerse.Domain.Constants;
using ChatVerse.Domain.Entities;
using ChatVerse.Domain.Enums;
using ChatVerse.Infrastructure.ExternalServices.OpenAI;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using ChatVerse.Infrastructure.Persistence.Redis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using ChatVerse.API.Hubs;

namespace ChatVerse.API.Controllers;

/// <summary>
/// Direct messages (1-to-1 text).
///
/// Persistence lives in MongoDB collection <c>dm_messages</c>. Realtime
/// delivery uses ChatHub — see <see cref="ChatHub.SendDm"/>. This REST
/// surface is the catch-up path for cold loads + paging.
///
/// Guest accounts cannot DM (registered-only feature).
/// </summary>
[ApiController]
[Route("api/dms")]
[Authorize]
public class DmsController : ControllerBase
{
    private readonly MongoService _mongo;
    private readonly PostgresProcService _postgres;
    private readonly RedisService _redis;
    private readonly ModerationOrchestrator _moderation;
    private readonly IHubContext<ChatHub> _hub;
    private readonly ILogger<DmsController> _logger;

    public DmsController(
        MongoService mongo,
        PostgresProcService postgres,
        RedisService redis,
        ModerationOrchestrator moderation,
        IHubContext<ChatHub> hub,
        ILogger<DmsController> logger)
    {
        _mongo = mongo;
        _postgres = postgres;
        _redis = redis;
        _moderation = moderation;
        _hub = hub;
        _logger = logger;
    }

    // ============================================================
    //  GET /api/dms
    //  All conversations involving the current user, newest first.
    // ============================================================
    [HttpGet]
    public async Task<IActionResult> GetConversations()
    {
        if (JwtService.GetIsGuest(User))
            return StatusCode(403, ApiResponse.Fail("DMs require a registered account."));

        var meId = JwtService.GetUserId(User).ToString();
        var summaries = await _mongo.GetUserDmConversationsAsync(meId);

        // Enrich with the other participant's username (best-effort).
        var enriched = new List<object>();
        foreach (var s in summaries)
        {
            string? otherName = null;
            if (Guid.TryParse(s.OtherUserId, out var otherGuid))
            {
                var record = await _postgres.GetUserAuthByIdAsync(otherGuid);
                otherName = record?.Username;
            }

            enriched.Add(new
            {
                conversationId   = s.ConversationId,
                otherUserId      = s.OtherUserId,
                otherUsername    = otherName ?? "Unknown",
                unreadCount      = s.UnreadCount,
                lastMessage      = s.LastMessageContent,
                lastMessageMine  = s.LastMessageSenderId == meId,
                lastMessageAt    = s.LastMessageAt.ToString("o"),
            });
        }

        return Ok(ApiResponse<object>.Ok(new
        {
            count = enriched.Count,
            conversations = enriched
        }));
    }

    // ============================================================
    //  GET /api/dms/{otherUserId}?skip=0&limit=50
    //  Paginated thread between the current user and otherUserId.
    //  Also marks all unread messages from the other side as read.
    // ============================================================
    [HttpGet("{otherUserId}")]
    public async Task<IActionResult> GetThread(
        string otherUserId,
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 50)
    {
        if (JwtService.GetIsGuest(User))
            return StatusCode(403, ApiResponse.Fail("DMs require a registered account."));

        var meId = JwtService.GetUserId(User).ToString();
        if (!Guid.TryParse(otherUserId, out _))
            return BadRequest(ApiResponse.Fail("Invalid user id"));

        var convId = MongoService.ConversationIdFor(meId, otherUserId);
        limit = Math.Clamp(limit, 1, 100);
        var thread = await _mongo.GetDmThreadAsync(convId, skip, limit);

        // Mark unread-to-me as read (background-friendly, but quick enough inline).
        var marked = await _mongo.MarkDmConversationReadAsync(convId, meId);
        if (marked > 0)
        {
            // Notify the other side so their UI can clear unread counters.
            await _hub.Clients.User(otherUserId).SendAsync("DmRead", new
            {
                conversationId = convId,
                readerId = meId,
            });
        }

        var result = thread.Select(m => new
        {
            id          = m.Id,
            conversationId = m.ConversationId,
            senderId    = m.SenderId,
            senderName  = m.SenderName,
            recipientId = m.RecipientId,
            content     = m.Content,
            type        = m.Type,
            mediaUrl    = m.MediaUrl,
            isRead      = m.IsRead,
            createdAt   = m.CreatedAt.ToString("o"),
        });

        return Ok(ApiResponse<object>.Ok(new
        {
            conversationId = convId,
            skip,
            limit,
            messages = result,
        }));
    }

    // ============================================================
    //  POST /api/dms/{otherUserId}
    //  REST fallback for sending a DM — ChatHub.SendDm is the primary
    //  realtime path. This is here for non-SignalR clients (e.g. an
    //  eventual mobile push reply).
    // ============================================================
    [HttpPost("{otherUserId}")]
    public async Task<IActionResult> SendDm(string otherUserId, [FromBody] SendDmRequest req)
    {
        if (JwtService.GetIsGuest(User))
            return StatusCode(403, ApiResponse.Fail("DMs require a registered account."));

        if (string.IsNullOrWhiteSpace(req.Content) || req.Content.Length > 2000)
            return BadRequest(ApiResponse.Fail("Message must be 1–2000 characters"));

        if (!Guid.TryParse(otherUserId, out _))
            return BadRequest(ApiResponse.Fail("Invalid user id"));

        var meId = JwtService.GetUserId(User).ToString();
        var meName = JwtService.GetUsername(User);
        if (meId == otherUserId)
            return BadRequest(ApiResponse.Fail("You can't DM yourself"));

        var convId = MongoService.ConversationIdFor(meId, otherUserId);
        var dm = new DmMessage
        {
            ConversationId = convId,
            SenderId       = meId,
            SenderName     = meName,
            RecipientId    = otherUserId,
            Content        = req.Content.Trim(),
            Type           = "text",
            CreatedAt      = DateTime.UtcNow,
        };

        var saved = await _mongo.InsertDmAsync(dm);

        // Broadcast realtime to both ends.
        var payload = new
        {
            id          = saved.Id,
            conversationId = saved.ConversationId,
            senderId    = saved.SenderId,
            senderName  = saved.SenderName,
            recipientId = saved.RecipientId,
            content     = saved.Content,
            type        = saved.Type,
            createdAt   = saved.CreatedAt.ToString("o"),
        };
        await _hub.Clients.Users(new[] { meId, otherUserId }).SendAsync("ReceiveDm", payload);

        // Best-effort moderation in the background — mirrors ChatHub.SendMessage.
        _ = Task.Run(async () =>
        {
            try
            {
                await _moderation.ModerateMessageAsync(
                    messageId: saved.Id ?? "",
                    roomId: convId,
                    senderId: meId,
                    content: req.Content,
                    roomClients: _hub.Clients.Users(new[] { meId, otherUserId })
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DM moderation failed for {Id}", saved.Id);
            }
        });

        return Ok(ApiResponse<object>.Ok(payload, "Sent"));
    }
}

public record SendDmRequest(string Content);
