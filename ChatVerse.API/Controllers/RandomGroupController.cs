using ChatVerse.API.Extensions;
using ChatVerse.Domain.Constants;
using ChatVerse.Domain.Enums;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using ChatVerse.Infrastructure.Persistence.Redis;
using ChatVerse.Infrastructure.Services.LiveKit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatVerse.API.Controllers;

/// <summary>
/// Random group video lobbies — Omegle-style group chat.
///
/// User joins → backend finds the most-filled open LiveKit lobby (best
/// social signal: walk into a busy room) or spins up a new one when no
/// space is left. Cap is small enough that nsfwjs on each client can
/// meaningfully police behaviour (default 6 — face quality stays high
/// and AI scan covers everyone visible).
/// </summary>
[ApiController]
[Route("api/random-group")]
[Authorize]
public class RandomGroupController : ControllerBase
{
    private readonly LiveKitService _liveKit;
    private readonly RedisService _redis;
    private readonly PostgresProcService _postgres;
    private readonly ILogger<RandomGroupController> _logger;

    // Tunables — keep small so each peer can self-moderate effectively.
    private const int MaxParticipants = 6;
    private const int LobbyEmptyTimeoutSeconds = 300; // 5 min
    private const string RoomPrefix = "rg-";

    public RandomGroupController(
        LiveKitService liveKit,
        RedisService redis,
        PostgresProcService postgres,
        ILogger<RandomGroupController> logger)
    {
        _liveKit = liveKit;
        _redis = redis;
        _postgres = postgres;
        _logger = logger;
    }

    // ============================================================
    //  POST /api/random-group/join
    //  Get into an open lobby (or new one). Returns LiveKit token.
    // ============================================================
    [HttpPost("join")]
    public async Task<IActionResult> Join()
    {
        var userId = JwtService.GetUserId(User).ToString();
        var username = JwtService.GetUsername(User);
        var trustScore = JwtService.GetTrustScore(User);

        // Trust gate — same threshold as random 1-on-1
        if (trustScore < TrustBands.RestrictedMax)
            return StatusCode(403, ApiResponse.Fail(
                "Trust score too low for group chat. Minimum required: 41"));

        string roomName;
        int count;

        // Look for an existing lobby with space.
        var open = await _redis.FindOpenRandomGroupAsync(MaxParticipants);
        if (open != null)
        {
            roomName = open.Value.RoomName;
            var newCount = await _redis.IncrementRandomGroupAsync(roomName);
            count = (int)newCount;

            // If this join filled the room, unlist it from open set.
            if (count >= MaxParticipants)
                await _redis.UnlistRandomGroupAsync(roomName);
        }
        else
        {
            // Spin up a new lobby.
            roomName = $"{RoomPrefix}{Guid.NewGuid().ToString("N")[..10]}";
            var created = await _liveKit.CreateRoomAsync(
                roomName,
                emptyTimeoutSeconds: LobbyEmptyTimeoutSeconds,
                maxParticipants: MaxParticipants);

            if (!created)
                return StatusCode(500, ApiResponse.Fail("Failed to create lobby"));

            await _redis.RegisterRandomGroupAsync(roomName);
            count = 1;
        }

        var token = _liveKit.GenerateToken(
            roomName: roomName,
            userId: userId,
            username: username);

        _logger.LogInformation(
            "Random group join — user {UserId} → room {Room} ({Count}/{Max})",
            userId, roomName, count, MaxParticipants);

        return Ok(ApiResponse<object>.Ok(new
        {
            token,
            roomName,
            serverUrl = _liveKit.GetServerUrl(),
            count,
            maxParticipants = MaxParticipants
        }));
    }

