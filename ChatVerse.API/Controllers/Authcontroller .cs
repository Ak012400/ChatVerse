using ChatVerse.API.Extensions;
using ChatVerse.Domain.Constants;
using ChatVerse.Domain.Enums;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using ChatVerse.Infrastructure.ExternalServices.Email;
using ChatVerse.Infrastructure.Persistence.Redis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ChatVerse.API.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly PostgresProcService _postgres;
    private readonly RedisService _redis;
    private readonly JwtService _jwt;
    private readonly BrevoEmailService _email;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        PostgresProcService postgres,
        RedisService redis,
        JwtService jwt,
        BrevoEmailService email,
        IConfiguration config,
        ILogger<AuthController> logger)
    {
        _postgres = postgres;
        _redis = redis;
        _jwt = jwt;
        _email = email;
        _config = config;
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
    //  Stage 1 of OTP-first registration.
    //
    //  Nothing is written to user_auth.users yet — we hold the intent
    //  (username + email + hashed password) in Redis with a short TTL.
    //  The actual row is only created after the OTP is verified, so a
    //  user who never completes verification leaves no trace.
    // ============================================================
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse.Fail("Invalid request data"));
        if (!req.Email.Contains('@'))
            return BadRequest(ApiResponse.Fail("Invalid email address"));
        if (req.Password.Length < 8)
            return BadRequest(ApiResponse.Fail("Password must be at least 8 characters"));
        if (string.IsNullOrWhiteSpace(req.Username) || req.Username.Length < 3)
            return BadRequest(ApiResponse.Fail("Username must be at least 3 characters"));

        // Best-effort fast fail — the proc's UNIQUE constraint is the
        // real guarantee, but checking up front avoids wasting an OTP
        // and lets the form react instantly.
        var (emailTaken, usernameTaken) =
            await _postgres.CheckRegistrationAvailabilityAsync(req.Email, req.Username);
        if (emailTaken)
            return Conflict(ApiResponse.Fail("This email is already registered"));
        if (usernameTaken)
            return Conflict(ApiResponse.Fail("This username is already taken"));

        // Rate-limit OTPs BEFORE doing the work (3 per 15 min per email).
        var allowed = await _redis.TryAllowOtpRequestAsync(req.Email);
        if (!allowed)
            return StatusCode(429, ApiResponse.Fail(
                "Too many OTP requests. Try again in 15 minutes."));

        // Stage the intent + OTP in Redis.
        var passwordHash = PasswordHasher.Hash(req.Password);
        var intent = JsonSerializer.Serialize(new
        {
            username = req.Username,
            email = req.Email,
            passwordHash
        });
        await _redis.SetRegistrationIntentAsync(req.Email, intent);

        var otpCode = GenerateOtpCode();
        await _redis.SetRegistrationOtpAsync(req.Email, otpCode);

        // Fire-and-forget the SMTP send so a slow / unreachable mail
        // provider can't block the HTTP response. If SendOtpEmailAsync
        // returns false (timeout, auth failure, etc.) the error is logged
        // by the email service itself. The user still gets a 200 OK
        // immediately — they'll discover the missing email via the
        // VerifyOtp page's "didn't get a code?" resend button.
        var bgEmail = req.Email;
        var bgCode = otpCode;
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await _email.SendOtpEmailAsync(bgEmail, bgCode, "email_verification");
                if (ok)
                    _logger.LogInformation("Registration OTP delivered to {Email}", bgEmail);
                else
                    _logger.LogWarning("Registration OTP send FAILED for {Email}", bgEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background OTP send threw for {Email}", bgEmail);
            }
        });

        return Ok(ApiResponse<object>.Ok(new
        {
            email = req.Email,
            otpSent = true,
            expiresIn = $"{Otp.ExpiryMinutes} minutes"
        }, "OTP sent. Check your email to finish creating your account."));
    }

    // ============================================================
    //  POST /api/auth/verify-otp
    //  Stage 2 of OTP-first registration. Reads the Redis-staged
    //  intent + OTP, creates the user row, marks email_verified=true,
    //  applies the +10 trust event, and returns a JWT.
    // ============================================================
    [HttpPost("verify-otp")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse.Fail("Invalid request data"));

        var storedOtp = await _redis.GetRegistrationOtpAsync(req.Email);
        if (storedOtp == null)
            return BadRequest(ApiResponse.Fail(
                "OTP has expired or was never requested. Please register again."));
        if (!string.Equals(storedOtp, req.Code, StringComparison.Ordinal))
            return BadRequest(ApiResponse.Fail("Invalid OTP code"));

        var intentJson = await _redis.GetRegistrationIntentAsync(req.Email);
        if (intentJson == null)
        {
            // Intent expired before the OTP did — rare edge case (different TTLs).
            return BadRequest(ApiResponse.Fail(
                "Your registration session expired. Please register again."));
        }

        // NOTE: the Redis-staged JSON uses camelCase keys (username/email/passwordHash)
        // while the record's positional properties are PascalCase. Default JSON
        // options are case-sensitive, so without this option every field would
        // deserialise to null and Npgsql would blow up on the next RegisterUserAsync
        // call with "Parameter 'p_username' must have ... its Value set."
        var intent = JsonSerializer.Deserialize<RegistrationIntent>(
            intentJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (intent == null
            || string.IsNullOrWhiteSpace(intent.Username)
            || string.IsNullOrWhiteSpace(intent.Email)
            || string.IsNullOrWhiteSpace(intent.PasswordHash))
            return StatusCode(500, ApiResponse.Fail("Malformed registration intent"));

        // Now actually create the user row.
        var (userId, error) = await _postgres.RegisterUserAsync(
            intent.Username, intent.Email, intent.PasswordHash);

        if (error != null)
        {
            // Possible race: someone grabbed the email/username during
            // the OTP window. Surface the same error the form pre-check
            // would have given.
            var message = error switch
            {
                "EMAIL_TAKEN"    => "This email was just taken by another signup. Please try a different one.",
                "USERNAME_TAKEN" => "This username was just taken. Please choose another.",
                _                => "Registration failed"
            };
            return Conflict(ApiResponse.Fail(message));
        }

        // OTP good + user created — burn the staged data.
        await _redis.DeleteRegistrationOtpAsync(req.Email);
        await _redis.DeleteRegistrationIntentAsync(req.Email);

        // Flip the email-verified flag (default insert is false).
        await _postgres.MarkEmailVerifiedAsync(userId!.Value);

        // Apply OTP-verified trust event (+10). The proc handles the
        // clamp + ledger insert + denormalised trust_score update.
        short newTrust = 60;
        try
        {
            var (afterScore, _) = await _postgres.ApplyTrustEventAsync(
                userId: userId.Value,
                eventType: TrustEventType.OtpVerified,
                delta: (short)TrustDeltas.OtpVerified,
                reason: "Email OTP verified at registration",
                refSource: "auth"
            );
            newTrust = afterScore;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply OTP-verified trust event for {UserId}", userId);
        }

        await _postgres.MarkUserActiveDayAsync(userId.Value);

        // Mint JWT
        var token = _jwt.GenerateToken(
            userId: userId.Value,
            username: intent.Username,
            isGuest: false,
            trustScore: newTrust,
            isEmailVerified: true,
            ageVerified: false
        );

        await _redis.SetSessionAsync(userId.Value.ToString(), new
        {
            userId = userId.Value.ToString(),
            username = intent.Username,
            isGuest = false
        });
        await _redis.SetUserOnlineAsync(userId.Value.ToString());

        // Welcome email (fire and forget)
        var emailForBg = req.Email;
        var usernameForBg = intent.Username;
        _ = Task.Run(async () =>
        {
            try { await _email.SendWelcomeEmailAsync(emailForBg, usernameForBg); }
            catch (Exception ex) { _logger.LogWarning(ex, "Welcome email failed"); }
        });

        return Ok(ApiResponse<object>.Ok(new
        {
            token,
            userId = userId.Value.ToString(),
            username = intent.Username,
            trustScore = newTrust,
            isEmailVerified = true,
            ageVerified = false
        }, "Email verified and account created."));
    }

    // Deserialised shape of the Redis-staged registration intent.
    private record RegistrationIntent(string Username, string Email, string PasswordHash);

    // ============================================================
    //  POST /api/auth/google
    //  Sign-in (or auto-register) with a Google ID token from the
    //  @react-oauth/google button. Google verifies the email on its
    //  side, so we skip OTP entirely for these accounts.
    //
    //  Flow:
    //   1. Verify the ID token against our Google client ID.
    //   2. Look up the user by email.
    //      - exists → mint JWT, return.
    //      - new    → auto-create with random unusable password hash,
    //                 generate a guest-style username from the Google name,
    //                 mark email_verified=true, apply +10 OTP-verified-equivalent
    //                 trust delta (Google's verification is at least as strong).
    // ============================================================
    [HttpPost("google")]
    [AllowAnonymous]
    public async Task<IActionResult> GoogleSignIn([FromBody] GoogleSignInRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Credential))
            return BadRequest(ApiResponse.Fail("Missing Google credential"));

        // Verify the ID token. The audience must match the OAuth client
        // ID we configured in Google Cloud Console; otherwise anyone with
        // a Google token (from any app) could authenticate.
        var clientId = _config["Google:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId) || clientId.StartsWith("YOUR_"))
            return StatusCode(503, ApiResponse.Fail("Google sign-in is not configured on the server."));

        Google.Apis.Auth.GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await Google.Apis.Auth.GoogleJsonWebSignature.ValidateAsync(
                req.Credential,
                new Google.Apis.Auth.GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = new[] { clientId }
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google token validation failed");
            return Unauthorized(ApiResponse.Fail("Invalid Google credential"));
        }

        if (string.IsNullOrWhiteSpace(payload.Email))
            return BadRequest(ApiResponse.Fail("Google account is missing an email"));
        if (!payload.EmailVerified)
            return BadRequest(ApiResponse.Fail("Your Google email is not verified yet."));

        // Step 1 — already-registered user? Just log them in.
        var existing = await _postgres.GetUserAuthByEmailAsync(payload.Email);
        if (existing != null)
        {
            if (string.Equals(existing.Status, "banned", StringComparison.OrdinalIgnoreCase))
                return StatusCode(403, ApiResponse.Fail("Your account has been banned"));
            if (string.Equals(existing.Status, "suspended", StringComparison.OrdinalIgnoreCase))
                return StatusCode(403, ApiResponse.Fail("Your account is suspended"));

            await _postgres.MarkUserActiveDayAsync(existing.UserId);
            // If the email wasn't verified before (legacy account), Google has now done it.
            if (!existing.IsEmailVerified)
            {
                try { await _postgres.MarkEmailVerifiedAsync(existing.UserId); } catch { /* best-effort */ }
            }

            var loginToken = _jwt.GenerateToken(
                userId: existing.UserId,
                username: existing.Username,
                isGuest: false,
                trustScore: existing.TrustScore,
                isEmailVerified: true,
                ageVerified: existing.AgeVerified);

            await _redis.SetSessionAsync(existing.UserId.ToString(), new
            {
                userId = existing.UserId.ToString(),
                username = existing.Username,
                isGuest = false
            });
            await _redis.SetUserOnlineAsync(existing.UserId.ToString());

            return Ok(ApiResponse<object>.Ok(new
            {
                token = loginToken,
                userId = existing.UserId.ToString(),
                username = existing.Username,
                trustScore = existing.TrustScore,
                isEmailVerified = true,
                ageVerified = existing.AgeVerified,
                isNew = false
            }, "Signed in with Google"));
        }

        // Step 2 — brand new user. Auto-generate username from name/email.
        var baseName = !string.IsNullOrWhiteSpace(payload.Name)
            ? new string(payload.Name.Where(char.IsLetterOrDigit).ToArray())
            : payload.Email.Split('@')[0];
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "User";
        var username = baseName + Random.Shared.Next(100, 999);

        // Random unusable password — they sign in via Google. If they later
        // want a password they can use the "forgot password" flow.
        var randomPwd = PasswordHasher.Hash(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));

        var (newId, error) = await _postgres.RegisterUserAsync(username, payload.Email, randomPwd);
        if (error != null)
        {
            // Username collision is the most likely cause — retry once with extra entropy.
            if (error == "USERNAME_TAKEN")
            {
                username = baseName + Random.Shared.Next(1000, 99999);
                (newId, error) = await _postgres.RegisterUserAsync(username, payload.Email, randomPwd);
            }
            if (error != null)
            {
                _logger.LogWarning("Google sign-in: register failed with {Error}", error);
                return Conflict(ApiResponse.Fail(error switch
                {
                    "EMAIL_TAKEN" => "This email is already linked to a different account.",
                    _ => "Could not create your account."
                }));
            }
        }

        await _postgres.MarkEmailVerifiedAsync(newId!.Value);

        // Equivalent to OTP-verified trust delta — Google verified the email.
        short trust = 60;
        try
        {
            var (after, _) = await _postgres.ApplyTrustEventAsync(
                userId: newId.Value,
                eventType: TrustEventType.OtpVerified,
                delta: (short)TrustDeltas.OtpVerified,
                reason: "Google-verified email at registration",
                refSource: "auth");
            trust = after;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Trust delta failed for Google signup"); }

        await _postgres.MarkUserActiveDayAsync(newId.Value);

        var token = _jwt.GenerateToken(
            userId: newId.Value,
            username: username,
            isGuest: false,
            trustScore: trust,
            isEmailVerified: true,
            ageVerified: false);

        await _redis.SetSessionAsync(newId.Value.ToString(), new
        {
            userId = newId.Value.ToString(),
            username,
            isGuest = false
        });
        await _redis.SetUserOnlineAsync(newId.Value.ToString());

        // Fire-and-forget welcome email — uses the same template as OTP-verified.
        _ = Task.Run(async () =>
        {
            try { await _email.SendWelcomeEmailAsync(payload.Email, username); }
            catch (Exception ex) { _logger.LogWarning(ex, "Welcome email failed"); }
        });

        return Ok(ApiResponse<object>.Ok(new
        {
            token,
            userId = newId.Value.ToString(),
            username,
            trustScore = trust,
            isEmailVerified = true,
            ageVerified = false,
            isNew = true
        }, "Account created with Google"));
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

        // Belt-and-braces: with the new OTP-first flow this should never
        // trigger for new accounts, but legacy rows that were created
        // under the old "register-then-verify" code still might be
        // unverified. Force them through OTP before letting them in.
        if (!record.IsEmailVerified)
            return StatusCode(403, ApiResponse.Fail(
                "Please verify your email before signing in."));

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
    //  Reissues an OTP for an in-progress registration. Requires that
    //  a registration intent still be staged in Redis — otherwise the
    //  caller hasn't started the flow and shouldn't receive an OTP.
    // ============================================================
    [HttpPost("resend-otp")]
    [AllowAnonymous]
    public async Task<IActionResult> ResendOtp([FromBody] ResendOtpRequest req)
    {
        var intent = await _redis.GetRegistrationIntentAsync(req.Email);
        if (intent == null)
            return BadRequest(ApiResponse.Fail(
                "Your registration session expired. Please register again."));

        var allowed = await _redis.TryAllowOtpRequestAsync(req.Email);
        if (!allowed)
            return StatusCode(429, ApiResponse.Fail(
                "Too many requests. Try again in 15 minutes."));

        var otpCode = GenerateOtpCode();
        await _redis.SetRegistrationOtpAsync(req.Email, otpCode);

        // Fire-and-forget — same rationale as Register. Respond immediately.
        var bgEmail = req.Email;
        var bgCode = otpCode;
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await _email.SendOtpEmailAsync(bgEmail, bgCode, "email_verification");
                _logger.Log(ok ? LogLevel.Information : LogLevel.Warning,
                    "Registration OTP resend {Status} for {Email}", ok ? "OK" : "FAILED", bgEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background OTP resend threw for {Email}", bgEmail);
            }
        });

        return Ok(ApiResponse.Ok("OTP resent successfully"));
    }

    // ============================================================
    //  POST /api/auth/forgot-password
    //  Always responds 200, regardless of whether the email exists.
    //  Prevents account-enumeration via timing or status code.
    // ============================================================
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            return BadRequest(ApiResponse.Fail("Invalid email"));

        // Rate-limit same as OTP — 3 per 15 min.
        var allowed = await _redis.TryAllowOtpRequestAsync(req.Email);
        if (!allowed)
        {
            // Still return a generic OK to avoid leaking the throttle.
            return Ok(ApiResponse.Ok("If that account exists, we've sent a reset code."));
        }

        var record = await _postgres.GetUserAuthByEmailAsync(req.Email);
        if (record != null && record.IsEmailVerified)
        {
            var code = GenerateOtpCode();
            await _redis.SetPasswordResetCodeAsync(req.Email, code);
            // Fire-and-forget (see Register). The endpoint must respond in
            // constant time anyway to avoid account-enumeration leaks via
            // timing, so blocking on SMTP would be a bug regardless of
            // performance considerations.
            var bgEmail = req.Email;
            var bgCode = code;
            _ = Task.Run(async () =>
            {
                try
                {
                    var ok = await _email.SendOtpEmailAsync(bgEmail, bgCode, "password_reset");
                    _logger.Log(ok ? LogLevel.Information : LogLevel.Warning,
                        "Password reset {Status} for {Email}", ok ? "OK" : "FAILED", bgEmail);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Password reset email failed");
                }
            });
        }

        // Generic message regardless.
        return Ok(ApiResponse.Ok("If that account exists, we've sent a reset code."));
    }

    // ============================================================
    //  POST /api/auth/reset-password
    //  Verify code + set a fresh BCrypt hash.
    // ============================================================
    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse.Fail("Invalid request"));
        if (req.NewPassword.Length < 8)
            return BadRequest(ApiResponse.Fail("Password must be at least 8 characters"));

        var stored = await _redis.GetPasswordResetCodeAsync(req.Email);
        if (stored == null)
            return BadRequest(ApiResponse.Fail("Reset code has expired. Request a new one."));
        if (!string.Equals(stored, req.Code, StringComparison.Ordinal))
            return BadRequest(ApiResponse.Fail("Invalid reset code"));

        var record = await _postgres.GetUserAuthByEmailAsync(req.Email);
        if (record == null)
            return BadRequest(ApiResponse.Fail("Account not found"));

        var newHash = PasswordHasher.Hash(req.NewPassword);
        await _postgres.UpdatePasswordHashAsync(record.UserId, newHash);
        await _redis.DeletePasswordResetCodeAsync(req.Email);

        // Best-effort: invalidate any active session so other devices are kicked.
        try { await _redis.DeleteSessionAsync(record.UserId.ToString()); } catch { /* ignore */ }

        _logger.LogInformation("Password reset for user {UserId}", record.UserId);

        return Ok(ApiResponse.Ok("Password updated. Please sign in with your new password."));
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
            // Fire-and-forget — same pattern as Register / Resend.
            var bgEmail = req.Email;
            var bgCode = otpCode;
            _ = Task.Run(async () =>
            {
                try
                {
                    var ok = await _email.SendOtpEmailAsync(bgEmail, bgCode, "email_verification");
                    _logger.Log(ok ? LogLevel.Information : LogLevel.Warning,
                        "Upgrade OTP {Status} for {Email}", ok ? "OK" : "FAILED", bgEmail);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background upgrade OTP send threw for {Email}", bgEmail);
                }
            });
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

public record ForgotPasswordRequest(
    [System.ComponentModel.DataAnnotations.Required] string Email
);

public record ResetPasswordRequest(
    [System.ComponentModel.DataAnnotations.Required] string Email,
    [System.ComponentModel.DataAnnotations.Required] string Code,
    [System.ComponentModel.DataAnnotations.Required] string NewPassword
);

public record GoogleSignInRequest(
    [System.ComponentModel.DataAnnotations.Required] string Credential
);