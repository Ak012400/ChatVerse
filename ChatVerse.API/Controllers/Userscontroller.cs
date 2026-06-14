using ChatVerse.API.Extensions;
using ChatVerse.Infrastructure.ExternalServices.Cloudinary;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Data;

namespace ChatVerse.API.Controllers;

/// <summary>
/// User discovery — needed for direct-invite calls, DM creation, and
/// future features like @mentions / friend requests.
///
/// Search uses a simple case-insensitive prefix match on username with
/// a small page size. For Phase 3 / scale this should move to a full
/// text index (pg_trgm or a dedicated search engine).
/// </summary>
[ApiController]
[Route("api/users")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly ChatVerseDbContext _db;
    private readonly PostgresProcService _postgres;
    private readonly CloudinaryService _cloudinary;
    private readonly MongoService _mongo;
    private readonly ILogger<UsersController> _logger;

    private const int DefaultLimit = 10;
    private const int MaxLimit = 25;

    public UsersController(
        ChatVerseDbContext db,
        PostgresProcService postgres,
        CloudinaryService cloudinary,
        MongoService mongo,
        ILogger<UsersController> logger)
    {
        _db = db;
        _postgres = postgres;
        _cloudinary = cloudinary;
        _mongo = mongo;
        _logger = logger;
    }

    // ============================================================
    //  GET /api/users/search?q=ar&limit=10
    //  Prefix search on username — excludes the requester, guests,
    //  banned users, and suspended users. Returns the minimum fields
    //  the UI needs to render a result (id, username, trust band, age
    //  verified flag, online status — online comes later via Redis).
    // ============================================================
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string q,
        [FromQuery] int limit = DefaultLimit)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Ok(ApiResponse<object>.Ok(new
            {
                query = q ?? "",
                results = Array.Empty<object>()
            }));

        limit = Math.Clamp(limit, 1, MaxLimit);
        var meId = JwtService.GetUserId(User);

        var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            @"SELECT id, username, trust_score, age_verified
              FROM user_auth.users
              WHERE LOWER(username) LIKE LOWER(@p_q) || '%'
                AND id <> @p_me
                AND is_guest = false
                AND status = 'active'
              ORDER BY trust_score DESC, username ASC
              LIMIT @p_limit",
            conn);
        cmd.Parameters.AddWithValue("p_q", q);
        cmd.Parameters.AddWithValue("p_me", meId);
        cmd.Parameters.AddWithValue("p_limit", limit);

        var results = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new
            {
                userId = reader.GetGuid(0).ToString(),
                username = reader.GetString(1),
                trustScore = reader.GetInt16(2),
                ageVerified = reader.GetBoolean(3)
            });
        }

        return Ok(ApiResponse<object>.Ok(new
        {
            query = q,
            count = results.Count,
            results
        }));
    }

    // ============================================================
    //  GET /api/users/me
    //  Fresh server-side view of the signed-in user. Handy after a
    //  profile edit when the cached JWT still has the old username.
    // ============================================================
    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var meId = JwtService.GetUserId(User);
        var record = await _postgres.GetUserAuthByIdAsync(meId);
        if (record == null) return NotFound(ApiResponse.Fail("User not found"));

        return Ok(ApiResponse<object>.Ok(new
        {
            userId          = record.UserId.ToString(),
            username        = record.Username,
            trustScore      = record.TrustScore,
            isEmailVerified = record.IsEmailVerified,
            ageVerified     = record.AgeVerified,
        }));
    }

    // ============================================================
    //  PATCH /api/users/me
    //  Body: { username?: string }
    //  Only fields the user is allowed to edit themselves.
    // ============================================================
    [HttpPatch("me")]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateMeRequest req)
    {
        if (JwtService.GetIsGuest(User))
            return StatusCode(403, ApiResponse.Fail("Sign up to edit your profile."));

        var meId = JwtService.GetUserId(User);

        if (!string.IsNullOrWhiteSpace(req.Username))
        {
            var (ok, error) = await _postgres.UpdateUsernameAsync(meId, req.Username.Trim());
            if (!ok)
            {
                var message = error switch
                {
                    "USERNAME_TAKEN"    => "That username is already taken",
                    "INVALID_USERNAME"  => "Username must be 3–50 characters",
                    _ => "Could not update username",
                };
                return Conflict(ApiResponse.Fail(message));
            }
        }

        var record = await _postgres.GetUserAuthByIdAsync(meId);
        return Ok(ApiResponse<object>.Ok(new
        {
            userId   = meId.ToString(),
            username = record?.Username,
        }, "Profile updated"));
    }

    // ============================================================
    //  POST /api/users/me/avatar  (multipart/form-data)
    //  Server-side Cloudinary public upload. Stores the CDN URL on
    //  user_auth.users.avatar_url.
    // ============================================================
    [HttpPost("me/avatar")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<IActionResult> UploadAvatar([FromForm] IFormFile file)
    {
        if (JwtService.GetIsGuest(User))
            return StatusCode(403, ApiResponse.Fail("Sign up to set an avatar."));

        var meId = JwtService.GetUserId(User);
        if (file == null || file.Length == 0)
            return BadRequest(ApiResponse.Fail("No file uploaded"));
        if (file.Length > 5 * 1024 * 1024)
            return BadRequest(ApiResponse.Fail("Avatar too large (max 5 MB)"));

        var allowedContent = new[] { "image/jpeg", "image/png", "image/webp" };
        if (!allowedContent.Contains(file.ContentType))
            return BadRequest(ApiResponse.Fail("Use JPG, PNG, or WEBP"));

        await using var stream = file.OpenReadStream();
        var upload = await _cloudinary.UploadAvatarAsync(stream, file.FileName, meId.ToString());
        if (!upload.Success)
        {
            _logger.LogError("Avatar upload failed for {UserId}: {Err}", meId, upload.Error);
            return StatusCode(500, ApiResponse.Fail("Avatar upload failed"));
        }

        await _postgres.UpdateAvatarUrlAsync(meId, upload.SecureUrl);

        return Ok(ApiResponse<object>.Ok(new
        {
            avatarUrl = upload.SecureUrl,
        }, "Avatar updated"));
    }

    // ============================================================
    //  BLOCK SYSTEM — Standard (Instagram-style)
    //
    //  Blocks are directed (A blocks B ≠ B blocks A). The blocked
    //  user receives NO explicit notification — their attempts to
    //  call/DM the blocker silently fail server-side, mirroring
    //  mainstream platform behaviour. The blocker sees their full
    //  list in Settings; the blocked party only sees an aggregated
    //  "you appear in N blocklists" count, never names.
    // ============================================================

    /// <summary>POST /api/users/{userId}/block — block another user.</summary>
    [HttpPost("{userId}/block")]
    public async Task<IActionResult> BlockUser(string userId, [FromBody] BlockUserRequest? req)
    {
        var meId = JwtService.GetUserId(User).ToString();
        if (meId == userId)
            return BadRequest(ApiResponse.Fail("Cannot block yourself"));
        if (!Guid.TryParse(userId, out _))
            return BadRequest(ApiResponse.Fail("Invalid user id"));

        await _mongo.BlockUserAsync(meId, userId, req?.Reason);
        _logger.LogInformation("User {Me} blocked {Other}", meId, userId);
        return Ok(ApiResponse.Ok("Blocked"));
    }

    /// <summary>DELETE /api/users/{userId}/block — undo a block.</summary>
    [HttpDelete("{userId}/block")]
    public async Task<IActionResult> UnblockUser(string userId)
    {
        var meId = JwtService.GetUserId(User).ToString();
        var removed = await _mongo.UnblockUserAsync(meId, userId);
        if (!removed) return NotFound(ApiResponse.Fail("Not blocked"));
        _logger.LogInformation("User {Me} unblocked {Other}", meId, userId);
        return Ok(ApiResponse.Ok("Unblocked"));
    }

    /// <summary>
    /// GET /api/users/me/blocks — the caller's outgoing block list.
    /// Returns blocked user IDs + usernames + reason + when.
    /// </summary>
    [HttpGet("me/blocks")]
    public async Task<IActionResult> GetMyBlocks()
    {
        var meId = JwtService.GetUserId(User).ToString();
        var blocks = await _mongo.GetBlocksByMeAsync(meId);
        if (blocks.Count == 0) return Ok(ApiResponse<object>.Ok(Array.Empty<object>()));

        // Hydrate usernames + avatars in one Postgres round-trip.
        var ids = blocks.Select(b => Guid.Parse(b.BlockedId)).ToArray();
        var conn = await GetConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, username, avatar_url FROM user_auth.users WHERE id = ANY(@ids)", conn);
        cmd.Parameters.AddWithValue("ids", ids);
        var profiles = new Dictionary<string, (string Username, string? AvatarUrl)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetGuid(0).ToString();
            profiles[id] = (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }

        var result = blocks.Select(b => new
        {
            userId = b.BlockedId,
            username = profiles.TryGetValue(b.BlockedId, out var p) ? p.Username : "(deleted)",
            avatarUrl = profiles.TryGetValue(b.BlockedId, out var p2) ? p2.AvatarUrl : null,
            reason = b.Reason,
            blockedAt = b.CreatedAt,
        });
        return Ok(ApiResponse<object>.Ok(result));
    }

    /// <summary>
    /// GET /api/users/me/blocked-by-count — aggregated count of how many
    /// users have blocked the caller. NEVER returns names, by design.
    /// </summary>
    [HttpGet("me/blocked-by-count")]
    public async Task<IActionResult> GetBlockedByCount()
    {
        var meId = JwtService.GetUserId(User).ToString();
        var count = await _mongo.GetBlockedMeCountAsync(meId);
        return Ok(ApiResponse<object>.Ok(new { count }));
    }

    // ── Postgres helper (lives in this controller — same pattern as
    //    other controllers that need raw SQL alongside the proc service). ──
    private async Task<NpgsqlConnection> GetConnectionAsync()
    {
        var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();
        return conn;
    }
}

public record UpdateMeRequest(string? Username);
public record BlockUserRequest(string? Reason);
