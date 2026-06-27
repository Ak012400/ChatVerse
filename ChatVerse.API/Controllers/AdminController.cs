using ChatVerse.API.Extensions;
using ChatVerse.API.Services;
using ChatVerse.Domain.Constants;
using ChatVerse.Domain.Enums;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Data;

namespace ChatVerse.API.Controllers;

/// <summary>
/// Admin dashboard endpoints.
///
/// Authorisation is gated by an allow-list of admin user-ids configured
/// in appsettings ("Admin:UserIds": ["uuid1", "uuid2"]). This is the
/// cheapest path to a working admin panel before a full role system —
/// upgrade to JWT-claim-based roles when the team grows.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize]
public class AdminController : ControllerBase
{
    private readonly ChatVerseDbContext _db;
    private readonly PostgresProcService _postgres;
    private readonly IConfiguration _config;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        ChatVerseDbContext db,
        PostgresProcService postgres,
        IConfiguration config,
        ILogger<AdminController> logger)
    {
        _db = db;
        _postgres = postgres;
        _config = config;
        _logger = logger;
    }

    /* ─── Admin allow-list check ─── */
    private bool IsAdmin(out Guid adminId)
    {
        adminId = JwtService.GetUserId(User);
        var allowList = _config.GetSection("Admin:UserIds").Get<string[]>() ?? Array.Empty<string>();
        return allowList.Contains(adminId.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    private async Task<NpgsqlConnection> GetConnAsync()
    {
        var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();
        return conn;
    }

    // ============================================================
    //  GET /api/admin/whoami
    //  Lightweight "am I admin?" probe so the frontend can decide
    //  whether to show the admin nav entry at all.
    // ============================================================
    [HttpGet("whoami")]
    public IActionResult WhoAmI()
    {
        return Ok(ApiResponse<object>.Ok(new { isAdmin = IsAdmin(out _) }));
    }

    // ============================================================
    //  GET /api/admin/reports?status=pending&limit=50
    //  Paginated list of user reports for review.
    // ============================================================
    [HttpGet("reports")]
    public async Task<IActionResult> GetReports(
        [FromQuery] string status = "pending",
        [FromQuery] int limit = 50)
    {
        if (!IsAdmin(out _)) return Forbid();
        limit = Math.Clamp(limit, 1, 200);

        var conn = await GetConnAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT r.id, r.reporter_id, r.reported_id, r.reason, r.description,
                     r.status, r.created_at,
                     rep.username AS reporter_name,
                     tgt.username AS reported_name
              FROM trust.user_reports r
              JOIN user_auth.users rep ON rep.id = r.reporter_id
              JOIN user_auth.users tgt ON tgt.id = r.reported_id
              WHERE r.status = @p_status::trust.report_status
              ORDER BY r.created_at DESC
              LIMIT @p_limit", conn);
        cmd.Parameters.AddWithValue("p_status", status);
        cmd.Parameters.AddWithValue("p_limit", limit);

        var results = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new
            {
                reportId    = reader.GetGuid(0).ToString(),
                reporterId  = reader.GetGuid(1).ToString(),
                reportedId  = reader.GetGuid(2).ToString(),
                reason      = reader.GetString(3),
                description = reader.IsDBNull(4) ? null : reader.GetString(4),
                status      = reader.GetString(5),
                createdAt   = reader.GetDateTime(6).ToString("o"),
                reporterName = reader.GetString(7),
                reportedName = reader.GetString(8)
            });
        }

        return Ok(ApiResponse<object>.Ok(new { status, count = results.Count, results }));
    }

    // ============================================================
    //  POST /api/admin/reports/{reportId}/review
    //  Body: { outcome: "valid" | "invalid" | "dismissed", note?: string }
    // ============================================================
    [HttpPost("reports/{reportId}/review")]
    public async Task<IActionResult> ReviewReport(
        string reportId,
        [FromBody] ReviewReportRequest req)
    {
        if (!IsAdmin(out var adminId)) return Forbid();
        if (!Guid.TryParse(reportId, out var reportGuid))
            return BadRequest(ApiResponse.Fail("Invalid report id"));

        var validOutcomes = new[] { "valid", "invalid", "dismissed" };
        if (!validOutcomes.Contains(req.Outcome?.ToLower()))
            return BadRequest(ApiResponse.Fail("Outcome must be: valid, invalid, or dismissed"));

        var (success, error) = await _postgres.ReviewReportAsync(
            reportGuid, adminId, req.Outcome!.ToLower(), req.Note);

        if (!success)
            return BadRequest(ApiResponse.Fail(error ?? "Review failed"));

        _logger.LogInformation(
            "Report {ReportId} reviewed by {AdminId} — outcome: {Outcome}",
            reportId, adminId, req.Outcome);

        return Ok(ApiResponse.Ok("Report reviewed"));
    }

    // ============================================================
    //  GET /api/admin/documents?status=pending&limit=50
    //  Document verifications awaiting review.
    // ============================================================
    [HttpGet("documents")]
    public async Task<IActionResult> GetDocuments(
        [FromQuery] string status = "pending",
        [FromQuery] int limit = 50)
    {
        if (!IsAdmin(out _)) return Forbid();
        limit = Math.Clamp(limit, 1, 200);

        var conn = await GetConnAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT d.id, d.user_id, d.doc_type, d.cloudinary_public_id,
                     d.cloudinary_url, d.status, d.submitted_at,
                     u.username
              FROM iam.document_verifications d
              JOIN user_auth.users u ON u.id = d.user_id
              WHERE d.status = @p_status::iam.doc_verify_status
              ORDER BY d.submitted_at ASC
              LIMIT @p_limit", conn);
        cmd.Parameters.AddWithValue("p_status", status);
        cmd.Parameters.AddWithValue("p_limit", limit);

        var results = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new
            {
                docId              = reader.GetGuid(0).ToString(),
                userId             = reader.GetGuid(1).ToString(),
                docType            = reader.GetString(2),
                cloudinaryPublicId = reader.GetString(3),
                cloudinaryUrl      = reader.GetString(4),
                status             = reader.GetString(5),
                submittedAt        = reader.GetDateTime(6).ToString("o"),
                username           = reader.GetString(7)
            });
        }

        return Ok(ApiResponse<object>.Ok(new { status, count = results.Count, results }));
    }

    // ============================================================
    //  POST /api/admin/documents/{docId}/review
    //  Body: { outcome: "approve" | "reject", rejectReason?: string }
    // ============================================================
    [HttpPost("documents/{docId}/review")]
    public async Task<IActionResult> ReviewDocument(
        string docId,
        [FromBody] ReviewDocRequest req)
    {
        if (!IsAdmin(out var adminId)) return Forbid();
        if (!Guid.TryParse(docId, out var docGuid))
            return BadRequest(ApiResponse.Fail("Invalid doc id"));

        var outcome = req.Outcome?.ToLower();
        if (outcome != "approve" && outcome != "reject")
            return BadRequest(ApiResponse.Fail("Outcome must be: approve or reject"));

        var (success, error) = await _postgres.ReviewDocumentAsync(
            docGuid, adminId, outcome, req.RejectReason);

        if (!success)
            return BadRequest(ApiResponse.Fail(error ?? "Review failed"));

        _logger.LogInformation(
            "Doc {DocId} reviewed by {AdminId} — outcome: {Outcome}",
            docId, adminId, outcome);

        return Ok(ApiResponse.Ok($"Document {outcome}d"));
    }

    // ============================================================
    //  GET /api/admin/stats
    //  Quick numbers — pending reports, pending docs, total users.
    //  Cheap glanceable view for the dashboard header.
    // ============================================================
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        if (!IsAdmin(out _)) return Forbid();

        var conn = await GetConnAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT
                (SELECT COUNT(*) FROM trust.user_reports WHERE status = 'pending'),
                (SELECT COUNT(*) FROM iam.document_verifications WHERE status = 'pending'),
                (SELECT COUNT(*) FROM user_auth.users WHERE is_guest = false),
                (SELECT COUNT(*) FROM user_auth.users WHERE age_verified = true)",
            conn);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();

        return Ok(ApiResponse<object>.Ok(new
        {
            pendingReports   = reader.GetInt64(0),
            pendingDocuments = reader.GetInt64(1),
            totalUsers       = reader.GetInt64(2),
            ageVerifiedUsers = reader.GetInt64(3),
        }));
    }
}

