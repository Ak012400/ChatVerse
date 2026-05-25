using ChatVerse.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using System.Data;

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

    // ── Helper: create StoredProcedure command ────────────────
    private static NpgsqlCommand Proc(NpgsqlConnection conn, string procName)
    {
        var cmd = new NpgsqlCommand(procName, conn);
        cmd.CommandType = CommandType.StoredProcedure;
        return cmd;
    }

    // ============================================================
    //  usp_create_guest_user
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

        return (
            (Guid)cmd.Parameters["p_user_id"].Value!,
            (string)cmd.Parameters["p_username_out"].Value!
        );
    }

    // ============================================================
    //  usp_register_user
    // ============================================================
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

    // ============================================================
    //  usp_upsert_otp
    // ============================================================
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

    // ============================================================
    //  usp_verify_otp
    // ============================================================
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

    // ============================================================
    //  usp_login_user
    // ============================================================
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

    // ============================================================
    //  usp_upgrade_guest_to_user
    // ============================================================
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

    // ============================================================
    //  usp_mark_user_active_day
    // ============================================================
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
    //  usp_submit_age_declaration
    // ============================================================
    public async Task<(bool Success, string? Error)> SubmitAgeDeclarationAsync(
        Guid userId, DateOnly dob, string? ipAddress, string? userAgent)
    {
        var conn = await GetOpenConnectionAsync();
        await using var cmd = Proc(conn, "iam.usp_submit_age_declaration");

        cmd.Parameters.AddWithValue("p_user_id", userId);
        cmd.Parameters.AddWithValue("p_dob", dob.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("p_ip_address", (object?)ipAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("p_user_agent", (object?)userAgent ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("p_success", NpgsqlDbType.Boolean) { Direction = ParameterDirection.Output });
        cmd.Parameters.Add(new NpgsqlParameter("p_error", NpgsqlDbType.Varchar) { Direction = ParameterDirection.Output });

        await cmd.ExecuteNonQueryAsync();

        return (
            (bool)cmd.Parameters["p_success"].Value!,
            cmd.Parameters["p_error"].Value == DBNull.Value ? null : (string?)cmd.Parameters["p_error"].Value
        );
    }

    // ============================================================
    //  usp_save_ai_maturity_result
    // ============================================================
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

    // ============================================================
    //  usp_submit_document_verification
    // ============================================================
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
}

internal static class StringExtensions
{
    public static string ToSnakeCase(this string value)
        => string.Concat(value.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + c : c.ToString()
        )).ToLower();
}