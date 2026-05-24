using ChatVerse.API.Extensions;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text.Json;


namespace ChatVerse.API.Middleware;

/// <summary>
/// Global exception handler middleware.
/// Catches any unhandled exception anywhere in the pipeline,
/// logs it to logs.error_logs via usp_log_error,
/// returns clean JSON 500 to client — never leaks stack traces.
/// </summary>
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ChatVerseDbContext db)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception on {Method} {Path}",
                context.Request.Method, context.Request.Path);

            await LogToDatabase(context, db, ex);
            await WriteErrorResponse(context);
        }
    }

    // ── Write clean 500 JSON to client ───────────────────────
    private static async Task WriteErrorResponse(HttpContext context)
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";

        var response = ApiResponse.Fail("An unexpected error occurred. Please try again.");
        await context.Response.WriteAsync(JsonSerializer.Serialize(response));
    }

    // ── Persist to PostgreSQL logs.error_logs ─────────────────
    private async Task LogToDatabase(
        HttpContext context, ChatVerseDbContext db, Exception ex)
    {
        try
        {
            // Extract userId from JWT if present (nullable — guests/unauth have no userId)
            Guid? userId = null;
            var userIdClaim = context.User?.FindFirst("uid")?.Value;
            if (Guid.TryParse(userIdClaim, out var parsedId))
                userId = parsedId;

            // Sanitize request body — remove passwords before storing
            var requestBody = await ReadSanitizedBodyAsync(context);

            // Get client IP
            var ip = context.Connection.RemoteIpAddress?.ToString()
                  ?? context.Request.Headers["X-Forwarded-For"].FirstOrDefault()
                  ?? "unknown";

            var pUserId = new NpgsqlParameter("p_user_id", (object?)userId ?? DBNull.Value);
            var pPath = new NpgsqlParameter("p_request_path", context.Request.Path.ToString());
            var pMethod = new NpgsqlParameter("p_request_method", context.Request.Method);
            var pStatus = new NpgsqlParameter("p_status_code", 500);
            var pExType = new NpgsqlParameter("p_exception_type", ex.GetType().FullName ?? "Unknown");
            var pMessage = new NpgsqlParameter("p_message", ex.Message);
            var pStack = new NpgsqlParameter("p_stack_trace", ex.StackTrace ?? "");
            var pBody = new NpgsqlParameter("p_request_body", requestBody ?? "");
            var pAgent = new NpgsqlParameter("p_user_agent", context.Request.Headers.UserAgent.ToString());
            var pIp = new NpgsqlParameter("p_ip_address", ip);
            var pLogId = new NpgsqlParameter("p_log_id", NpgsqlTypes.NpgsqlDbType.Uuid)
            { Direction = System.Data.ParameterDirection.Output };

            await db.Database.ExecuteSqlRawAsync(
                @"CALL logs.usp_log_error(
                    @p_user_id, @p_request_path, @p_request_method,
                    @p_status_code, @p_exception_type, @p_message,
                    @p_stack_trace, @p_request_body, @p_user_agent,
                    @p_ip_address, @p_log_id)",
                pUserId, pPath, pMethod, pStatus, pExType,
                pMessage, pStack, pBody, pAgent, pIp, pLogId);
        }
        catch (Exception logEx)
        {
            // Logging failed — just write to console, never crash
            _logger.LogCritical(logEx, "Failed to write error log to database");
        }
    }

    // ── Read + sanitize request body ──────────────────────────
    private static async Task<string?> ReadSanitizedBodyAsync(HttpContext context)
    {
        try
        {
            context.Request.EnableBuffering();
            context.Request.Body.Position = 0;

            using var reader = new StreamReader(
                context.Request.Body,
                leaveOpen: true);

            var body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;

            if (string.IsNullOrWhiteSpace(body)) return null;

            // Remove sensitive fields before storing
            var sensitiveFields = new[]
            {
                "password", "passwordHash", "password_hash",
                "otp", "code", "token", "secret",
                "cardNumber", "cvv"
            };

            var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
            if (json == null) return body;

            foreach (var field in sensitiveFields)
            {
                if (json.ContainsKey(field))
                    json[field] = JsonSerializer.Deserialize<JsonElement>("\"[REDACTED]\"");
            }

            // Truncate to 2000 chars max
            var sanitized = JsonSerializer.Serialize(json);
            return sanitized.Length > 2000
                ? sanitized[..2000] + "...[truncated]"
                : sanitized;
        }
        catch
        {
            return "[body unreadable]";
        }
    }
}

// ── Extension for clean registration in Program.cs ───────────
public static class GlobalExceptionMiddlewareExtensions
{
    public static IApplicationBuilder UseGlobalExceptionHandler(
        this IApplicationBuilder app)
        => app.UseMiddleware<GlobalExceptionMiddleware>();
}