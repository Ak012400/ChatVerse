using ChatVerse.API.Extensions;
using ChatVerse.Domain.Constants;
using ChatVerse.Infrastructure.Persistence.Redis;
using ChatVerse.Infrastructure.Services.LiveKit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatVerse.API.Controllers;

/// <summary>
/// Token endpoint for direct invite calls. The invite/accept handshake
/// happens through ChatHub (see <c>InviteToCall</c> / <c>AcceptCall</c>).
/// Once both sides agree on a shared room name, each fetches its own
/// LiveKit token here — keeping API keys server-side and gating on JWT.
/// </summary>
[ApiController]
[Route("api/direct-call")]
[Authorize]
public class DirectCallController : ControllerBase
{
    private readonly LiveKitService _liveKit;
    private readonly RedisService _redis;
    private readonly ILogger<DirectCallController> _logger;

    // LiveKit deletes a room after it has been empty for this many seconds.
    // "Empty" means no participants connected. A 60-second value was too
    // aggressive: a single network blip on either side could leave the
    // room temporarily empty, trip the 60-second timer, and permanently
    // destroy the room — preventing reconnection. Bumping to 10 minutes
    // gives both sides plenty of grace for transient drops without
    // meaningfully increasing LiveKit Cloud resource usage (empty rooms
    // cost almost nothing on the free tier).
    private const int EmptyTimeoutSeconds = 600;
    private const int MaxParticipants = 2;

    public DirectCallController(
        LiveKitService liveKit,
        RedisService redis,
        ILogger<DirectCallController> logger)
    {
        _liveKit = liveKit;
        _redis = redis;
        _logger = logger;
    }

    // ============================================================
    //  POST /api/direct-call/token
    //  Body: { roomName }
    //  - Trust gate
    //  - Lazily create the LiveKit room if it doesn't exist (first
    //    caller to fetch the token wins; the second call is a no-op).
    // ============================================================
    [HttpPost("token")]
    public async Task<IActionResult> GetToken([FromBody] DirectCallTokenRequest req)
    {
        var userId = JwtService.GetUserId(User).ToString();
        var username = JwtService.GetUsername(User);
        var trustScore = JwtService.GetTrustScore(User);

        if (trustScore < TrustBands.RestrictedMax)
            return StatusCode(403, ApiResponse.Fail(
                "Trust score too low for direct calls. Minimum required: 41"));

        if (string.IsNullOrWhiteSpace(req.RoomName) ||
            !req.RoomName.StartsWith("dc-", StringComparison.Ordinal))
            return BadRequest(ApiResponse.Fail("Invalid roomName"));

        // Idempotent create — LiveKitService returns false on duplicate
        // but the token still works, so we don't fail the request.
        await _liveKit.CreateRoomAsync(
            req.RoomName,
            emptyTimeoutSeconds: EmptyTimeoutSeconds,
            maxParticipants: MaxParticipants);

        var token = _liveKit.GenerateToken(
            roomName: req.RoomName,
            userId: userId,
            username: username);

        _logger.LogInformation(
            "Direct-call token minted — user {UserId} → {Room}",
            userId, req.RoomName);

        return Ok(ApiResponse<object>.Ok(new
        {
            token,
            roomName = req.RoomName,
            serverUrl = _liveKit.GetServerUrl(),
            maxParticipants = MaxParticipants
        }));
    }
}

public record DirectCallTokenRequest(
    [System.ComponentModel.DataAnnotations.Required] string RoomName
);
