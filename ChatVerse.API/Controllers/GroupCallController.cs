using ChatVerse.API.Extensions;
using ChatVerse.Domain.Constants;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.Redis;
using ChatVerse.Infrastructure.Services.LiveKit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatVerse.API.Controllers;

[ApiController]
[Route("api/group-call")]
[Authorize]
public class GroupCallController : ControllerBase
{
    private readonly LiveKitService _liveKit;
    private readonly MongoService _mongo;
    private readonly RedisService _redis;
    private readonly ILogger<GroupCallController> _logger;

    // Redis key — active group calls track karne ke liye
    private static string GroupCallKey(string roomName)
        => $"groupcall:active:{roomName}";

    public GroupCallController(
        LiveKitService liveKit,
        MongoService mongo,
        RedisService redis,
        ILogger<GroupCallController> logger)
    {
        _liveKit = liveKit;
        _mongo = mongo;
        _redis = redis;
        _logger = logger;
    }

    // ============================================================
    //  POST /api/group-call/token
    //  Room join karne ke liye LiveKit token lo
    // ============================================================
    [HttpPost("token")]
    public async Task<IActionResult> GetToken([FromBody] GetGroupCallTokenRequest req)
    {
        var userId = JwtService.GetUserId(User).ToString();
        var username = JwtService.GetUsername(User);
        var trustScore = JwtService.GetTrustScore(User);

        // Trust gate
        if (trustScore < TrustBands.RestrictedMax)
            return StatusCode(403, ApiResponse.Fail(
                "Trust score too low for group calls. Minimum required: 41"));

        // Room name sanitize
        var roomName = SanitizeRoomName(req.RoomName);
        if (string.IsNullOrEmpty(roomName))
            return BadRequest(ApiResponse.Fail("Invalid room name"));

        // 18+ room check
        if (roomName.StartsWith("adult-") && !JwtService.GetAgeVerified(User))
            return StatusCode(403, ApiResponse.Fail(
                "Age verification required for adult rooms"));

        // Room exist nahi karta toh create karo
        var roomExists = await _redis.KeyExistsAsync(GroupCallKey(roomName));
        if (!roomExists)
        {
            await _liveKit.CreateRoomAsync(
                roomName,
                emptyTimeoutSeconds: 300,
                maxParticipants: req.MaxParticipants ?? 10
            );

            // Redis mein track karo — 4 hour TTL
            await _redis.SetStringAsync(
                GroupCallKey(roomName),
                $"{userId}:{DateTime.UtcNow:O}",
                TimeSpan.FromHours(4)
            );

            _logger.LogInformation(
                "Group call room created: {RoomName} by {UserId}", roomName, userId);
        }

        // Token generate karo
        var token = _liveKit.GenerateToken(
            roomName: roomName,
            userId: userId,
            username: username
        );

        return Ok(ApiResponse<object>.Ok(new
        {
            token,
            roomName,
            serverUrl = _liveKit.GetServerUrl(),
            maxParticipants = req.MaxParticipants ?? 10
        }));
    }

    // ============================================================
    //  POST /api/group-call/create
    //  Explicitly room create karo — host ke liye
    // ============================================================
    [HttpPost("create")]
    public async Task<IActionResult> CreateRoom([FromBody] CreateGroupCallRequest req)
    {
        var userId = JwtService.GetUserId(User).ToString();
        var username = JwtService.GetUsername(User);
        var trustScore = JwtService.GetTrustScore(User);

        if (trustScore < TrustBands.NormalMax)
            return StatusCode(403, ApiResponse.Fail(
                "Trust score too low to create group calls. Minimum required: 71"));

        var roomName = $"gc-{SanitizeRoomName(req.RoomName)}-{Guid.NewGuid().ToString("N")[..6]}";

        var created = await _liveKit.CreateRoomAsync(
            roomName,
            emptyTimeoutSeconds: 600,
            maxParticipants: Math.Clamp(req.MaxParticipants, 2, 50)
        );

        if (!created)
            return StatusCode(500, ApiResponse.Fail("Failed to create group call room"));

        await _redis.SetStringAsync(
            GroupCallKey(roomName),
            $"{userId}:{DateTime.UtcNow:O}",
            TimeSpan.FromHours(4)
        );

        // Token for host
        var token = _liveKit.GenerateToken(
            roomName: roomName,
            userId: userId,
            username: username
        );

        _logger.LogInformation(
            "Group call created: {RoomName} by {UserId}", roomName, userId);

        return Ok(ApiResponse<object>.Ok(new
        {
            token,
            roomName,
            serverUrl = _liveKit.GetServerUrl(),
            inviteLink = $"/group-call/{roomName}",
            maxParticipants = req.MaxParticipants
        }));
    }

    // ============================================================
    //  GET /api/group-call/{roomName}/participants
    //  Room mein kaun kaun hai
    // ============================================================
    [HttpGet("{roomName}/participants")]
    public async Task<IActionResult> GetParticipants(string roomName)
    {
        var participants = await _liveKit.GetRoomParticipantsAsync(roomName);

        return Ok(ApiResponse<object>.Ok(new
        {
            roomName,
            count = participants.Count,
            participants = participants.Select(p => new
            {
                userId = p.Identity,
                username = p.Name,
                joinedAt = DateTimeOffset.FromUnixTimeSeconds(p.JoinedAt).ToString("o"),
                // String comparison se Enum mismatch error nahi aayega
                isPublishing = p.Tracks.Any(t => t.Type.ToString().Contains("Video", StringComparison.OrdinalIgnoreCase) && !t.Muted)
            })
        }));
    }

    // ============================================================
    //  DELETE /api/group-call/{roomName}
    //  Room band karo
    // ============================================================
    [HttpDelete("{roomName}")]
    public async Task<IActionResult> EndCall(string roomName)
    {
        var userId = JwtService.GetUserId(User).ToString();

        await _liveKit.DeleteRoomAsync(roomName);
        await _redis.DeleteKeyAsync(GroupCallKey(roomName));

        _logger.LogInformation("Group call ended: {RoomName} by {UserId}", roomName, userId);

        return Ok(ApiResponse.Ok("Group call ended"));
    }

    // ============================================================
    //  GET /api/group-call/active
    //  Abhi kitne active group calls hain
    // ============================================================
    [HttpGet("active")]
    public async Task<IActionResult> GetActiveRooms()
    {
        var rooms = await _liveKit.ListActiveRoomsAsync();

        return Ok(ApiResponse<object>.Ok(new
        {
            count = rooms.Count,
            rooms = rooms.Select(r => new
            {
                roomName = r.Name,
                participants = r.NumParticipants,
                createdAt = DateTimeOffset.FromUnixTimeSeconds(r.CreationTime).ToString("o")
            })
        }));
    }

    // ── Private helper ────────────────────────────────────────
    private static string SanitizeRoomName(string name)
    {
        var sanitized = new string(name
            .ToLower()
            .Replace(" ", "-")
            .Where(c => char.IsLetterOrDigit(c) || c == '-')
            .ToArray());

        return sanitized.Length > 50
            ? sanitized[..50]
            : sanitized;
    }
}

// ── Request DTOs ──────────────────────────────────────────────
public record GetGroupCallTokenRequest(
    [System.ComponentModel.DataAnnotations.Required] string RoomName,
    int? MaxParticipants
);

public record CreateGroupCallRequest(
    [System.ComponentModel.DataAnnotations.Required] string RoomName,
    int MaxParticipants = 10
);