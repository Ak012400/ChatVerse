using ChatVerse.API.Extensions;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.Redis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatVerse.API.Controllers;

[ApiController]
[Route("api/rooms")]
[Authorize]
public class RoomsController : ControllerBase
{
    private readonly MongoService _mongo;
    private readonly RedisService _redis;
    private readonly ILogger<RoomsController> _logger;

    public RoomsController(
        MongoService mongo,
        RedisService redis,
        ILogger<RoomsController> logger)
    {
        _mongo = mongo;
        _redis = redis;
        _logger = logger;
    }

    // ============================================================
    //  GET /api/rooms
    //  All active rooms with live active count from Redis
    // ============================================================
    [HttpGet]
    public async Task<IActionResult> GetRooms()
    {
        var ageVerified = JwtService.GetAgeVerified(User);
        var rooms = await _mongo.GetActiveRoomsAsync();

        // Filter 18+ rooms for unverified users
        var filtered = rooms
            .Where(r => r.Category != "18plus" || ageVerified)
            .ToList();

        // Enrich with live Redis counts
        var result = await Task.WhenAll(filtered.Select(async r =>
        {
            var liveCount = await _redis.GetRoomActiveCountAsync(r.Slug);
            return new
            {
                slug = r.Slug,
                displayName = r.DisplayName,
                description = r.Description,
                category = r.Category,
                iconEmoji = r.IconEmoji,
                activeNow = liveCount > 0 ? liveCount : r.Stats.ActiveNow,
                totalMessages = r.Stats.TotalMessages
            };
        }));

        return Ok(ApiResponse<object>.Ok(result));
    }

    // ============================================================
    //  GET /api/rooms/{slug}
    //  Single room details
    // ============================================================
    [HttpGet("{slug}")]
    public async Task<IActionResult> GetRoom(string slug)
    {
        var ageVerified = JwtService.GetAgeVerified(User);
        var room = await _mongo.GetRoomBySlugAsync(slug);

        if (room == null)
            return NotFound(ApiResponse.Fail("Room not found"));

        if (room.Category == "18plus" && !ageVerified)
            return StatusCode(403, ApiResponse.Fail("Age verification required"));

        var liveCount = await _redis.GetRoomActiveCountAsync(slug);

        return Ok(ApiResponse<object>.Ok(new
        {
            slug = room.Slug,
            displayName = room.DisplayName,
            description = room.Description,
            category = room.Category,
            iconEmoji = room.IconEmoji,
            rules = room.Rules,
            activeNow = liveCount > 0 ? liveCount : room.Stats.ActiveNow,
            totalMessages = room.Stats.TotalMessages
        }));
    }

    // ============================================================
    //  GET /api/rooms/{slug}/messages?skip=0&limit=50
    //  Paginated message history (REST fallback — SignalR is primary)
    // ============================================================
    [HttpGet("{slug}/messages")]
    public async Task<IActionResult> GetMessages(
        string slug,
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 50)
    {
        var ageVerified = JwtService.GetAgeVerified(User);
        var room = await _mongo.GetRoomBySlugAsync(slug);

        if (room == null)
            return NotFound(ApiResponse.Fail("Room not found"));

        if (room.Category == "18plus" && !ageVerified)
            return StatusCode(403, ApiResponse.Fail("Age verification required"));

        // Clamp limit
        limit = Math.Clamp(limit, 1, 100);

        var messages = await _mongo.GetRoomMessagesAsync(slug, skip, limit);

        var result = messages.Select(m => new
        {
            id = m.Id,
            senderId = m.SenderId,
            senderName = m.SenderName,
            senderAvatar = m.SenderAvatarUrl,
            content = m.Content,
            type = m.Type,
            mediaUrl = m.MediaUrl,
            replyTo = m.ReplyTo,
            reactions = m.Reactions,
            modStatus = m.Moderation.Status,
            editedAt = m.EditedAt,
            createdAt = m.CreatedAt
        });

        return Ok(ApiResponse<object>.Ok(new
        {
            roomSlug = slug,
            skip,
            limit,
            messages = result
        }));
    }
}