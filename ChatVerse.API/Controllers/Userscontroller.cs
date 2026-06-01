using ChatVerse.API.Extensions;
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
    private readonly ILogger<UsersController> _logger;

    private const int DefaultLimit = 10;
    private const int MaxLimit = 25;

    public UsersController(
        ChatVerseDbContext db,
        ILogger<UsersController> logger)
    {
        _db = db;
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
}
