using ChatVerse.API.Extensions;
using Microsoft.EntityFrameworkCore;
using ChatVerse.Infrastructure.ExternalServices.Cloudinary;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using ChatVerse.Infrastructure.Services.UserState;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ChatVerse.API.Controllers;

[ApiController]
[Route("api/age")]
[Authorize]
public class AgeController : ControllerBase
{
    private readonly PostgresProcService _postgres;
    private readonly CloudinaryService _cloudinary;
    private readonly UserStateService _userState;
    private readonly ILogger<AgeController> _logger;

    public AgeController(
        PostgresProcService postgres,
        CloudinaryService cloudinary,
        UserStateService userState,
        ILogger<AgeController> logger)
    {
        _postgres = postgres;
        _cloudinary = cloudinary;
        _userState = userState;
        _logger = logger;
    }

    // ============================================================
    //  GET /api/age/status
    //  Returns full verification gate status for current user
    // ============================================================
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var userId = JwtService.GetUserId(User);

        var conn = await GetConnectionAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT * FROM iam.usp_get_age_verification_status(@p_user_id)", conn);
        cmd.Parameters.AddWithValue("p_user_id", userId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return NotFound(ApiResponse.Fail("User not found"));

        return Ok(ApiResponse<object>.Ok(new
        {
            gates = new
            {
                gate1_emailVerified = reader.IsDBNull(0) ? false : reader.GetBoolean(0),
                gate2_selfDeclared = reader.IsDBNull(1) ? false : reader.GetBoolean(1),
                gate2_dob = reader.IsDBNull(2) ? null : reader.GetDateTime(2).ToString("yyyy-MM-dd"),
                gate3_aiPassed = reader.IsDBNull(3) ? false : reader.GetBoolean(3),
                gate3_aiScore = reader.IsDBNull(4) ? (short?)null : reader.GetInt16(4),
                gate3_attemptsUsed = reader.IsDBNull(5) ? (short?)null : reader.GetInt16(5),
                gate4a_activeDays = reader.IsDBNull(6) ? (short?)null : reader.GetInt16(6),
                gate4a_tenureCleared = reader.IsDBNull(7) ? false : reader.GetBoolean(7),
                gate4b_docSubmitted = reader.IsDBNull(8) ? false : reader.GetBoolean(8),
                gate4b_docStatus = reader.IsDBNull(9) ? null : reader.GetString(9),
            },
            ageVerified = reader.IsDBNull(10) ? false : reader.GetBoolean(10),
            ageVerifyMethod = reader.IsDBNull(11) ? null : reader.GetString(11),
            ageVerifiedAt = reader.IsDBNull(12) ? null : reader.GetDateTime(12).ToString("o")
        }));
    }

    // ============================================================
    //  POST /api/age/declare
    //  Gate 2 — User submits DOB + 18+ self declaration
    // ============================================================
    [HttpPost("declare")]
    public async Task<IActionResult> DeclareAge([FromBody] AgeDeclareRequest req)
    {
        var userId = JwtService.GetUserId(User);
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();

        if (!DateOnly.TryParse(req.Dob, out var dob))
            return BadRequest(ApiResponse.Fail("Invalid date format. Use YYYY-MM-DD"));

        if (dob > DateOnly.FromDateTime(DateTime.UtcNow))
            return BadRequest(ApiResponse.Fail("Date of birth cannot be in the future"));

        var (success, error) = await _postgres.SubmitAgeDeclarationAsync(
            userId, dob, ipAddress, userAgent);

        if (!success)
        {
            var message = error switch
            {
                "UNDERAGE_BLOCKED" => "You must be at least 13 years old to use ChatVerse",
                "ALREADY_DECLARED" => "Age declaration already submitted",
                _ => "Age declaration failed"
            };
            return BadRequest(ApiResponse.Fail(message));
        }

        return Ok(ApiResponse.Ok("Age declaration submitted. Proceed to AI assessment."));
    }

    // ============================================================
    //  POST /api/age/ai-quiz/submit
    //  Gate 3 — Submit AI maturity quiz result
    //  Frontend sends OpenAI quiz session result to be saved
    // ============================================================
    [HttpPost("ai-quiz/submit")]
    public async Task<IActionResult> SubmitAiQuizResult([FromBody] AiQuizSubmitRequest req)
    {
        var userId = JwtService.GetUserId(User);

        if (req.FinalScore < 0 || req.FinalScore > 100)
            return BadRequest(ApiResponse.Fail("Invalid score range"));

        var questionsJson = JsonSerializer.Serialize(req.Questions ?? new List<object>());

        var (passed, attemptsUsed, error) = await _postgres.SaveAiMaturityResultAsync(
            userId,
            req.SessionRef,
            questionsJson,
            (short)req.FinalScore
        );

        if (error == "MAX_ATTEMPTS_REACHED")
            return StatusCode(429, ApiResponse.Fail("Maximum AI quiz attempts reached (3). Please use document verification instead."));

        // If this attempt cleared the age-verification gate, drop the
        // cached value so the next hub check picks up the new state
        // immediately instead of waiting for the TTL.
        if (passed) await _userState.InvalidateAsync(userId);

        return Ok(ApiResponse<object>.Ok(new
        {
            passed,
            attemptsUsed,
            attemptsRemaining = 3 - attemptsUsed,
            message = passed
                ? "AI assessment passed! Proceed to tenure or document verification."
                : $"Score: {req.FinalScore}/100. Minimum required: 70. Attempts remaining: {3 - attemptsUsed}"
        }));
    }

