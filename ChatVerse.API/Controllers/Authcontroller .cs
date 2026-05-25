using ChatVerse.API.Extensions;
using ChatVerse.Domain.Constants;
using ChatVerse.Domain.Enums;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using ChatVerse.Infrastructure.Persistence.Redis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;

namespace ChatVerse.API.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly PostgresProcService _postgres;
    private readonly RedisService _redis;
    private readonly JwtService _jwt;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        PostgresProcService postgres,
        RedisService redis,
        JwtService jwt,
        ILogger<AuthController> logger)
    {
        _postgres = postgres;
        _redis = redis;
        _jwt = jwt;
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

        var passwordHash = HashPassword(req.Password);
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

        // TODO: Send email via Brevo
        // await _emailService.SendOtpEmailAsync(req.Email, otpCode);
        // For now log to console in dev
        _logger.LogInformation("OTP for {Email}: {Code}", req.Email, otpCode);

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

        // Mint JWT — email now verified
        var token = _jwt.GenerateToken(
            userId: userId.Value,
            username: req.Email.Split('@')[0], // temp — get real username below
            isGuest: false,
            trustScore: 60,  // base + otp_verified delta
            isEmailVerified: true,
            ageVerified: false
        );

        await _redis.SetUserOnlineAsync(userId.Value.ToString());

        return Ok(ApiResponse<object>.Ok(new
        {
            token,
            userId = userId.Value.ToString(),
            emailVerified = true
        }, "Email verified successfully"));
    }

    // ============================================================
    //  POST /api/auth/login
    //  Email + password login — returns JWT
    // ============================================================
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse.Fail("Invalid request data"));

        var passwordHash = HashPassword(req.Password);

        var (userId, username, trustScore, isEmailVerified, ageVerified, error)
            = await _postgres.LoginUserAsync(req.Email, passwordHash);

        if (error != null)
        {
            var message = error switch
            {
                "INVALID_CREDENTIALS" => "Invalid email or password",
                "ACCOUNT_BANNED" => "Your account has been banned",
                "ACCOUNT_SUSPENDED" => "Your account is suspended",
                _ => "Login failed"
            };
            // 401 for invalid creds, 403 for banned/suspended
            var statusCode = error == "INVALID_CREDENTIALS" ? 401 : 403;
            return StatusCode(statusCode, ApiResponse.Fail(message));
        }

        // Mark active day — may clear tenure gate
        var tenureCleared = await _postgres.MarkUserActiveDayAsync(userId!.Value);
        if (tenureCleared)
            _logger.LogInformation("User {UserId} just cleared 7-day tenure gate", userId);

        var token = _jwt.GenerateToken(
            userId: userId.Value,
            username: username!,
            isGuest: false,
            trustScore: trustScore,
            isEmailVerified: isEmailVerified,
            ageVerified: ageVerified
        );

        await _redis.SetSessionAsync(userId.Value.ToString(), new
        {
            userId = userId.Value.ToString(),
            username,
            isGuest = false
        });

        await _redis.SetUserOnlineAsync(userId.Value.ToString());

        return Ok(ApiResponse<object>.Ok(new
        {
            token,
            userId = userId.Value.ToString(),
            username,
            trustScore,
            isEmailVerified,
            ageVerified
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

        // TODO: await _emailService.SendOtpEmailAsync(req.Email, otpCode);
        _logger.LogInformation("Resent OTP for {Email}: {Code}", req.Email, otpCode);

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

        var passwordHash = HashPassword(req.Password);
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
            // TODO: await _emailService.SendOtpEmailAsync(req.Email, otpCode);
            _logger.LogInformation("Upgrade OTP for {Email}: {Code}", req.Email, otpCode);
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

    private static string HashPassword(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexString(bytes).ToLower();
    }

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
public record RegisterRequest(
    [System.ComponentModel.DataAnnotations.Required]
    string Username,
    [System.ComponentModel.DataAnnotations.Required]
    string Email,
    [System.ComponentModel.DataAnnotations.Required]
    string Password
);

public record LoginRequest(
    [System.ComponentModel.DataAnnotations.Required]
    string Email,
    [System.ComponentModel.DataAnnotations.Required]
    string Password
);

public record VerifyOtpRequest(
    [System.ComponentModel.DataAnnotations.Required]
    string Email,
    [System.ComponentModel.DataAnnotations.Required]
    string Code
);

public record ResendOtpRequest(
    [System.ComponentModel.DataAnnotations.Required]
    string Email
);