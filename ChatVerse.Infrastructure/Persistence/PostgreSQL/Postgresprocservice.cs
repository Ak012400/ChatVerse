using ChatVerse.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using System.Data;
using System.Net;

namespace ChatVerse.Infrastructure.Persistence.PostgreSQL;

public class PostgresProcService
{
    private readonly ChatVerseDbContext _db;
    public PostgresProcService(ChatVerseDbContext db) => _db = db;

    private async Task<NpgsqlConnection> GetOpenConnectionAsync()
    {
        var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();
        return conn;
    }

    private static NpgsqlCommand Proc(NpgsqlConnection conn, string procName)
    {
        var cmd = new NpgsqlCommand(procName, conn);
        cmd.CommandType = CommandType.StoredProcedure;
        return cmd;
    }

    // ============================================================
    //  AUTH PROCS
    // ============================================================

    public async Task<(Guid UserId, string Username)> CreateGuestUserAsync(string username)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = Proc(conn, "user_auth.usp_create_guest_user");
        cmd.Parameters.AddWithValue("p_username", username);
        cmd.Parameters.Add(new NpgsqlParameter("p_user_id", NpgsqlDbType.Uuid) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_username_out", NpgsqlDbType.Varchar) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_created_at", NpgsqlDbType.TimestampTz) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return ((Guid)cmd.Parameters["p_user_id"].Value!, (string)cmd.Parameters["p_username_out"].Value!);
    }

    public async Task<(Guid? UserId, string? Error)> RegisterUserAsync(
        string username, string email, string passwordHash)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = Proc(conn, "user_auth.usp_register_user");
        cmd.Parameters.AddWithValue("p_username", username);
        cmd.Parameters.AddWithValue("p_email", email);
        cmd.Parameters.AddWithValue("p_password_hash", passwordHash);
        cmd.Parameters.Add(new NpgsqlParameter("p_user_id", NpgsqlDbType.Uuid) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_error", NpgsqlDbType.Varchar) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return (
            cmd.Parameters["p_user_id"].Value == DBNull.Value ? null : (Guid?)cmd.Parameters["p_user_id"].Value,
            cmd.Parameters["p_error"].Value == DBNull.Value ? null : (string?)cmd.Parameters["p_error"].Value
        );
    }

    public async Task<Guid> UpsertOtpAsync(
        string email, string code, OtpPurpose purpose, DateTime expiresAt)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = Proc(conn, "user_auth.usp_upsert_otp");
        cmd.Parameters.AddWithValue("p_email", email);
        cmd.Parameters.AddWithValue("p_code", code);
        cmd.Parameters.Add(new NpgsqlParameter("p_purpose", NpgsqlDbType.Unknown)
        { Value = purpose.ToString().ToSnakeCase() });
        cmd.Parameters.AddWithValue("p_expires_at", expiresAt);
        cmd.Parameters.Add(new NpgsqlParameter("p_otp_id", NpgsqlDbType.Uuid) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return (Guid)cmd.Parameters["p_otp_id"].Value!;
    }

    public async Task<(bool IsValid, Guid? UserId, string Message)> VerifyOtpAsync(
        string email, string code, OtpPurpose purpose)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = Proc(conn, "user_auth.usp_verify_otp");
        cmd.Parameters.AddWithValue("p_email", email);
        cmd.Parameters.AddWithValue("p_code", code);
        cmd.Parameters.Add(new NpgsqlParameter("p_purpose", NpgsqlDbType.Unknown)
        { Value = purpose.ToString().ToSnakeCase() });
        cmd.Parameters.Add(new NpgsqlParameter("p_is_valid", NpgsqlDbType.Boolean) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_user_id", NpgsqlDbType.Uuid) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_message", NpgsqlDbType.Varchar) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return (
            (bool)cmd.Parameters["p_is_valid"].Value!,
            cmd.Parameters["p_user_id"].Value == DBNull.Value ? null : (Guid?)cmd.Parameters["p_user_id"].Value,
            (string)cmd.Parameters["p_message"].Value!
        );
    }

    public async Task<(Guid? UserId, string? Username, short TrustScore,
        bool IsEmailVerified, bool AgeVerified, string? Error)> LoginUserAsync(
        string email, string passwordHash)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = Proc(conn, "user_auth.usp_login_user");
        cmd.Parameters.AddWithValue("p_email", email);
        cmd.Parameters.AddWithValue("p_password_hash", passwordHash);
        cmd.Parameters.Add(new NpgsqlParameter("p_user_id", NpgsqlDbType.Uuid) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_username", NpgsqlDbType.Varchar) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_trust_score", NpgsqlDbType.Smallint) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_is_email_verified", NpgsqlDbType.Boolean) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_age_verified", NpgsqlDbType.Boolean) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_status", NpgsqlDbType.Unknown) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_error", NpgsqlDbType.Varchar) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        var error = cmd.Parameters["p_error"].Value == DBNull.Value ? null : (string?)cmd.Parameters["p_error"].Value;
        if (error != null) return (null, null, 0, false, false, error);
        return (
            (Guid)cmd.Parameters["p_user_id"].Value!,
            (string)cmd.Parameters["p_username"].Value!,
            (short)cmd.Parameters["p_trust_score"].Value!,
            (bool)cmd.Parameters["p_is_email_verified"].Value!,
            (bool)cmd.Parameters["p_age_verified"].Value!,
            null
        );
    }

    public async Task<(bool Success, string? Error)> UpgradeGuestToUserAsync(
        Guid guestId, string email, string passwordHash)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = Proc(conn, "user_auth.usp_upgrade_guest_to_user");
        cmd.Parameters.AddWithValue("p_guest_id", guestId);
        cmd.Parameters.AddWithValue("p_email", email);
        cmd.Parameters.AddWithValue("p_password_hash", passwordHash);
        cmd.Parameters.Add(new NpgsqlParameter("p_success", NpgsqlDbType.Boolean) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_error", NpgsqlDbType.Varchar) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return (
            (bool)cmd.Parameters["p_success"].Value!,
            cmd.Parameters["p_error"].Value == DBNull.Value ? null : (string?)cmd.Parameters["p_error"].Value
        );
    }

    public async Task<bool> MarkUserActiveDayAsync(Guid userId)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = Proc(conn, "user_auth.usp_mark_user_active_day");
        cmd.Parameters.AddWithValue("p_user_id", userId);
        cmd.Parameters.Add(new NpgsqlParameter("p_tenure_just_cleared", NpgsqlDbType.Boolean)
        { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return (bool)cmd.Parameters["p_tenure_just_cleared"].Value!;
    }

    // ============================================================
    //  AUTH HELPERS — raw SQL for BCrypt-based login flow
    //  The stored procs do exact-match hash comparison which BCrypt
    //  can't support (per-hash random salts). These helpers let the
    //  controller fetch the stored hash and verify in code instead.
    // ============================================================

    public record UserAuthRecord(
        Guid UserId,
        string Username,
        string? PasswordHash,
        short TrustScore,
        bool IsEmailVerified,
        bool AgeVerified,
        string Status);

    /// <summary>
    /// Fetch the auth record for a registered user by email.
    /// Returns null if no user found or the row belongs to a guest.
    /// </summary>
    public async Task<UserAuthRecord?> GetUserAuthByEmailAsync(string email)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT id, username, password_hash, trust_score, is_email_verified, age_verified, status::text
              FROM user_auth.users
              WHERE email = @p_email AND is_guest = false
              LIMIT 1", conn);
        cmd.Parameters.AddWithValue("p_email", email);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new UserAuthRecord(
            UserId: reader.GetGuid(0),
            Username: reader.GetString(1),
            PasswordHash: reader.IsDBNull(2) ? null : reader.GetString(2),
            TrustScore: reader.GetInt16(3),
            IsEmailVerified: reader.GetBoolean(4),
            AgeVerified: reader.GetBoolean(5),
            Status: reader.GetString(6)
        );
    }

    /// <summary>
    /// Fetch a minimal user view by id — used after OTP verify when we
    /// already know the userId and need the canonical username + flags
    /// to mint a correct JWT.
    /// </summary>
    public async Task<UserAuthRecord?> GetUserAuthByIdAsync(Guid userId)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT id, username, password_hash, trust_score, is_email_verified, age_verified, status::text
              FROM user_auth.users
              WHERE id = @p_user_id
              LIMIT 1", conn);
        cmd.Parameters.AddWithValue("p_user_id", userId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new UserAuthRecord(
            UserId: reader.GetGuid(0),
            Username: reader.GetString(1),
            PasswordHash: reader.IsDBNull(2) ? null : reader.GetString(2),
            TrustScore: reader.GetInt16(3),
            IsEmailVerified: reader.GetBoolean(4),
            AgeVerified: reader.GetBoolean(5),
            Status: reader.GetString(6)
        );
    }

    /// <summary>
    /// Update a user's stored password hash. Used to silently re-hash
    /// legacy SHA-256 passwords to BCrypt after a successful login.
    /// </summary>
    public async Task UpdatePasswordHashAsync(Guid userId, string newHash)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            @"UPDATE user_auth.users
              SET password_hash = @p_hash, updated_at = NOW()
              WHERE id = @p_user_id", conn);
        cmd.Parameters.AddWithValue("p_hash", newHash);
        cmd.Parameters.AddWithValue("p_user_id", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Fast pre-check for email + username availability. Returns a flag
    /// for each so we can fail fast in /register before bothering to
    /// send an OTP. The real uniqueness guarantee still comes from the
    /// proc's underlying constraint on insert.
    /// </summary>
    public async Task<(bool EmailTaken, bool UsernameTaken)> CheckRegistrationAvailabilityAsync(
        string email, string username)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT
                EXISTS(SELECT 1 FROM user_auth.users WHERE LOWER(email) = LOWER(@p_email)),
                EXISTS(SELECT 1 FROM user_auth.users WHERE LOWER(username) = LOWER(@p_username))",
            conn);
        cmd.Parameters.AddWithValue("p_email", email);
        cmd.Parameters.AddWithValue("p_username", username);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }

    /// <summary>
    /// Update the user's display username. Returns false on conflict.
    /// Username uniqueness is enforced by the DB UNIQUE constraint.
    /// </summary>
    public async Task<(bool Success, string? Error)> UpdateUsernameAsync(Guid userId, string newUsername)
    {
        if (string.IsNullOrWhiteSpace(newUsername) || newUsername.Length < 3 || newUsername.Length > 50)
            return (false, "INVALID_USERNAME");

        var conn = await GetOpenConnectionAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(
                @"UPDATE user_auth.users
                  SET username = @p_username, updated_at = NOW()
                  WHERE id = @p_user_id", conn);
            cmd.Parameters.AddWithValue("p_username", newUsername);
            cmd.Parameters.AddWithValue("p_user_id", userId);
            await cmd.ExecuteNonQueryAsync();
            return (true, null);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            // unique_violation
            return (false, "USERNAME_TAKEN");
        }
    }

    /// <summary>Update the avatar URL on the user record.</summary>
    public async Task UpdateAvatarUrlAsync(Guid userId, string? avatarUrl)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            @"UPDATE user_auth.users
              SET avatar_url = @p_avatar, updated_at = NOW()
              WHERE id = @p_user_id", conn);
        cmd.Parameters.AddWithValue("p_avatar", (object?)avatarUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("p_user_id", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Flip a user's is_email_verified flag to true. Used after the new
    /// "OTP-first" registration flow creates the row.
    /// </summary>
    public async Task MarkEmailVerifiedAsync(Guid userId)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            @"UPDATE user_auth.users
              SET is_email_verified = TRUE, updated_at = NOW()
              WHERE id = @p_user_id", conn);
        cmd.Parameters.AddWithValue("p_user_id", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    // ============================================================
    //  ADMIN REVIEW PROCS
    // ============================================================

    /// <summary>
    /// Admin marks a user report as valid / invalid / dismissed.
    /// Backing proc handles the trust delta side-effects.
    /// </summary>
    public async Task<(bool Success, string? Error)> ReviewReportAsync(
        Guid reportId, Guid reviewerId, string outcome, string? note)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = Proc(conn, "trust.usp_review_report");
        cmd.Parameters.AddWithValue("p_report_id", reportId);
        cmd.Parameters.AddWithValue("p_reviewer_id", reviewerId);
        cmd.Parameters.Add(new NpgsqlParameter("p_outcome", NpgsqlDbType.Varchar) { Value = outcome });
        cmd.Parameters.AddWithValue("p_review_note", (object?)note ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("p_success", NpgsqlDbType.Boolean) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_error", NpgsqlDbType.Varchar) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return (
            (bool)cmd.Parameters["p_success"].Value!,
            cmd.Parameters["p_error"].Value == DBNull.Value ? null : (string?)cmd.Parameters["p_error"].Value
        );
    }

    /// <summary>
    /// Admin approves/rejects a document verification. On approve, the
    /// proc flips users.age_verified=true.
    /// </summary>
    public async Task<(bool Success, string? Error)> ReviewDocumentAsync(
        Guid docId, Guid reviewerId, string outcome, string? rejectReason)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = Proc(conn, "iam.usp_review_document_verification");
        cmd.Parameters.AddWithValue("p_doc_id", docId);
        cmd.Parameters.AddWithValue("p_reviewer_id", reviewerId);
        cmd.Parameters.Add(new NpgsqlParameter("p_outcome", NpgsqlDbType.Varchar) { Value = outcome });
        cmd.Parameters.AddWithValue("p_reject_reason", (object?)rejectReason ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("p_success", NpgsqlDbType.Boolean) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_error", NpgsqlDbType.Varchar) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return (
            (bool)cmd.Parameters["p_success"].Value!,
            cmd.Parameters["p_error"].Value == DBNull.Value ? null : (string?)cmd.Parameters["p_error"].Value
        );
    }

    // ============================================================
    //  IAM PROCS
    // ============================================================

    public async Task<(bool Success, string? Error)> SubmitAgeDeclarationAsync(
        Guid userId, DateOnly dob, string? ipAddress, string? userAgent)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = Proc(conn, "iam.usp_submit_age_declaration");
        cmd.Parameters.Add(new NpgsqlParameter("p_user_id", NpgsqlDbType.Uuid) { Value = userId });
        // IMPORTANT: the proc's p_dob is DATE. Passing DateOnly.ToDateTime(...)
        // via AddWithValue makes Npgsql infer `timestamp without time zone`,
        // which doesn't match the proc signature and you get
        //   42883: procedure iam.usp_submit_age_declaration(... p_dob => timestamp without time zone ...) does not exist
        // Force the param type to Date so the function lookup succeeds.
        cmd.Parameters.Add(new NpgsqlParameter("p_dob", NpgsqlDbType.Date) { Value = dob });

        // Replace this line:
        var ip = ipAddress == null ? (object)DBNull.Value : IPAddress.Parse(ipAddress);
        cmd.Parameters.Add(new NpgsqlParameter("p_ip_address", NpgsqlDbType.Inet) { Value = ip });
        cmd.Parameters.Add(new NpgsqlParameter("p_user_agent", NpgsqlDbType.Text) { Value = (object?)userAgent ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("p_success", NpgsqlDbType.Boolean) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_error", NpgsqlDbType.Varchar) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return (
            (bool)cmd.Parameters["p_success"].Value!,
            cmd.Parameters["p_error"].Value == DBNull.Value ? null : (string?)cmd.Parameters["p_error"].Value
        );
    }

    public async Task<(bool Passed, short AttemptsUsed, string? Error)> SaveAiMaturityResultAsync(
        Guid userId, string? sessionRef, string questionsJson, short finalScore)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = Proc(conn, "iam.usp_save_ai_maturity_result");
        cmd.Parameters.AddWithValue("p_user_id", userId);
        cmd.Parameters.AddWithValue("p_session_ref", (object?)sessionRef ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("p_questions_asked", NpgsqlDbType.Jsonb) { Value = questionsJson });
        cmd.Parameters.AddWithValue("p_final_score", finalScore);
        cmd.Parameters.Add(new NpgsqlParameter("p_passed", NpgsqlDbType.Boolean) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_attempts_used", NpgsqlDbType.Smallint) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_error", NpgsqlDbType.Varchar) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return (
            (bool)cmd.Parameters["p_passed"].Value!,
            (short)cmd.Parameters["p_attempts_used"].Value!,
            cmd.Parameters["p_error"].Value == DBNull.Value ? null : (string?)cmd.Parameters["p_error"].Value
        );
    }

    public async Task<(Guid? DocId, string? Error)> SubmitDocumentVerificationAsync(
        Guid userId, string docType, string cloudinaryPublicId, string cloudinaryUrl)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = Proc(conn, "iam.usp_submit_document_verification");
        cmd.Parameters.AddWithValue("p_user_id", userId);
        cmd.Parameters.AddWithValue("p_doc_type", docType);
        cmd.Parameters.AddWithValue("p_cloudinary_public_id", cloudinaryPublicId);
        cmd.Parameters.AddWithValue("p_cloudinary_url", cloudinaryUrl);
        cmd.Parameters.Add(new NpgsqlParameter("p_doc_id", NpgsqlDbType.Uuid) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_error", NpgsqlDbType.Varchar) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return (
            cmd.Parameters["p_doc_id"].Value == DBNull.Value ? null : (Guid?)cmd.Parameters["p_doc_id"].Value,
            cmd.Parameters["p_error"].Value == DBNull.Value ? null : (string?)cmd.Parameters["p_error"].Value
        );
    }

    // ============================================================
    //  TRUST PROCS
    // ============================================================

    public async Task<(short NewScore, short OldScore)> ApplyTrustEventAsync(
        Guid userId,
        TrustEventType eventType,
        short delta,
        string? reason,
        Guid? refId = null,
        string? refSource = null,
        Guid? appliedBy = null)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = Proc(conn, "trust.usp_apply_trust_event");
        cmd.Parameters.AddWithValue("p_user_id", userId);
        cmd.Parameters.Add(new NpgsqlParameter("p_event_type", NpgsqlDbType.Unknown)
        { Value = eventType.ToString().ToSnakeCase() });
        cmd.Parameters.AddWithValue("p_delta", delta);
        cmd.Parameters.AddWithValue("p_reason", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("p_ref_id", (object?)refId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("p_ref_source", (object?)refSource ?? DBNull.Value);
        cmd.Parameters.AddWithValue("p_applied_by", (object?)appliedBy ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("p_new_score", NpgsqlDbType.Smallint) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_old_score", NpgsqlDbType.Smallint) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return (
            (short)cmd.Parameters["p_new_score"].Value!,
            (short)cmd.Parameters["p_old_score"].Value!
        );
    }

    public async Task<(Guid? ReportId, string? Error)> FileReportAsync(
        Guid reporterId, Guid reportedId, string reason, string? description)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = Proc(conn, "trust.usp_file_report");
        cmd.Parameters.AddWithValue("p_reporter_id", reporterId);
        cmd.Parameters.AddWithValue("p_reported_id", reportedId);
        cmd.Parameters.AddWithValue("p_reason", reason);
        cmd.Parameters.AddWithValue("p_description", (object?)description ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("p_report_id", NpgsqlDbType.Uuid) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_error", NpgsqlDbType.Varchar) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return (
            cmd.Parameters["p_report_id"].Value == DBNull.Value ? null : (Guid?)cmd.Parameters["p_report_id"].Value,
            cmd.Parameters["p_error"].Value == DBNull.Value ? null : (string?)cmd.Parameters["p_error"].Value
        );
    }

    // ── Quick trust score read (no proc needed) ───────────────
    public async Task<short> GetTrustScoreAsync(Guid userId)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT trust_score FROM user_auth.users WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", userId);
        var result = await cmd.ExecuteScalarAsync();
        return result == null || result == DBNull.Value ? (short)50 : Convert.ToInt16(result);
    }

    // ── Combined trust + age verified read (UserStateService) ────
    //  Single round-trip so the cache populates both keys at once
    //  on a miss.
    public async Task<(short TrustScore, bool AgeVerified)> GetUserStateAsync(Guid userId)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT trust_score, age_verified FROM user_auth.users WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", userId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var score = reader.IsDBNull(0) ? (short)50 : reader.GetInt16(0);
            var verified = !reader.IsDBNull(1) && reader.GetBoolean(1);
            return (score, verified);
        }
        return ((short)50, false);
    }

    // ============================================================
    //  BILLING PROCS
    // ============================================================

    public async Task<(Guid? SubscriptionId, Guid? PaymentId, string? Error)> CreateSubscriptionAsync(
        Guid userId, string planType, string razorpayOrderId,
        int amountPaise, DateTime startsAt, DateTime endsAt)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = Proc(conn, "billing.usp_create_subscription");
        cmd.Parameters.AddWithValue("p_user_id", userId);
        cmd.Parameters.Add(new NpgsqlParameter("p_plan_type", NpgsqlDbType.Unknown)
        { Value = planType.ToLower() });
        cmd.Parameters.AddWithValue("p_razorpay_order_id", razorpayOrderId);
        cmd.Parameters.AddWithValue("p_amount_paise", amountPaise);
        cmd.Parameters.AddWithValue("p_starts_at", startsAt);
        cmd.Parameters.AddWithValue("p_ends_at", endsAt);
        cmd.Parameters.Add(new NpgsqlParameter("p_subscription_id", NpgsqlDbType.Uuid) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_payment_id", NpgsqlDbType.Uuid) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_error", NpgsqlDbType.Varchar) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return (
            cmd.Parameters["p_subscription_id"].Value == DBNull.Value ? null : (Guid?)cmd.Parameters["p_subscription_id"].Value,
            cmd.Parameters["p_payment_id"].Value == DBNull.Value ? null : (Guid?)cmd.Parameters["p_payment_id"].Value,
            cmd.Parameters["p_error"].Value == DBNull.Value ? null : (string?)cmd.Parameters["p_error"].Value
        );
    }

    public async Task<(bool Success, Guid? UserId, string? Error)> ActivateSubscriptionAsync(
        string razorpayOrderId, string razorpayPaymentId)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = Proc(conn, "billing.usp_activate_subscription");
        cmd.Parameters.AddWithValue("p_razorpay_order_id", razorpayOrderId);
        cmd.Parameters.AddWithValue("p_razorpay_payment_id", razorpayPaymentId);
        cmd.Parameters.Add(new NpgsqlParameter("p_success", NpgsqlDbType.Boolean) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_user_id", NpgsqlDbType.Uuid) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_error", NpgsqlDbType.Varchar) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return (
            (bool)cmd.Parameters["p_success"].Value!,
            cmd.Parameters["p_user_id"].Value == DBNull.Value ? null : (Guid?)cmd.Parameters["p_user_id"].Value,
            cmd.Parameters["p_error"].Value == DBNull.Value ? null : (string?)cmd.Parameters["p_error"].Value
        );
    }

    // ============================================================
    //  CHAT PROCS
    // ============================================================

    public async Task<(bool IsBanned, string? Reason, DateTime? ExpiresAt)> CheckRoomBanAsync(
        Guid userId, string roomSlug)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT * FROM chat.usp_check_room_ban(@p_user_id, @p_room_slug)", conn);
        cmd.Parameters.AddWithValue("p_user_id", userId);
        cmd.Parameters.AddWithValue("p_room_slug", roomSlug);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var isBanned = reader.GetBoolean(0);
            if (!isBanned) return (false, null, null);
            var reason = reader.IsDBNull(2) ? null : reader.GetString(2);
            var expiresAt = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3);
            return (true, reason, expiresAt);
        }
        return (false, null, null);
    }
}

// ── Snake case helper ─────────────────────────────────────────
internal static class StringExtensions
{
    public static string ToSnakeCase(this string value)
        => string.Concat(value.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + c : c.ToString()
        )).ToLower();
}