    // ============================================================
    //  POST /api/age/doc-upload
    //  Gate 4B — Submit document for verification
    //  Cloudinary upload done by frontend, URL sent here
    // ============================================================
    [HttpPost("doc-upload")]
    public async Task<IActionResult> SubmitDocument([FromBody] DocUploadRequest req)
    {
        var userId = JwtService.GetUserId(User);

        var validDocTypes = new[] { "aadhaar", "passport", "driving_license" };
        if (!validDocTypes.Contains(req.DocType.ToLower()))
            return BadRequest(ApiResponse.Fail("Invalid document type. Allowed: aadhaar, passport, driving_license"));

        var (docId, error) = await _postgres.SubmitDocumentVerificationAsync(
            userId,
            req.DocType.ToLower(),
            req.CloudinaryPublicId,
            req.CloudinaryUrl
        );

        if (error != null)
        {
            var message = error switch
            {
                "ALREADY_AGE_VERIFIED" => "You are already age verified",
                "DOC_REVIEW_PENDING" => "A document is already under review. Please wait.",
                _ => "Document submission failed"
            };
            return BadRequest(ApiResponse.Fail(message));
        }

        _logger.LogInformation("Document submitted for user {UserId} — DocId: {DocId}", userId, docId);

        return Ok(ApiResponse<object>.Ok(new
        {
            docId = docId.ToString(),
            status = "pending",
            message = "Document submitted for review. You will be notified within 24-48 hours."
        }));
    }

    // ============================================================
    //  POST /api/age/doc-upload-file  (multipart/form-data)
    //  All-in-one: takes the file directly, pushes it to Cloudinary
    //  (private/authenticated upload) and creates the verification
    //  record server-side. Frontend doesn't need any Cloudinary keys.
    // ============================================================
    [HttpPost("doc-upload-file")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB
    public async Task<IActionResult> SubmitDocumentFile(
        [FromForm] string docType,
        [FromForm] IFormFile file)
    {
        var userId = JwtService.GetUserId(User);

        var validDocTypes = new[] { "aadhaar", "passport", "driving_license" };
        if (string.IsNullOrWhiteSpace(docType) ||
            !validDocTypes.Contains(docType.ToLower()))
            return BadRequest(ApiResponse.Fail(
                "Invalid document type. Allowed: aadhaar, passport, driving_license"));

        if (file == null || file.Length == 0)
            return BadRequest(ApiResponse.Fail("No file uploaded"));

        if (file.Length > 10 * 1024 * 1024)
            return BadRequest(ApiResponse.Fail("File too large (max 10 MB)"));

        var allowedContent = new[] { "image/jpeg", "image/png", "image/webp", "application/pdf" };
        if (!allowedContent.Contains(file.ContentType))
            return BadRequest(ApiResponse.Fail("Unsupported file type. Use JPG, PNG, WEBP, or PDF."));

        // Push to Cloudinary as a private/authenticated asset.
        await using var stream = file.OpenReadStream();
        var upload = await _cloudinary.UploadDocumentAsync(stream, file.FileName, userId.ToString());

        if (!upload.Success)
        {
            _logger.LogError("Doc upload to Cloudinary failed for user {UserId}: {Err}",
                userId, upload.Error);
            return StatusCode(500, ApiResponse.Fail("Could not upload your document. Try again."));
        }

        // Create the verification record.
        var (docId, error) = await _postgres.SubmitDocumentVerificationAsync(
            userId,
            docType.ToLower(),
            upload.PublicId,
            upload.SecureUrl);

        if (error != null)
        {
            var message = error switch
            {
                "ALREADY_AGE_VERIFIED" => "You are already age verified",
                "DOC_REVIEW_PENDING"   => "A document is already under review. Please wait.",
                _ => "Document submission failed"
            };
            return BadRequest(ApiResponse.Fail(message));
        }

        _logger.LogInformation(
            "Document uploaded + submitted — user {UserId}, docId {DocId}, publicId {PublicId}",
            userId, docId, upload.PublicId);

        return Ok(ApiResponse<object>.Ok(new
        {
            docId = docId.ToString(),
            status = "pending",
            bytes = upload.Bytes,
            message = "Document submitted. You will be notified within 24-48 hours."
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
public record AgeDeclareRequest(
    [System.ComponentModel.DataAnnotations.Required] string Dob
);

public record AiQuizSubmitRequest(
    [System.ComponentModel.DataAnnotations.Required] int FinalScore,
    string? SessionRef,
    List<object>? Questions
);

public record DocUploadRequest(
    [System.ComponentModel.DataAnnotations.Required] string DocType,
    [System.ComponentModel.DataAnnotations.Required] string CloudinaryPublicId,
    [System.ComponentModel.DataAnnotations.Required] string CloudinaryUrl
);