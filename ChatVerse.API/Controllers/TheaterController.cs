using ChatVerse.API.Extensions;
using ChatVerse.Domain.Constants;
using ChatVerse.Infrastructure.Persistence.Redis;
using ChatVerse.Infrastructure.Services.LiveKit;
using ChatVerse.Infrastructure.Services.UserState;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatVerse.API.Controllers;

/// <summary>
/// Watch-party rooms — up to 10 friends sharing one user's screen via
/// LiveKit's screen-share track plus per-participant video tiles. The
/// "host" of the moment is just whichever participant currently has a
/// screen-share track published; LiveKit's track ownership model means
/// the rotation happens client-side without server bookkeeping.
///
/// Mobile reality: phone browsers can't INITIATE getDisplayMedia, but
/// they can VIEW a shared screen — so the client lets phone users join
/// as viewers and hides the "Share screen" button automatically.
///
/// Copyright note: this controller doesn't ship/proxy any media. It
/// only provisions LiveKit rooms and tokens. What participants choose
/// to put on their screens is their responsibility — same as on Zoom
/// or Google Meet. The client surfaces a disclaimer banner.
/// </summary>
[ApiController]
[Route("api/theater")]
[Authorize]
public class TheaterController : ControllerBase
{
    private readonly LiveKitService _liveKit;
    private readonly RedisService _redis;
    private readonly UserStateService _userState;
    private readonly ILogger<TheaterController> _logger;

    private static string TheaterKey(string roomName) => $"theater:active:{roomName}";

    public TheaterController(
        LiveKitService liveKit,
        RedisService redis,
        UserStateService userState,
        ILogger<TheaterController> logger)
    {
        _liveKit = liveKit;
        _redis = redis;
        _userState = userState;
        _logger = logger;
    }

    // ============================================================
    //  POST /api/theater/create
    //  Host creates a watch party. Returns LiveKit token + room name.
    // ============================================================
    [HttpPost("create")]
    public async Task<IActionResult> Create([FromBody] CreateTheaterRequest req)
    {
        var userGuid = JwtService.GetUserId(User);
        var userId = userGuid.ToString();
        var username = JwtService.GetUsername(User);

        // Trust gate — same threshold as group calls / video chat. Stops
        // brand-new accounts from spinning up rooms to harvest viewers.
        var trustScore = await _userState.GetTrustScoreAsync(userGuid);
        if (trustScore < TrustBands.RestrictedMax)
            return StatusCode(403, ApiResponse.Fail(
                "Trust score too low for theater rooms. Minimum required: 41"));

        if (string.IsNullOrWhiteSpace(req.Title) || req.Title.Length < 3 || req.Title.Length > 60)
            return BadRequest(ApiResponse.Fail("Title must be 3–60 characters"));

        // Slug carries the th- prefix so the client router can detect
        // theater rooms without an extra round-trip.
        var roomName = $"{Theater.SlugPrefix}{SanitizeRoomName(req.Title)}-{Guid.NewGuid().ToString("N")[..6]}";

        var created = await _liveKit.CreateRoomAsync(
            roomName,
            emptyTimeoutSeconds: Theater.EmptyTimeoutSeconds,
            maxParticipants: Theater.MaxParticipants);

        if (!created)
            return StatusCode(500, ApiResponse.Fail("Could not create theater room"));

        // Lightweight metadata so the client can render a card on the
        // active-rooms list before any tokens are issued.
        await _redis.SetStringAsync(
            TheaterKey(roomName),
            $"{userId}:{username}:{req.Title}:{DateTime.UtcNow:O}",
            TimeSpan.FromHours(4));

        var token = _liveKit.GenerateToken(roomName: roomName, userId: userId, username: username);

        _logger.LogInformation("Theater room created: {Room} by {User}", roomName, userId);

        return Ok(ApiResponse<object>.Ok(new
        {
            roomName,
            token,
            serverUrl = _liveKit.GetServerUrl(),
            title = req.Title,
            maxParticipants = Theater.MaxParticipants,
            shareLink = $"/theater/{roomName}",
        }));
    }

    // ============================================================
    //  POST /api/theater/token   { roomName }
    //  A viewer joins an existing room. Capacity check stops the
    //  11th joiner — LiveKit enforces this server-side too, but we
    //  give a friendlier error before they try.
    // ============================================================
    [HttpPost("token")]
    public async Task<IActionResult> JoinToken([FromBody] JoinTheaterRequest req)
    {
        var userGuid = JwtService.GetUserId(User);
        var userId = userGuid.ToString();
        var username = JwtService.GetUsername(User);

        var trustScore = await _userState.GetTrustScoreAsync(userGuid);
        if (trustScore < TrustBands.RestrictedMax)
            return StatusCode(403, ApiResponse.Fail(
                "Trust score too low to join theater rooms"));

        if (string.IsNullOrWhiteSpace(req.RoomName) || !req.RoomName.StartsWith(Theater.SlugPrefix))
            return BadRequest(ApiResponse.Fail("Invalid theater room name"));

        var participants = await _liveKit.GetRoomParticipantsAsync(req.RoomName);
        if (participants.Count >= Theater.MaxParticipants)
            return StatusCode(409, ApiResponse.Fail(
                $"Theater is full ({Theater.MaxParticipants} max)"));

        var token = _liveKit.GenerateToken(roomName: req.RoomName, userId: userId, username: username);

        return Ok(ApiResponse<object>.Ok(new
        {
            token,
            serverUrl = _liveKit.GetServerUrl(),
            roomName = req.RoomName,
            currentParticipants = participants.Count,
            maxParticipants = Theater.MaxParticipants,
        }));
    }

    // ============================================================
    //  GET /api/theater/active
    //  Lobby listing — shows currently-running theaters so a user
    //  can browse and join without an invite link.
    // ============================================================
    [HttpGet("active")]
    public async Task<IActionResult> ListActive()
    {
        var rooms = await _liveKit.ListActiveRoomsAsync();
        var theaters = rooms
            .Where(r => r.Name.StartsWith(Theater.SlugPrefix))
            .Select(r => new
            {
                roomName = r.Name,
                participants = r.NumParticipants,
                maxParticipants = Theater.MaxParticipants,
                createdAt = DateTimeOffset.FromUnixTimeSeconds(r.CreationTime).ToString("o"),
            });

        return Ok(ApiResponse<object>.Ok(new { theaters }));
    }

    // ============================================================
    //  DELETE /api/theater/{roomName}
    //  Host or any participant explicitly tears down the room.
    // ============================================================
    [HttpDelete("{roomName}")]
    public async Task<IActionResult> End(string roomName)
    {
        var userId = JwtService.GetUserId(User).ToString();
        await _liveKit.DeleteRoomAsync(roomName);
        await _redis.DeleteKeyAsync(TheaterKey(roomName));
        _logger.LogInformation("Theater room closed: {Room} by {User}", roomName, userId);
        return Ok(ApiResponse.Ok("Theater closed"));
    }

    // ── Helpers ─────────────────────────────────────────────────
    private static string SanitizeRoomName(string raw)
    {
        var s = new string(raw.ToLowerInvariant().Replace(' ', '-')
            .Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return s.Length > 40 ? s[..40] : s;
    }
}

public record CreateTheaterRequest(string Title);
public record JoinTheaterRequest(string RoomName);
