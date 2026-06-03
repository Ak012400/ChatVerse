using ChatVerse.API.Extensions;
using ChatVerse.Infrastructure.ExternalServices.Cloudinary;
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
    private readonly ILogger<UsersController> _logger;

    private const int DefaultLimit = 10;
    private const int MaxLimit = 25;

    public UsersController(
        ChatVerseDbContext db,
        PostgresProcService postgres,
        CloudinaryService cloudinary,
        ILogger<UsersController> logger)
    {
        _db = db;
        _postgres = postgres;
        _cloudinary = cloudinary;
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
}

public record UpdateMeRequest(string? Username);