public record ReviewReportRequest(string Outcome, string? Note);
public record ReviewDocRequest(string Outcome, string? RejectReason);

// ============================================================
//  Admin test triggers — exposed via a SEPARATE controller class
//  but same /api/admin route, gated by the same admin allow-list.
//
//  Why separate class: keeps the historical AdminController focused on
//  user / report / doc review while the test-trigger endpoints sit
//  alongside without bloating that file. They share the same allow-list
//  pattern but inject the background-service singletons directly.
//
//  Endpoints:
//    • POST /api/admin/test/force-pyaar-formation
//        Bypasses Saturday 8pm IST guard and runs FormShowAsync now.
//    • POST /api/admin/test/force-ghost-pairing
//        Bypasses Thursday 9pm IST guard and runs PairAsync now.
//
//  Both require the caller's userId to appear in Admin:UserIds.
// ============================================================
[ApiController]
[Route("api/admin/test")]
[Authorize]
public class AdminTestTriggersController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly PyaarLiveOrchestrator _pyaar;
    private readonly GhostDateService _ghost;
    private readonly ChatVerse.Infrastructure.Persistence.MongoDB.MongoService _mongo;
    private readonly ChatVerse.Infrastructure.Persistence.PostgreSQL.ChatVerseDbContext _db;
    private readonly ILogger<AdminTestTriggersController> _logger;

    public AdminTestTriggersController(
        IConfiguration config,
        PyaarLiveOrchestrator pyaar,
        GhostDateService ghost,
        ChatVerse.Infrastructure.Persistence.MongoDB.MongoService mongo,
        ChatVerse.Infrastructure.Persistence.PostgreSQL.ChatVerseDbContext db,
        ILogger<AdminTestTriggersController> logger)
    {
        _config = config;
        _pyaar = pyaar;
        _ghost = ghost;
        _mongo = mongo;
        _db = db;
        _logger = logger;
    }

    private bool IsAdmin()
    {
        var meId = JwtService.GetUserId(User);
        var allowList = _config.GetSection("Admin:UserIds").Get<string[]>() ?? Array.Empty<string>();
        return allowList.Contains(meId.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    [HttpPost("force-pyaar-formation")]
    public async Task<IActionResult> ForcePyaarFormation(CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();
        try
        {
            await _pyaar.ForceFormationNowAsync(ct);
            return Ok(new { ok = true, message = "PYAAR LIVE formation triggered." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PYAAR force-formation failed");
            return StatusCode(500, new { ok = false, error = ex.Message });
        }
    }

    [HttpPost("force-ghost-pairing")]
    public async Task<IActionResult> ForceGhostPairing(CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();
        try
        {
            await _ghost.ForcePairingNowAsync(ct);
            return Ok(new { ok = true, message = "Ghost Date pairing triggered." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ghost force-pairing failed");
            return StatusCode(500, new { ok = false, error = ex.Message });
        }
    }

    /// <summary>Seed the PYAAR LIVE / Ghost Date pool with N existing
    /// users (the most recently active non-guest accounts other than the
    /// admin themselves). Lets a single admin test the full pairing flow
    /// without needing N other browsers signed in. Idempotent per-user:
    /// users already pending in today's pool are skipped.</summary>
    [HttpPost("seed-pool")]
    public async Task<IActionResult> SeedPool([FromQuery] string kind, [FromQuery] int count, CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();
        if (kind != "pyaar" && kind != "ghost")
            return BadRequest(new { ok = false, error = "kind must be 'pyaar' or 'ghost'" });
        if (count is < 1 or > 20)
            return BadRequest(new { ok = false, error = "count must be 1..20" });

        var meId = JwtService.GetUserId(User);

        // Direct SQL — ChatVerseDbContext has no DbSet<T> mappings so we
        // can't use LINQ. Pull recent active non-guest users (excluding me)
        // ordered by last activity. `status` is the user_auth.user_status
        // enum; raw text comparison to 'active' is what the existing
        // admin queries already do.
        var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        var users = new List<Guid>();
        await using (var cmd = new NpgsqlCommand(
            @"SELECT id FROM user_auth.users
              WHERE is_guest = false
                AND status = 'active'
                AND id <> @p_me
              ORDER BY last_active_date DESC NULLS LAST
              LIMIT @p_count", conn))
        {
            cmd.Parameters.AddWithValue("p_me", meId);
            cmd.Parameters.AddWithValue("p_count", count);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                users.Add(reader.GetGuid(0));
            }
        }
        if (users.Count == 0)
            return Ok(new { ok = false, message = "No eligible users to seed with — invite some real test accounts first." });

        var nowUtc = DateTime.UtcNow;
        var todayIst = nowUtc.AddHours(5).AddMinutes(30).Date.ToString("yyyy-MM-dd");
        var seeded = 0;
        foreach (var uid in users)
        {
            try
            {
                if (kind == "pyaar")
                    await _mongo.RegisterForPyaarLiveAsync(uid.ToString(), todayIst);
                else
                    await _mongo.RegisterForGhostDateAsync(uid.ToString(), todayIst);
                seeded++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Seed-pool insert failed for user {UserId}", uid);
            }
        }

        return Ok(new
        {
            ok = true,
            message = $"Seeded {seeded} user(s) into the {kind} pool for today. Now click 'Start' to trigger formation.",
            seeded,
        });
    }
}
