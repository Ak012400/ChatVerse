using System.Text;
using System.Text.Json;

namespace ChatVerse.Infrastructure.ExternalServices.Email;

/// <summary>
/// Brevo (formerly Sendinblue) SMTP/API email service.
/// Used for OTP emails, welcome emails, and notifications.
/// </summary>
public class BrevoEmailService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _senderEmail;
    private readonly string _senderName;
    private readonly ILogger<BrevoEmailService> _logger;

    public BrevoEmailService(
        HttpClient http,
        IConfiguration config,
        ILogger<BrevoEmailService> logger)
    {
        _http = http;
        _apiKey = config["Brevo:ApiKey"]!;
        _senderEmail = config["Brevo:SenderEmail"]!;
        _senderName = config["Brevo:SenderName"]!;
        _logger = logger;

        _http.BaseAddress = new Uri("https://api.brevo.com/v3/");
        _http.DefaultRequestHeaders.Add("api-key", _apiKey);
        _http.DefaultRequestHeaders.Add("accept", "application/json");
    }

    // ============================================================
    //  SendOtpEmailAsync
    //  Main OTP email — verification + password reset
    // ============================================================
    public async Task<bool> SendOtpEmailAsync(string toEmail, string otpCode, string purpose)
    {
        var subject = purpose switch
        {
            "email_verification" => "Verify your ChatVerse account",
            "password_reset" => "Reset your ChatVerse password",
            _ => "Your ChatVerse OTP"
        };

        var bodyHtml = BuildOtpEmailHtml(otpCode, purpose);

        return await SendEmailAsync(toEmail, subject, bodyHtml);
    }

    // ============================================================
    //  SendWelcomeEmailAsync
    //  Sent after successful email verification
    // ============================================================
    public async Task<bool> SendWelcomeEmailAsync(string toEmail, string username)
    {
        var subject = $"Welcome to ChatVerse, {username}!";
        var bodyHtml = BuildWelcomeEmailHtml(username);
        return await SendEmailAsync(toEmail, subject, bodyHtml);
    }

    // ============================================================
    //  Core send method — calls Brevo transactional email API
    // ============================================================
    private async Task<bool> SendEmailAsync(string toEmail, string subject, string htmlContent)
    {
        try
        {
            var payload = new
            {
                sender = new { name = _senderName, email = _senderEmail },
                to = new[] { new { email = toEmail } },
                subject,
                htmlContent
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync("smtp/email", content);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Email sent to {Email} — Subject: {Subject}", toEmail, subject);
                return true;
            }

            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Brevo email failed: {StatusCode} — {Error}",
                response.StatusCode, error);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while sending email to {Email}", toEmail);
            return false;
        }
    }

    // ============================================================
    //  Email Templates
    // ============================================================
    private static string BuildOtpEmailHtml(string otpCode, string purpose)
    {
        var heading = purpose == "password_reset"
            ? "Reset Your Password"
            : "Verify Your Email";

        var message = purpose == "password_reset"
            ? "You requested a password reset. Use the code below:"
            : "Thanks for signing up! Use the code below to verify your email:";

        return $"""
        <!DOCTYPE html>
        <html>
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
        </head>
        <body style="margin:0;padding:0;background:#0f0f0f;font-family:'Segoe UI',Arial,sans-serif;">
          <table width="100%" cellpadding="0" cellspacing="0">
            <tr>
              <td align="center" style="padding:40px 20px;">
                <table width="480" cellpadding="0" cellspacing="0"
                       style="background:#1a1a1a;border-radius:12px;overflow:hidden;">

                  <!-- Header -->
                  <tr>
                    <td style="background:linear-gradient(135deg,#6366f1,#8b5cf6);
                               padding:32px;text-align:center;">
                      <h1 style="color:#fff;margin:0;font-size:24px;font-weight:700;">
                        💬 ChatVerse
                      </h1>
                    </td>
                  </tr>

                  <!-- Body -->
                  <tr>
                    <td style="padding:40px 32px;">
                      <h2 style="color:#fff;margin:0 0 12px;font-size:20px;">{heading}</h2>
                      <p style="color:#9ca3af;margin:0 0 32px;font-size:15px;line-height:1.6;">
                        {message}
                      </p>

                      <!-- OTP Code Box -->
                      <div style="background:#0f0f0f;border:2px solid #6366f1;border-radius:8px;
                                  padding:24px;text-align:center;margin-bottom:32px;">
                        <span style="font-size:40px;font-weight:700;color:#6366f1;
                                     letter-spacing:12px;font-family:monospace;">
                          {otpCode}
                        </span>
                      </div>

                      <p style="color:#6b7280;font-size:13px;margin:0;">
                        This code expires in <strong style="color:#9ca3af;">10 minutes</strong>.
                        If you didn't request this, ignore this email.
                      </p>
                    </td>
                  </tr>

                  <!-- Footer -->
                  <tr>
                    <td style="padding:20px 32px;border-top:1px solid #2d2d2d;text-align:center;">
                      <p style="color:#4b5563;font-size:12px;margin:0;">
                        © 2026 ChatVerse. All rights reserved.
                      </p>
                    </td>
                  </tr>

                </table>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
    }

    private static string BuildWelcomeEmailHtml(string username)
    {
        return $"""
        <!DOCTYPE html>
        <html>
        <head><meta charset="utf-8"></head>
        <body style="margin:0;padding:0;background:#0f0f0f;font-family:'Segoe UI',Arial,sans-serif;">
          <table width="100%" cellpadding="0" cellspacing="0">
            <tr>
              <td align="center" style="padding:40px 20px;">
                <table width="480" cellpadding="0" cellspacing="0"
                       style="background:#1a1a1a;border-radius:12px;overflow:hidden;">

                  <tr>
                    <td style="background:linear-gradient(135deg,#6366f1,#8b5cf6);
                               padding:32px;text-align:center;">
                      <h1 style="color:#fff;margin:0;font-size:24px;">💬 ChatVerse</h1>
                    </td>
                  </tr>

                  <tr>
                    <td style="padding:40px 32px;">
                      <h2 style="color:#fff;margin:0 0 16px;">Welcome, {username}! 🎉</h2>
                      <p style="color:#9ca3af;margin:0 0 24px;font-size:15px;line-height:1.6;">
                        Your account is verified and ready. Start chatting, meet new people,
                        and explore public rooms.
                      </p>
                      <p style="color:#9ca3af;font-size:14px;line-height:1.6;">
                        🔒 Keep your account secure<br>
                        💬 Join public chat rooms<br>
                        🎯 Build your trust score<br>
                        📹 Video chat after age verification
                      </p>
                    </td>
                  </tr>

                  <tr>
                    <td style="padding:20px 32px;border-top:1px solid #2d2d2d;text-align:center;">
                      <p style="color:#4b5563;font-size:12px;margin:0;">
                        © 2026 ChatVerse. All rights reserved.
                      </p>
                    </td>
                  </tr>

                </table>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
    }
}