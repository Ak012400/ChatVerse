using ChatVerse.API.Extensions;
using Microsoft.EntityFrameworkCore;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatVerse.API.Controllers;

[ApiController]
[Route("api/trust")]
[Authorize]
public class TrustController : ControllerBase
{
    private readonly PostgresProcService _postgres;
    private readonly ILogger<TrustController> _logger;

    public TrustController(
        PostgresProcService postgres,
        ILogger<TrustController> logger)
    {
        _postgres = postgres;
        _logger = logger;
    }

    // ============================================================
    //  GET /api/trust/score
    //  Current user's trust score + band
    // ============================================================
    [HttpGet("score")]
    public async Task<IActionResult> GetScore()
    {
        var userId = JwtService.GetUserId(User);
        var conn = await GetConnectionAsync();

        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT * FROM trust.usp_get_trust_score(@p_user_id)", conn);
        cmd.Parameters.AddWithValue("p_user_id", userId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return NotFound(ApiResponse.Fail("User not found"));

        return Ok(ApiResponse<object>.Ok(new
        {
            score = reader.GetInt16(0),
            band = reader.GetString(1),
            bandLabel = reader.GetString(2)
        }));
    }

    // ============================================================
    //  GET /api/trust/history?skip=0&limit=20
    //  Trust event history for current user
    // ============================================================
    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 20)
    {
        var userId = JwtService.GetUserId(User);
        limit = Math.Clamp(limit, 1, 100);

        var conn = await GetConnectionAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT * FROM trust.usp_get_trust_history(@p_user_id, @p_limit, @p_offset)", conn);
        cmd.Parameters.AddWithValue("p_user_id", userId);
        cmd.Parameters.AddWithValue("p_limit", limit);
        cmd.Parameters.AddWithValue("p_offset", skip);

        var events = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            events.Add(new
            {
                id = reader.GetGuid(0).ToString(),
                eventType = reader.GetString(1),
                delta = reader.GetInt16(2),
                reason = reader.IsDBNull(3) ? null : reader.GetString(3),
                refSource = reader.IsDBNull(4) ? null : reader.GetString(4),
                createdAt = reader.GetDateTime(5).ToString("o")
            });
        }

        return Ok(ApiResponse<object>.Ok(new { skip, limit, events }));
    }

    // ============================================================
    //  POST /api/trust/report
    //  File a report against another user
    // ============================================================
    [HttpPost("report")]
    public async Task<IActionResult> FileReport([FromBody] FileReportRequest req)
    {
        var reporterId = JwtService.GetUserId(User);

        if (!Guid.TryParse(req.ReportedUserId, out var reportedId))
            return BadRequest(ApiResponse.Fail("Invalid user ID"));

        if (reporterId == reportedId)
            return BadRequest(ApiResponse.Fail("You cannot report yourself"));

        var validReasons = new[] { "harassment", "spam", "nsfw", "underage", "hate_speech", "other" };
        if (!validReasons.Contains(req.Reason.ToLower()))
            return BadRequest(ApiResponse.Fail($"Invalid reason. Allowed: {string.Join(", ", validReasons)}"));

        var (reportId, error) = await _postgres.FileReportAsync(
            reporterId,
            reportedId,
            req.Reason.ToLower(),
            req.Description
        );

        if (error != null)
        {
            var message = error switch
            {
                "CANNOT_SELF_REPORT" => "You cannot report yourself",
                "USER_NOT_FOUND" => "User not found",
                _ => "Report submission failed"
            };
            return BadRequest(ApiResponse.Fail(message));
        }

        _logger.LogInformation("Report filed by {ReporterId} against {ReportedId} — Reason: {Reason}",
            reporterId, reportedId, req.Reason);

        return Ok(ApiResponse<object>.Ok(new
        {
            reportId = reportId.ToString(),
            message = "Report submitted successfully. Our team will review it shortly."
        }));
    }

    // ============================================================
    //  GET /api/trust/score/{userId}
    //  View another user's trust score (public info)
    // ============================================================
    [HttpGet("score/{userId}")]
    public async Task<IActionResult> GetUserScore(string userId)
    {
        if (!Guid.TryParse(userId, out var targetUserId))
            return BadRequest(ApiResponse.Fail("Invalid user ID"));

        var conn = await GetConnectionAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT * FROM trust.usp_get_trust_score(@p_user_id)", conn);
        cmd.Parameters.AddWithValue("p_user_id", targetUserId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return NotFound(ApiResponse.Fail("User not found"));

        return Ok(ApiResponse<object>.Ok(new
        {
            userId,
            score = reader.GetInt16(0),
            band = reader.GetString(1),
            bandLabel = reader.GetString(2)
        }));
    }

    // ── Private helper ────────────────────────────────────────
    private async Task<Npgsql.NpgsqlConnection> GetConnectionAsync()
    {
        var conn = (Npgsql.NpgsqlConnection)HttpContext.RequestServices
            .GetRequiredService<Infrastructure.Persistence.PostgreSQL.ChatVerseDbContext>()
            .Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();
        return conn;
    }
}

// ── Request DTOs ──────────────────────────────────────────────
public record FileReportRequest(
    [System.ComponentModel.DataAnnotations.Required] string ReportedUserId,
    [System.ComponentModel.DataAnnotations.Required] string Reason,
    string? Description
);