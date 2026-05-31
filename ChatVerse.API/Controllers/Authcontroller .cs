using ChatVerse.API.Extensions;
using ChatVerse.Domain.Constants;
using ChatVerse.Domain.Enums;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using ChatVerse.Infrastructure.ExternalServices.Email;
using ChatVerse.Infrastructure.Persistence.Redis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatVerse.API.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly PostgresProcService _postgres;
    private readonly RedisService _redis;
    private readonly JwtService _jwt;
    private readonly BrevoEmailService _email;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        PostgresProcService postgres,
        RedisService redis,
        JwtService jwt,
        BrevoEmailService email,
        ILogger<AuthController> logger)
    {
        _postgres = postgres;
        _redis = redis;
        _jwt = jwt;
        _email = email;
        _logger = logger;
    }

    // ============================================================
    //  POST /api/auth/guest
    //  Auto-generate a guest session — no email needed
    //  Called when user lands on site for first time
    // ============================================================
    [HttpPost("guest")]
    [AllowAnonymous]
    public async Task<IActionResult> CreateGuest()
    {
        // Generate Adjective + Noun + 4-digit username
        var username = GenerateGuestUsername();

        var (userId, finalUsername) = await _postgres.CreateGuestUserAsync(username);

        // Mark active day
        await _postgres.MarkUserActiveDayAsync(userId);

        // Mint JWT
        var token = _jwt.GenerateToken(
            userId: userId,
            username: finalUsername,
            isGuest: true,
            trustScore: 50,
            isEmailVerified: false,
            ageVerified: false
        );

        // Cache session in Redis
        await _redis.SetSessionAsync(userId.ToString(), new
        {
            userId = userId.ToString(),
            username = finalUsername,
            isGuest = true
        });

        await _redis.SetUserOnlineAsync(userId.ToString());

        return Ok(ApiResponse<object>.Ok(new
        {
            token,
            userId = userId.ToString(),
            username = finalUsername,
            isGuest = true
        }, "Guest session created"));
    }

    // ============================================================
    //  POST /api/auth/register
    //  Email registration — sends OTP after success
    // ============================================================
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse.Fail("Invalid request data"));

        // Basic email format check
        if (!req.Email.Contains('@'))
            return BadRequest(ApiResponse.Fail("Invalid email address"));

        // Password minimum length
        if (req.Password.Length < 8)
            return BadRequest(ApiResponse.Fail("Password must be at least 8 characters"));

        var passwordHash = PasswordHasher.Hash(req.Password);
        var (userId, error) = await _postgres.RegisterUserAsync(
            req.Username, req.Email, passwordHash);

        if (error != null)
        {
            var message = error switch
            {
                "EMAIL_TAKEN" => "This email is already registered",
                "USERNAME_TAKEN" => "This username is already taken",
                _ => "Registration failed"
            };
            return Conflict(ApiResponse.Fail(message));
        }

        // Check OTP rate limit
        var allowed = await _redis.TryAllowOtpRequestAsync(req.Email);
        if (!allowed)
            return StatusCode(429, ApiResponse.Fail("Too many OTP requests. Try again in 15 minutes."));

        // Generate + store OTP
        var otpCode = GenerateOtpCode();
        var expiresAt = DateTime.UtcNow.AddMinutes(Otp.ExpiryMinutes);

        await _postgres.UpsertOtpAsync(req.Email, otpCode, OtpPurpose.EmailVerification, expiresAt);

        // Send OTP email via Brevo
        await _email.SendOtpEmailAsync(req.Email, otpCode, "email_verification");
        _logger.LogInformation("OTP sent to {Email}", req.Email);

        return Ok(ApiResponse<object>.Ok(new
        {
            userId = userId.ToString(),
            email = req.Email,
            otpSent = true,
            expiresIn = $"{Otp.ExpiryMinutes} minutes"
        }, "Registration successful. Check your email for OTP."));
    }

    // ============================================================
    //  POST /api/auth/verify-otp
    //  Verify email OTP — returns JWT on success
    // ============================================================
    [HttpPost("verify-otp")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse.Fail("Invalid request data"));

        var (isValid, userId, message) = await _postgres.VerifyOtpAsync(
            req.Email, req.Code, OtpPurpose.EmailVerification);

        if (!isValid)
        {
            var errorMsg = message switch
            {
                "OTP_NOT_FOUND" => "Invalid OTP code",
                "OTP_EXPIRED" => "OTP has expired. Please request a new one.",
                _ => "OTP verification failed"
            };
            return BadRequest(ApiResponse.Fail(errorMsg));
        }

        // Mark active day
        await _postgres.MarkUserActiveDayAsync(userId!.Value);

        // Pull canonical username + flags from DB so the JWT reflects truth
        // instead of an email-prefix guess.
        var user = await _postgres.GetUserAuthByIdAsync(userId.Value);
        var username = user?.Username ?? req.Email.Split('@')[0];
        var trustScore = user?.TrustScore ?? (short)60;
        var ageVerified = user?.AgeVerified ?? false;

        // Mint JWT — email now verified
        var token = _jwt.GenerateToken(
            userId: userId.Value,
            username: username,
            isGuest: false,
            trustScore: trustScore,
            isEmailVerified: true,
            ageVerified: ageVerified
        );

        await _redis.SetSessionAsync(userId.Value.ToString(), new
        {
            userId = userId.Value.ToString(),
            username,
            isGuest = false
        });
        await _redis.SetUserOnlineAsync(userId.Value.ToString());

        // Send welcome email (fire and forget) — use the real username
        var welcomeUsername = username;
        _ = Task.Run(async () =>
        {
            try { await _email.SendWelcomeEmailAsync(req.Email, welcomeUsername); }
            catch (Exception ex) { _logger.LogWarning(ex, "Welcome email failed"); }
        });

        return Ok(ApiResponse<object>.Ok(new
        {
            token,
            userId = userId.Value.ToString(),
            username,
            trustScore,
            isEmailVerified = true,
            ageVerified,
            emailVerified = true  // legacy alias — keep for compat
        }, "Email verified successfully"));
    }

    // ============================================================
    //  POST /api/auth/login
    //  Email + password login — returns JWT
    //
    //  Verification happens in code (not in the stored proc) so we can
    //  use BCrypt, which embeds a random salt per hash and therefore
    //  can't be matched by an exact-string SELECT. Legacy SHA-256 hashes
    //  are still recognised by PasswordHasher.Verify, and silently
    //  re-hashed to BCrypt on the next successful login.
    // ============================================================
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse.Fail("Invalid request data"));

        var record = await _postgres.GetUserAuthByEmailAsync(req.Email);

        // Generic message to avoid leaking which half (email or password) is wrong.
        if (record == null || string.IsNullOrEmpty(record.PasswordHash))
            return StatusCode(401, ApiResponse.Fail("Invalid email or password"));

        // Status gates first — banned/suspended users shouldn't even hit hash compare.
        if (string.Equals(record.Status, "banned", StringComparison.OrdinalIgnoreCase))
            return StatusCode(403, ApiResponse.Fail("Your account has been banned"));
        if (string.Equals(record.Status, "suspended", StringComparison.OrdinalIgnoreCase))
            return StatusCode(403, ApiResponse.Fail("Your account is suspended"));

        var (isValid, needsRehash) = PasswordHasher.Verify(req.Password, record.PasswordHash);
        if (!isValid)
            return StatusCode(401, ApiResponse.Fail("Invalid email or password"));

        // Silently upgrade legacy SHA-256 hashes to BCrypt.
        if (needsRehash)
        {
            try
            {
                var newHash = PasswordHasher.Hash(req.Password);
                await _postgres.UpdatePasswordHashAsync(record.UserId, newHash);
                _logger.LogInformation("Re-hashed legacy password for user {UserId}", record.UserId);
            }
            catch (Exception ex)
            {
                // Don't block the login if re-hash fails — try again next time.
                _logger.LogWarning(ex, "Failed to re-hash legacy password for user {UserId}", record.UserId);
            }
        }

        // Mark active day — may clear tenure gate
        var tenureCleared = await _postgres.MarkUserActiveDayAsync(record.UserId);
        if (tenureCleared)
            _logger.LogInformation("User {UserId} just cleared 7-day tenure gate", record.UserId);

        var token = _jwt.GenerateToken(
            userId: record.UserId,
            username: record.Username,
            isGuest: false,
            trustScore: record.TrustScore,
            isEmailVerified: record.IsEmailVerified,
            ageVerified: record.AgeVerified
        );

        await _redis.SetSessionAsync(record.UserId.ToString(), new
        {
            userId = record.UserId.ToString(),
            username = record.Username,
            isGuest = false
        });

        await _redis.SetUserOnlineAsync(record.UserId.ToString());

        return Ok(ApiResponse<object>.Ok(new
        {
            token,
            userId = record.UserId.ToString(),
            username = record.Username,
            trustScore = record.TrustScore,
            isEmailVerified = record.IsEmailVerified,
            ageVerified = record.AgeVerified
        }, "Login successful"));
    }

    // ============================================================
    //  POST /api/auth/resend-otp
    //  Resend OTP — rate limited via Redis
    // ============================================================
    [HttpPost("resend-otp")]
    [AllowAnonymous]
    public async Task<IActionResult> ResendOtp([FromBody] ResendOtpRequest req)
    {
        var allowed = await _redis.TryAllowOtpRequestAsync(req.Email);
        if (!allowed)
            return StatusCode(429, ApiResponse.Fail("Too many requests. Try again in 15 minutes."));

        var otpCode = GenerateOtpCode();
        var expiresAt = DateTime.UtcNow.AddMinutes(Otp.ExpiryMinutes);

        await _postgres.UpsertOtpAsync(req.Email, otpCode, OtpPurpose.EmailVerification, expiresAt);

        await _email.SendOtpEmailAsync(req.Email, otpCode, "email_verification");
        _logger.LogInformation("OTP resent to {Email}", req.Email);

        return Ok(ApiResponse.Ok("OTP resent successfully"));
    }

    // ============================================================
    //  POST /api/auth/logout
    //  Invalidate session in Redis
    // ============================================================
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        var userId = JwtService.GetUserId(User).ToString();
        await _redis.DeleteSessionAsync(userId);
        await _redis.SetUserOfflineAsync(userId);
        return Ok(ApiResponse.Ok("Logged out successfully"));
    }

    // ============================================================
    //  POST /api/auth/upgrade
    //  Convert guest account to full account
    // ============================================================
    [HttpPost("upgrade")]
    [Authorize]
    public async Task<IActionResult> UpgradeGuest([FromBody] RegisterRequest req)
    {
        var userId = JwtService.GetUserId(User);
        var isGuest = JwtService.GetIsGuest(User);

        if (!isGuest)
            return BadRequest(ApiResponse.Fail("Account is already registered"));

        if (req.Password.Length < 8)
            return BadRequest(ApiResponse.Fail("Password must be at least 8 characters"));

        var passwordHash = PasswordHasher.Hash(req.Password);
        var (success, error) = await _postgres.UpgradeGuestToUserAsync(
            userId, req.Email, passwordHash);

        if (!success)
        {
            var message = error switch
            {
                "EMAIL_TAKEN" => "This email is already registered",
                "GUEST_NOT_FOUND" => "Guest session not found",
                _ => "Upgrade failed"
            };
            return Conflict(ApiResponse.Fail(message));
        }

        // Send OTP for email verification
        var allowed = await _redis.TryAllowOtpRequestAsync(req.Email);
        if (allowed)
        {
            var otpCode = GenerateOtpCode();
            var expiresAt = DateTime.UtcNow.AddMinutes(Otp.ExpiryMinutes);
            await _postgres.UpsertOtpAsync(req.Email, otpCode, OtpPurpose.EmailVerification, expiresAt);
            await _email.SendOtpEmailAsync(req.Email, otpCode, "email_verification");
            _logger.LogInformation("Upgrade OTP sent to {Email}", req.Email);
        }

        return Ok(ApiResponse.Ok("Account upgraded. Please verify your email."));
    }

    // ============================================================
    //  GET /api/auth/me
    //  Current user profile from JWT
    // ============================================================
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me()
    {
        return Ok(ApiResponse<object>.Ok(new
        {
            userId = JwtService.GetUserId(User).ToString(),
            username = JwtService.GetUsername(User),
            isGuest = JwtService.GetIsGuest(User),
            trustScore = JwtService.GetTrustScore(User),
            ageVerified = JwtService.GetAgeVerified(User)
        }));
    }

    // ============================================================
    //  Private helpers
    // ============================================================

    private static string GenerateOtpCode()
    {
        return Random.Shared.Next(100000, 999999).ToString();
    }

    private static readonly string[] Adjectives =
    {
        "Cool", "Fast", "Brave", "Silent", "Wild", "Clever",
        "Fierce", "Swift", "Bold", "Calm", "Sharp", "Dark"
    };

    private static readonly string[] Nouns =
    {
        "Fox", "Wolf", "Hawk", "Tiger", "Panda", "Eagle",
        "Bear", "Lion", "Shark", "Raven", "Cobra", "Lynx"
    };

    private static string GenerateGuestUsername()
    {
        var adj = Adjectives[Random.Shared.Next(Adjectives.Length)];
        var noun = Nouns[Random.Shared.Next(Nouns.Length)];
        var digits = Random.Shared.Next(1000, 9999);
        return $"{adj}{noun}{digits}";
    }
}

// ── Request DTOs ─────────────────────────────────────────────
// Note: [Required] on record types must go on constructor param, not property
public record RegisterRequest(
    [System.ComponentModel.DataAnnotations.Required] string Username,
    [System.ComponentModel.DataAnnotations.Required] string Email,
    [System.ComponentModel.DataAnnotations.Required] string Password
);

public record LoginRequest(
    [System.ComponentModel.DataAnnotations.Required] string Email,
    [System.ComponentModel.DataAnnotations.Required] string Password
);

public record VerifyOtpRequest(
    [System.ComponentModel.DataAnnotations.Required] string Email,
    [System.ComponentModel.DataAnnotations.Required] string Code
);

public record ResendOtpRequest(
    [System.ComponentModel.DataAnnotations.Required] string Email
);