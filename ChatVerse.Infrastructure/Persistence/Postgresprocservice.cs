using ChatVerse.Domain.Entities;
using ChatVerse.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatVerse.Infrastructure.Persistence.PostgreSQL;

/// <summary>
/// All PostgreSQL stored procedure calls live here.
/// Controllers/Services never write raw SQL — they call these methods.
/// </summary>
public class PostgresProcService
{
    private readonly ChatVerseDbContext _db;

    public PostgresProcService(ChatVerseDbContext db)
    {
        _db = db;
    }

    // ============================================================
    //  AUTH PROCS
    // ============================================================

    /// <summary>
    /// Creates a guest user with auto-generated username.
    /// Returns (userId, username) or throws if username taken.
    /// </summary>
    public async Task<(Guid UserId, string Username)> CreateGuestUserAsync(string username)
    {
        var pUsername = new NpgsqlParameter("p_username", username);
        var pUserId = new NpgsqlParameter("p_user_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Direction = System.Data.ParameterDirection.Output };
        var pUserOut = new NpgsqlParameter("p_username_out", NpgsqlTypes.NpgsqlDbType.Varchar) { Direction = System.Data.ParameterDirection.Output };
        var pCreatedAt = new NpgsqlParameter("p_created_at", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Direction = System.Data.ParameterDirection.Output };

        await _db.Database.ExecuteSqlRawAsync(
            "CALL user_auth.usp_create_guest_user(@p_username, @p_user_id, @p_username_out, @p_created_at)",
            pUsername, pUserId, pUserOut, pCreatedAt);

        return ((Guid)pUserId.Value!, (string)pUserOut.Value!);
    }

    /// <summary>
    /// Registers a new full user account.
    /// Returns (userId, error). error is null on success.
    /// </summary>
    public async Task<(Guid? UserId, string? Error)> RegisterUserAsync(
        string username, string email, string passwordHash)
    {
        var pUsername = new NpgsqlParameter("p_username", username);
        var pEmail = new NpgsqlParameter("p_email", email);
        var pHash = new NpgsqlParameter("p_password_hash", passwordHash);
        var pUserId = new NpgsqlParameter("p_user_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Direction = System.Data.ParameterDirection.Output };
        var pError = new NpgsqlParameter("p_error", NpgsqlTypes.NpgsqlDbType.Varchar) { Direction = System.Data.ParameterDirection.Output };

        await _db.Database.ExecuteSqlRawAsync(
            "CALL user_auth.usp_register_user(@p_username, @p_email, @p_password_hash, @p_user_id, @p_error)",
            pUsername, pEmail, pHash, pUserId, pError);

        var error = pError.Value == DBNull.Value ? null : (string?)pError.Value;
        var userId = pUserId.Value == DBNull.Value ? (Guid?)null : (Guid)pUserId.Value;

        return (userId, error);
    }

    /// <summary>
    /// Creates/replaces OTP for email + purpose combo.
    /// </summary>
    public async Task<Guid> UpsertOtpAsync(
        string email, string code, OtpPurpose purpose, DateTime expiresAt)
    {
        var pEmail = new NpgsqlParameter("p_email", email);
        var pCode = new NpgsqlParameter("p_code", code);
        var pPurpose = new NpgsqlParameter("p_purpose", purpose.ToString().ToSnakeCase())
        { DataTypeName = "user_auth.otp_purpose" };
        var pExpiresAt = new NpgsqlParameter("p_expires_at", expiresAt);
        var pOtpId = new NpgsqlParameter("p_otp_id", NpgsqlTypes.NpgsqlDbType.Uuid)
        { Direction = System.Data.ParameterDirection.Output };

        await _db.Database.ExecuteSqlRawAsync(
            "CALL user_auth.usp_upsert_otp(@p_email, @p_code, @p_purpose, @p_expires_at, @p_otp_id)",
            pEmail, pCode, pPurpose, pExpiresAt, pOtpId);

        return (Guid)pOtpId.Value!;
    }

    /// <summary>
    /// Verifies OTP code. Returns (isValid, userId, message).
    /// </summary>
    public async Task<(bool IsValid, Guid? UserId, string Message)> VerifyOtpAsync(
        string email, string code, OtpPurpose purpose)
    {
        var pEmail = new NpgsqlParameter("p_email", email);
        var pCode = new NpgsqlParameter("p_code", code);
        var pPurpose = new NpgsqlParameter("p_purpose", purpose.ToString().ToSnakeCase())
        { DataTypeName = "user_auth.otp_purpose" };
        var pIsValid = new NpgsqlParameter("p_is_valid", NpgsqlTypes.NpgsqlDbType.Boolean) { Direction = System.Data.ParameterDirection.Output };
        var pUserId = new NpgsqlParameter("p_user_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Direction = System.Data.ParameterDirection.Output };
        var pMessage = new NpgsqlParameter("p_message", NpgsqlTypes.NpgsqlDbType.Varchar) { Direction = System.Data.ParameterDirection.Output };

        await _db.Database.ExecuteSqlRawAsync(
            "CALL user_auth.usp_verify_otp(@p_email, @p_code, @p_purpose, @p_is_valid, @p_user_id, @p_message)",
            pEmail, pCode, pPurpose, pIsValid, pUserId, pMessage);

        return (
            (bool)pIsValid.Value!,
            pUserId.Value == DBNull.Value ? null : (Guid?)pUserId.Value,
            (string)pMessage.Value!
        );
    }

    /// <summary>
    /// Validates login credentials. Returns user data for JWT minting.
    /// </summary>
    public async Task<(Guid? UserId, string? Username, short TrustScore,
        bool IsEmailVerified, bool AgeVerified, string? Error)> LoginUserAsync(
        string email, string passwordHash)
    {
        var pEmail = new NpgsqlParameter("p_email", email);
        var pHash = new NpgsqlParameter("p_password_hash", passwordHash);
        var pUserId = new NpgsqlParameter("p_user_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Direction = System.Data.ParameterDirection.Output };
        var pUsername = new NpgsqlParameter("p_username", NpgsqlTypes.NpgsqlDbType.Varchar) { Direction = System.Data.ParameterDirection.Output };
        var pTrust = new NpgsqlParameter("p_trust_score", NpgsqlTypes.NpgsqlDbType.Smallint) { Direction = System.Data.ParameterDirection.Output };
        var pVerified = new NpgsqlParameter("p_is_email_verified", NpgsqlTypes.NpgsqlDbType.Boolean) { Direction = System.Data.ParameterDirection.Output };
        var pAgeVer = new NpgsqlParameter("p_age_verified", NpgsqlTypes.NpgsqlDbType.Boolean) { Direction = System.Data.ParameterDirection.Output };
        var pStatus = new NpgsqlParameter("p_status", NpgsqlTypes.NpgsqlDbType.Varchar) { Direction = System.Data.ParameterDirection.Output };
        var pError = new NpgsqlParameter("p_error", NpgsqlTypes.NpgsqlDbType.Varchar) { Direction = System.Data.ParameterDirection.Output };

        await _db.Database.ExecuteSqlRawAsync(
            @"CALL user_auth.usp_login_user(
                @p_email, @p_password_hash,
                @p_user_id, @p_username, @p_trust_score,
                @p_is_email_verified, @p_age_verified, @p_status, @p_error)",
            pEmail, pHash, pUserId, pUsername, pTrust, pVerified, pAgeVer, pStatus, pError);

        var error = pError.Value == DBNull.Value ? null : (string?)pError.Value;
        if (error != null)
            return (null, null, 0, false, false, error);

        return (
            (Guid)pUserId.Value!,
            (string)pUsername.Value!,
            (short)pTrust.Value!,
            (bool)pVerified.Value!,
            (bool)pAgeVer.Value!,
            null
        );
    }

    /// <summary>
    /// Upgrades guest account to full registered user.
    /// </summary>
    public async Task<(bool Success, string? Error)> UpgradeGuestToUserAsync(
        Guid guestId, string email, string passwordHash)
    {
        var pGuestId = new NpgsqlParameter("p_guest_id", guestId);
        var pEmail = new NpgsqlParameter("p_email", email);
        var pHash = new NpgsqlParameter("p_password_hash", passwordHash);
        var pSuccess = new NpgsqlParameter("p_success", NpgsqlTypes.NpgsqlDbType.Boolean) { Direction = System.Data.ParameterDirection.Output };
        var pError = new NpgsqlParameter("p_error", NpgsqlTypes.NpgsqlDbType.Varchar) { Direction = System.Data.ParameterDirection.Output };

        await _db.Database.ExecuteSqlRawAsync(
            "CALL user_auth.usp_upgrade_guest_to_user(@p_guest_id, @p_email, @p_password_hash, @p_success, @p_error)",
            pGuestId, pEmail, pHash, pSuccess, pError);

        return (
            (bool)pSuccess.Value!,
            pError.Value == DBNull.Value ? null : (string?)pError.Value
        );
    }

    /// <summary>
    /// Marks user active for today. Auto-clears tenure gate if 7 days reached.
    /// Returns true if tenure gate was just cleared.
    /// </summary>
    public async Task<bool> MarkUserActiveDayAsync(Guid userId)
    {
        var pUserId = new NpgsqlParameter("p_user_id", userId);
        var pCleared = new NpgsqlParameter("p_tenure_just_cleared", NpgsqlTypes.NpgsqlDbType.Boolean)
        { Direction = System.Data.ParameterDirection.Output };

        await _db.Database.ExecuteSqlRawAsync(
            "CALL user_auth.usp_mark_user_active_day(@p_user_id, @p_tenure_just_cleared)",
            pUserId, pCleared);

        return (bool)pCleared.Value!;
    }

    // ============================================================
    //  IAM PROCS
    // ============================================================

    /// <summary>
    /// Records user's DOB self-declaration.
    /// Returns (success, error). error codes: UNDERAGE_BLOCKED, ALREADY_DECLARED
    /// </summary>
    public async Task<(bool Success, string? Error)> SubmitAgeDeclarationAsync(
        Guid userId, DateOnly dob, string? ipAddress, string? userAgent)
    {
        var pUserId = new NpgsqlParameter("p_user_id", userId);
        var pDob = new NpgsqlParameter("p_dob", dob.ToDateTime(TimeOnly.MinValue));
        var pIp = new NpgsqlParameter("p_ip_address", (object?)ipAddress ?? DBNull.Value);
        var pAgent = new NpgsqlParameter("p_user_agent", (object?)userAgent ?? DBNull.Value);
        var pSuccess = new NpgsqlParameter("p_success", NpgsqlTypes.NpgsqlDbType.Boolean) { Direction = System.Data.ParameterDirection.Output };
        var pError = new NpgsqlParameter("p_error", NpgsqlTypes.NpgsqlDbType.Varchar) { Direction = System.Data.ParameterDirection.Output };

        await _db.Database.ExecuteSqlRawAsync(
            "CALL iam.usp_submit_age_declaration(@p_user_id, @p_dob, @p_ip_address, @p_user_agent, @p_success, @p_error)",
            pUserId, pDob, pIp, pAgent, pSuccess, pError);

        return (
            (bool)pSuccess.Value!,
            pError.Value == DBNull.Value ? null : (string?)pError.Value
        );
    }

    /// <summary>
    /// Saves AI maturity quiz result. Max 3 attempts enforced in proc.
    /// </summary>
    public async Task<(bool Passed, short AttemptsUsed, string? Error)> SaveAiMaturityResultAsync(
        Guid userId, string? sessionRef, string questionsJson, short finalScore)
    {
        var pUserId = new NpgsqlParameter("p_user_id", userId);
        var pSessionRef = new NpgsqlParameter("p_session_ref", (object?)sessionRef ?? DBNull.Value);
        var pQuestions = new NpgsqlParameter("p_questions_asked", questionsJson)
        { DataTypeName = "jsonb" };
        var pScore = new NpgsqlParameter("p_final_score", finalScore);
        var pPassed = new NpgsqlParameter("p_passed", NpgsqlTypes.NpgsqlDbType.Boolean) { Direction = System.Data.ParameterDirection.Output };
        var pAttempts = new NpgsqlParameter("p_attempts_used", NpgsqlTypes.NpgsqlDbType.Smallint) { Direction = System.Data.ParameterDirection.Output };
        var pError = new NpgsqlParameter("p_error", NpgsqlTypes.NpgsqlDbType.Varchar) { Direction = System.Data.ParameterDirection.Output };

        await _db.Database.ExecuteSqlRawAsync(
            @"CALL iam.usp_save_ai_maturity_result(
                @p_user_id, @p_session_ref, @p_questions_asked,
                @p_final_score, @p_passed, @p_attempts_used, @p_error)",
            pUserId, pSessionRef, pQuestions, pScore, pPassed, pAttempts, pError);

        return (
            (bool)pPassed.Value!,
            (short)pAttempts.Value!,
            pError.Value == DBNull.Value ? null : (string?)pError.Value
        );
    }

    /// <summary>
    /// Submits ID document for age verification review.
    /// </summary>
    public async Task<(Guid? DocId, string? Error)> SubmitDocumentVerificationAsync(
        Guid userId, string docType, string cloudinaryPublicId, string cloudinaryUrl)
    {
        var pUserId = new NpgsqlParameter("p_user_id", userId);
        var pDocType = new NpgsqlParameter("p_doc_type", docType);
        var pPublicId = new NpgsqlParameter("p_cloudinary_public_id", cloudinaryPublicId);
        var pUrl = new NpgsqlParameter("p_cloudinary_url", cloudinaryUrl);
        var pDocId = new NpgsqlParameter("p_doc_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Direction = System.Data.ParameterDirection.Output };
        var pError = new NpgsqlParameter("p_error", NpgsqlTypes.NpgsqlDbType.Varchar) { Direction = System.Data.ParameterDirection.Output };

        await _db.Database.ExecuteSqlRawAsync(
            @"CALL iam.usp_submit_document_verification(
                @p_user_id, @p_doc_type, @p_cloudinary_public_id,
                @p_cloudinary_url, @p_doc_id, @p_error)",
            pUserId, pDocType, pPublicId, pUrl, pDocId, pError);

        return (
            pDocId.Value == DBNull.Value ? null : (Guid?)pDocId.Value,
            pError.Value == DBNull.Value ? null : (string?)pError.Value
        );
    }
}

// ── Extension helper ─────────────────────────────────────────
internal static class StringExtensions
{
    /// <summary>
    /// Converts PascalCase enum to snake_case for PostgreSQL ENUM values.
    /// e.g. EmailVerification → email_verification
    /// </summary>
    public static string ToSnakeCase(this string value)
    {
        return string.Concat(value.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + c : c.ToString()
        )).ToLower();
    }
}