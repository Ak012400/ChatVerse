using ChatVerse.API.Extensions;
using ChatVerse.Domain.Constants;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.Redis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

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
    //  All active *public* rooms with live active count. Private
    //  rooms (user-created, invite-only) never surface here.
    // ============================================================
    [HttpGet]
    public async Task<IActionResult> GetRooms()
    {
        var ageVerified = JwtService.GetAgeVerified(User);
        var rooms = await _mongo.GetActiveRoomsAsync();

        // Filter: hide private rooms entirely; hide 18+ rooms unless verified.
        var filtered = rooms
            .Where(r => !r.IsPrivate)
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

    // ============================================================
    //  POST /api/rooms
    //  Create a private/user-defined room. Trust gate keeps low-score
    //  accounts from spawning throwaway rooms.
    // ============================================================
    [HttpPost]
    public async Task<IActionResult> CreateRoom([FromBody] CreateRoomRequest req)
    {
        if (JwtService.GetIsGuest(User))
            return StatusCode(403, ApiResponse.Fail("Sign up to create rooms"));

        var trustScore = JwtService.GetTrustScore(User);
        if (trustScore < TrustBands.RestrictedMax)
            return StatusCode(403, ApiResponse.Fail(
                "Trust score too low to create rooms. Minimum required: 41"));

        if (string.IsNullOrWhiteSpace(req.DisplayName) || req.DisplayName.Length < 3 || req.DisplayName.Length > 60)
            return BadRequest(ApiResponse.Fail("Room name must be 3–60 characters"));

        var meId = JwtService.GetUserId(User).ToString();

        // Slug: kebab-case of display-name + 6-char suffix for uniqueness.
        var baseSlug = Regex.Replace(req.DisplayName.ToLower(), @"[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrEmpty(baseSlug)) baseSlug = "room";
        var slug = $"{baseSlug[..Math.Min(baseSlug.Length, 40)]}-{Guid.NewGuid().ToString("N")[..6]}";

        var inviteToken = Guid.NewGuid().ToString("N");

        var room = new Room
        {
            Slug         = slug,
            DisplayName  = req.DisplayName.Trim(),
            Description  = req.Description?.Trim(),
            Category     = string.IsNullOrWhiteSpace(req.Category) ? "public" : req.Category!.ToLower(),
            IconEmoji    = string.IsNullOrWhiteSpace(req.IconEmoji) ? "💬" : req.IconEmoji,
            Rules        = req.Rules?.ToList() ?? new(),
            CreatedBy    = meId,
            IsActive     = true,
            IsPrivate    = true,           // user-created → invite-only
            InviteToken  = inviteToken,    // persisted so we can look up later
            CreatedAt    = DateTime.UtcNow,
        };

        await _mongo.InsertRoomAsync(room);

        // The creator joins their own room implicitly so it shows up in
        // their sidebar without an extra round-trip.
        await _redis.AddJoinedRoomAsync(meId, slug);

        _logger.LogInformation("Private room {Slug} created by {UserId}", slug, meId);

        return Ok(ApiResponse<object>.Ok(new
        {
            slug,
            displayName = room.DisplayName,
            description = room.Description,
            category    = room.Category,
            iconEmoji   = room.IconEmoji,
            createdBy   = meId,
            inviteToken,
            inviteUrl   = $"/rooms/join/{inviteToken}",
        }, "Room created"));
    }

    // ============================================================
    //  GET /api/rooms/join/{token}
    //  Public preview — returns minimal info for a share-link page.
    // ============================================================
    [AllowAnonymous]
    [HttpGet("join/{token}")]
    public async Task<IActionResult> PreviewByInvite(string token)
    {
        var room = await _mongo.GetRoomByInviteTokenAsync(token);
        if (room == null || !room.IsActive)
            return NotFound(ApiResponse.Fail("Invite is invalid or expired"));

        return Ok(ApiResponse<object>.Ok(new
        {
            slug        = room.Slug,
            displayName = room.DisplayName,
            description = room.Description,
            iconEmoji   = room.IconEmoji,
            category    = room.Category,
        }));
    }

    // ============================================================
    //  POST /api/rooms/join/{token}
    //  Actually joins the current user (guest or registered) to a
    //  private room. Adds the slug to their joined-rooms set so it
    //  appears in their "My rooms" sidebar from now on.
    // ============================================================
    [HttpPost("join/{token}")]
    public async Task<IActionResult> JoinByInvite(string token)
    {
        var meId = JwtService.GetUserId(User).ToString();

        var room = await _mongo.GetRoomByInviteTokenAsync(token);
        if (room == null || !room.IsActive)
            return NotFound(ApiResponse.Fail("Invite is invalid or expired"));

        // Idempotent — adding a member that's already in the Redis set is a no-op.
        await _redis.AddJoinedRoomAsync(meId, room.Slug);

        _logger.LogInformation("User {UserId} joined private room {Slug} via invite",
            meId, room.Slug);

        return Ok(ApiResponse<object>.Ok(new
        {
            slug        = room.Slug,
            displayName = room.DisplayName,
            description = room.Description,
            iconEmoji   = room.IconEmoji,
            category    = room.Category,
        }, "Joined room"));
    }

    // ============================================================
    //  GET /api/rooms/mine
    //  The user's joined private rooms — kept separate from /rooms
    //  so the public list stays cacheable. Returns same shape as
    //  /rooms so the sidebar can render them uniformly.
    // ============================================================
    [HttpGet("mine")]
    public async Task<IActionResult> GetMyRooms()
    {
        var meId = JwtService.GetUserId(User).ToString();
        var slugs = await _redis.GetJoinedRoomsAsync(meId);
        if (slugs.Count == 0) return Ok(ApiResponse<object>.Ok(Array.Empty<object>()));

        var rooms = await _mongo.GetRoomsBySlugsAsync(slugs);

        // Prune any slugs that no longer resolve to an active room.
        var aliveSlugs = rooms.Select(r => r.Slug).ToHashSet();
        foreach (var stale in slugs.Where(s => !aliveSlugs.Contains(s)))
            _ = _redis.RemoveJoinedRoomAsync(meId, stale);

        var result = await Task.WhenAll(rooms.Select(async r =>
        {
            var live = await _redis.GetRoomActiveCountAsync(r.Slug);
            return new
            {
                slug          = r.Slug,
                displayName   = r.DisplayName,
                description   = r.Description,
                category      = r.Category,
                iconEmoji     = r.IconEmoji,
                activeNow     = live > 0 ? live : r.Stats.ActiveNow,
                totalMessages = r.Stats.TotalMessages,
                isPrivate     = true,
            };
        }));

        return Ok(ApiResponse<object>.Ok(result));
    }

    // ============================================================
    //  DELETE /api/rooms/{slug}
    //  Only the original creator may deactivate (Phase 1 — no admin
    //  override yet beyond the admin dashboard).
    // ============================================================
    [HttpDelete("{slug}")]
    public async Task<IActionResult> DeactivateRoom(string slug)
    {
        var meId = JwtService.GetUserId(User).ToString();
        var room = await _mongo.GetRoomBySlugAsync(slug);
        if (room == null) return NotFound(ApiResponse.Fail("Room not found"));
        if (room.CreatedBy != meId)
            return StatusCode(403, ApiResponse.Fail("Only the creator can close this room"));

        await _mongo.DeactivateRoomAsync(slug);
        _logger.LogInformation("Room {Slug} deactivated by {UserId}", slug, meId);
        return Ok(ApiResponse.Ok("Room closed"));
    }
}

public record CreateRoomRequest(
    [System.ComponentModel.DataAnnotations.Required] string DisplayName,
    string? Description,
    string? Category,
    string? IconEmoji,
    IEnumerable<string>? Rules
);