    // ============================================================
    //  POST /api/random-group/leave
    //  Frontend SHOULD call this when leaving so the count stays accurate
    //  for the next joiner. (LiveKit's own disconnect handles cleanup
    //  too — we use the Redis count as a soft hint.)
    // ============================================================
    [HttpPost("leave")]
    public async Task<IActionResult> Leave([FromBody] LeaveGroupRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.RoomName))
            return BadRequest(ApiResponse.Fail("roomName is required"));

        var newCount = await _redis.DecrementRandomGroupAsync(req.RoomName);

        // If room empties, also tell LiveKit so the SFU doesn't keep it
        // around for the 5-min empty timeout.
        if (newCount <= 0)
        {
            await _liveKit.DeleteRoomAsync(req.RoomName);
        }

        return Ok(ApiResponse.Ok("Left lobby"));
    }

    // ============================================================
    //  POST /api/random-group/report
    //  Browser nsfwjs detected violation OR a peer manually reported.
    //  - If `selfReport=true` (auto-detection of own feed) → kick the
    //    violator immediately with full trust penalty.
    //  - If a peer reports someone else → flag for review (mongo) and
    //    let the moderation team / repeat-report threshold decide.
    // ============================================================
    [HttpPost("report")]
    public async Task<IActionResult> Report([FromBody] ReportGroupViolationRequest req)
    {
        var reporterId = JwtService.GetUserId(User).ToString();

        if (string.IsNullOrWhiteSpace(req.RoomName) ||
            string.IsNullOrWhiteSpace(req.ViolatorUserId))
            return BadRequest(ApiResponse.Fail("roomName and violatorUserId are required"));

        if (!Guid.TryParse(req.ViolatorUserId, out var violatorGuid))
            return BadRequest(ApiResponse.Fail("Invalid violatorUserId"));

        // Auto-kick path — only trust when the violator self-reports
        // (nsfwjs scanning their own camera). Self-reports can't be
        // weaponised to kick others.
        if (req.SelfReport && reporterId == req.ViolatorUserId)
        {
            await _liveKit.RemoveParticipantAsync(req.RoomName, req.ViolatorUserId);
            await _redis.DecrementRandomGroupAsync(req.RoomName);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _postgres.ApplyTrustEventAsync(
                        userId: violatorGuid,
                        eventType: TrustEventType.VideoNsfw,
                        delta: (short)TrustDeltas.VideoNsfw,
                        reason: $"Group NSFW (self-detected) — label: {req.Label} ({req.Confidence:P0})",
                        refSource: "random_group"
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Trust penalty failed for self-report in {Room}", req.RoomName);
                }
            });

            _logger.LogWarning("Self-NSFW kick — user {UserId} from {Room}", reporterId, req.RoomName);

            return Ok(ApiResponse<object>.Ok(new
            {
                action = "kicked",
                reason = "Inappropriate content detected on your camera"
            }));
        }

        // Peer-report path — log it, don't auto-kick. A separate admin
        // / threshold service can act on accumulated reports.
        _logger.LogInformation(
            "Peer NSFW report — reporter {ReporterId} → violator {ViolatorId} in {Room}",
            reporterId, req.ViolatorUserId, req.RoomName);

        return Ok(ApiResponse<object>.Ok(new
        {
            action = "queued_for_review",
            reason = "Report submitted. Our team will review."
        }));
    }

    // ============================================================
    //  GET /api/random-group/active
    //  Snapshot of open lobbies (for admin / debug use).
    // ============================================================
    [HttpGet("active")]
    public async Task<IActionResult> GetActive()
    {
        var lobbies = await _redis.ListRandomGroupsAsync();
        return Ok(ApiResponse<object>.Ok(new
        {
            count = lobbies.Count,
            lobbies = lobbies.Select(l => new
            {
                roomName = l.RoomName,
                participants = l.Count,
                maxParticipants = MaxParticipants
            })
        }));
    }
}

// ── Request DTOs ──────────────────────────────────────────────
public record LeaveGroupRequest(
    [System.ComponentModel.DataAnnotations.Required] string RoomName
);

public record ReportGroupViolationRequest(
    [System.ComponentModel.DataAnnotations.Required] string RoomName,
    [System.ComponentModel.DataAnnotations.Required] string ViolatorUserId,
    bool SelfReport = false,
    string? Label = null,
    double Confidence = 0d
);
