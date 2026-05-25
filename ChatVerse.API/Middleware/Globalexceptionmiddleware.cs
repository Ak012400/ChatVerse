using ChatVerse.API.Extensions;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using System.Data;
using System.Text.Json;

namespace ChatVerse.API.Middleware;

public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ChatVerseDbContext db)
    {
        try { await _next(context); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception on {Method} {Path}",
                context.Request.Method, context.Request.Path);
            await LogToDatabase(context, db, ex);
            await WriteErrorResponse(context);
        }
    }

    private static async Task WriteErrorResponse(HttpContext context)
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(ApiResponse.Fail("An unexpected error occurred. Please try again.")));
    }

    private async Task LogToDatabase(HttpContext context, ChatVerseDbContext db, Exception ex)
    {
        try
        {
            Guid? userId = null;
            if (Guid.TryParse(context.User?.FindFirst("uid")?.Value, out var pid))
                userId = pid;

            var requestBody = await ReadSanitizedBodyAsync(context);
            var ip = context.Connection.RemoteIpAddress?.ToString()
                  ?? context.Request.Headers["X-Forwarded-For"].FirstOrDefault()
                  ?? "unknown";

            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open) await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand("logs.usp_log_error", conn);
            cmd.CommandType = CommandType.StoredProcedure;

            cmd.Parameters.AddWithValue("p_user_id", (object?)userId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("p_request_path", context.Request.Path.ToString());
            cmd.Parameters.AddWithValue("p_request_method", context.Request.Method);
            cmd.Parameters.AddWithValue("p_status_code", 500);
            cmd.Parameters.AddWithValue("p_exception_type", ex.GetType().FullName ?? "Unknown");
            cmd.Parameters.AddWithValue("p_message", ex.Message);
            cmd.Parameters.AddWithValue("p_stack_trace", ex.StackTrace ?? "");
            cmd.Parameters.AddWithValue("p_request_body", requestBody ?? "");
            cmd.Parameters.AddWithValue("p_user_agent", context.Request.Headers.UserAgent.ToString());
            cmd.Parameters.AddWithValue("p_ip_address", ip);
            cmd.Parameters.Add(new NpgsqlParameter("p_log_id", NpgsqlDbType.Uuid)
            { Direction = ParameterDirection.Output });

            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception logEx)
        {
            _logger.LogCritical(logEx, "Failed to write error log to database");
        }
    }

    private static async Task<string?> ReadSanitizedBodyAsync(HttpContext context)
    {
        try
        {
            context.Request.EnableBuffering();
            context.Request.Body.Position = 0;
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
            if (string.IsNullOrWhiteSpace(body)) return null;

            var sensitiveFields = new[] { "password", "passwordHash", "password_hash", "otp", "code", "token", "secret" };
            var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
            if (json == null) return body;

            foreach (var field in sensitiveFields)
                if (json.ContainsKey(field))
                    json[field] = JsonSerializer.Deserialize<JsonElement>("\"[REDACTED]\"");

            var sanitized = JsonSerializer.Serialize(json);
            return sanitized.Length > 2000 ? sanitized[..2000] + "...[truncated]" : sanitized;
        }
        catch { return "[body unreadable]"; }
    }
}

public static class GlobalExceptionMiddlewareExtensions
{
    public static IApplicationBuilder UseGlobalExceptionHandler(this IApplicationBuilder app)
        => app.UseMiddleware<GlobalExceptionMiddleware>();
}