using System.Text;
using System.Text.Json;

namespace ChatVerse.Infrastructure.ExternalServices.Email;

/// <summary>
/// Brevo (formerly Sendinblue) transactional email service.
///
/// Templates are intentionally LIGHT-themed and minimal — gradient-heavy
/// dark emails get flagged by spam filters (especially Outlook/Yahoo),
/// and Gmail/iOS render light HTML predictably across both color modes.
///
/// We also send a plain-text alternative alongside the HTML — Gmail
/// computes its spam score partly off the text/html ratio, and a
/// matching plain version raises deliverability significantly.
/// </summary>
public class BrevoEmailService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _senderEmail;
    private readonly string _senderName;
    private readonly ILogger<BrevoEmailService> _logger;

    // Footer constants — surface a real-looking org address to satisfy
    // CAN-SPAM / GDPR expectations even though we're transactional-only.
    private const string CompanyName = "ChatVerse";
    private const string CompanyTagline = "Conversations, on your terms.";
    private const string SupportEmail = "support@chatverse.app";
    private const string WebsiteUrl = "https://chatverse.app";

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
    //  Public surface
    // ============================================================
    public async Task<bool> SendOtpEmailAsync(string toEmail, string otpCode, string purpose)
    {
        var (subject, heading, lead) = purpose switch
        {
            "email_verification" => (
                "Your ChatVerse verification code",
                "Verify your email",
                "Enter the code below in your browser to finish creating your ChatVerse account."),
            "password_reset" => (
                "Your ChatVerse password reset code",
                "Reset your password",
                "Use the code below to set a new password. If you didn't request this, you can safely ignore this message."),
            _ => (
                "Your ChatVerse security code",
                "Security code",
                "Use the code below to continue.")
        };

        var html = BuildOtpHtml(heading, lead, otpCode);
        var text = BuildOtpText(heading, lead, otpCode);
        return await SendEmailAsync(toEmail, subject, html, text);
    }

    public async Task<bool> SendWelcomeEmailAsync(string toEmail, string username)
    {
        var subject = $"Welcome to ChatVerse, {username}";
        var html = BuildWelcomeHtml(username);
        var text = BuildWelcomeText(username);
        return await SendEmailAsync(toEmail, subject, html, text);
    }

    // ============================================================
    //  Core send (HTML + plain text + reply-to)
    // ============================================================
    private async Task<bool> SendEmailAsync(
        string toEmail, string subject, string htmlContent, string textContent)
    {
        try
        {
            var payload = new
            {
                sender = new { name = _senderName, email = _senderEmail },
                to = new[] { new { email = toEmail } },
                replyTo = new { email = SupportEmail, name = $"{CompanyName} Support" },
                subject,
                htmlContent,
                textContent,
                // Brevo respects List-Unsubscribe automatically when this is set,
                // and Gmail uses it to render the unsubscribe link → trust signal.
                headers = new Dictionary<string, string>
                {
                    ["X-Mailer"] = "ChatVerse-Transactional",
                    ["List-Unsubscribe"] = $"<mailto:{SupportEmail}?subject=Unsubscribe>"
                }
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
    //  HTML template — OTP / verification / password reset
    //  Light theme, system fonts, single accent colour, inline CSS.
    // ============================================================
    private static string BuildOtpHtml(string heading, string lead, string otpCode)
    {
        var year = DateTime.UtcNow.Year;
        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <meta name="x-apple-disable-message-reformatting">
          <meta name="color-scheme" content="light">
          <meta name="supported-color-schemes" content="light">
          <title>{{heading}}</title>
        </head>
        <body style="margin:0;padding:0;background:#f4f5f7;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:#1f2328;">
          <!-- Preheader (hidden, shows in inbox preview) -->
          <div style="display:none;max-height:0;overflow:hidden;mso-hide:all;">
            Your {{CompanyName}} verification code — valid for 10 minutes.
          </div>

          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:#f4f5f7;">
            <tr>
              <td align="center" style="padding:40px 16px;">
                <table role="presentation" width="520" cellpadding="0" cellspacing="0" border="0"
                       style="max-width:520px;width:100%;background:#ffffff;border:1px solid #e6e8eb;border-radius:12px;overflow:hidden;">

                  <!-- Header -->
                  <tr>
                    <td style="padding:24px 32px;border-bottom:1px solid #eef0f2;">
                      <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0">
                        <tr>
                          <td>
                            <span style="display:inline-block;width:28px;height:28px;border-radius:8px;background:#4f46e5;vertical-align:middle;text-align:center;line-height:28px;color:#ffffff;font-weight:700;font-size:14px;">C</span>
                            <span style="display:inline-block;margin-left:10px;vertical-align:middle;font-weight:600;font-size:16px;letter-spacing:-0.01em;color:#1f2328;">{{CompanyName}}</span>
                          </td>
                          <td align="right" style="font-size:12px;color:#6a737d;">Transactional</td>
                        </tr>
                      </table>
                    </td>
                  </tr>

                  <!-- Body -->
                  <tr>
                    <td style="padding:32px;">
                      <h1 style="margin:0 0 12px;font-size:22px;line-height:1.3;font-weight:600;letter-spacing:-0.01em;color:#1f2328;">{{heading}}</h1>
                      <p style="margin:0 0 28px;font-size:15px;line-height:1.6;color:#4a5159;">{{lead}}</p>

                      <!-- OTP block -->
                      <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="margin-bottom:28px;">
                        <tr>
                          <td align="center" style="background:#f8f9fb;border:1px solid #e6e8eb;border-radius:10px;padding:22px;">
                            <div style="font-family:'SF Mono','Consolas','Liberation Mono',Menlo,monospace;font-size:32px;font-weight:600;letter-spacing:10px;color:#4f46e5;">{{otpCode}}</div>
                            <div style="margin-top:8px;font-size:12px;color:#6a737d;">Code expires in 10 minutes</div>
                          </td>
                        </tr>
                      </table>

                      <p style="margin:0;font-size:13px;line-height:1.6;color:#6a737d;">
                        For your security, never share this code with anyone. {{CompanyName}} staff will never ask for your code.
                      </p>
                    </td>
                  </tr>

                  <!-- Footer -->
                  <tr>
                    <td style="padding:20px 32px;border-top:1px solid #eef0f2;background:#fafbfc;">
                      <p style="margin:0 0 6px;font-size:12px;line-height:1.5;color:#6a737d;">
                        You're receiving this because someone (hopefully you) tried to sign in to {{CompanyName}}.
                        If this wasn't you, you can safely ignore this email — your account stays as it was.
                      </p>
                      <p style="margin:8px 0 0;font-size:11px;color:#9097a0;">
                        © {{year}} {{CompanyName}} · {{CompanyTagline}}<br>
                        Questions? Reply to this email or write to <a href="mailto:{{SupportEmail}}" style="color:#4f46e5;text-decoration:none;">{{SupportEmail}}</a>
                      </p>
                    </td>
                  </tr>

                </table>

                <p style="margin:16px 0 0;font-size:11px;color:#9097a0;text-align:center;">
                  <a href="{{WebsiteUrl}}" style="color:#9097a0;text-decoration:none;">{{WebsiteUrl}}</a>
                </p>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
    }

    // Plain-text version is used by Gmail/Outlook to compute spam score
    // and is what shows in text-only clients. Keep it mirror-of-HTML.
    private static string BuildOtpText(string heading, string lead, string otpCode)
    {
        var year = DateTime.UtcNow.Year;
        return $"""
        {CompanyName}

        {heading}

        {lead}

        Your code: {otpCode}
        This code expires in 10 minutes.

        For your security, never share this code with anyone.
        {CompanyName} staff will never ask for your code.

        ────────────────────────────────────────
        You're receiving this because someone tried to sign in to {CompanyName}.
        If this wasn't you, you can safely ignore this email.

        Questions? Write to {SupportEmail}
        © {year} {CompanyName} · {CompanyTagline}
        {WebsiteUrl}
        """;
    }

    // ============================================================
    //  Welcome template
    // ============================================================
    private static string BuildWelcomeHtml(string username)
    {
        var year = DateTime.UtcNow.Year;
        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <meta name="color-scheme" content="light">
          <title>Welcome to {{CompanyName}}</title>
        </head>
        <body style="margin:0;padding:0;background:#f4f5f7;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:#1f2328;">
          <div style="display:none;max-height:0;overflow:hidden;mso-hide:all;">
            Your {{CompanyName}} account is ready, {{username}}. Here's what's inside.
          </div>

          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:#f4f5f7;">
            <tr>
              <td align="center" style="padding:40px 16px;">
                <table role="presentation" width="520" cellpadding="0" cellspacing="0" border="0"
                       style="max-width:520px;width:100%;background:#ffffff;border:1px solid #e6e8eb;border-radius:12px;overflow:hidden;">

                  <tr>
                    <td style="padding:24px 32px;border-bottom:1px solid #eef0f2;">
                      <span style="display:inline-block;width:28px;height:28px;border-radius:8px;background:#4f46e5;vertical-align:middle;text-align:center;line-height:28px;color:#ffffff;font-weight:700;font-size:14px;">C</span>
                      <span style="display:inline-block;margin-left:10px;vertical-align:middle;font-weight:600;font-size:16px;letter-spacing:-0.01em;color:#1f2328;">{{CompanyName}}</span>
                    </td>
                  </tr>

                  <tr>
                    <td style="padding:32px;">
                      <h1 style="margin:0 0 12px;font-size:22px;line-height:1.3;font-weight:600;letter-spacing:-0.01em;color:#1f2328;">Welcome, {{username}}.</h1>
                      <p style="margin:0 0 24px;font-size:15px;line-height:1.6;color:#4a5159;">
                        Your account is verified. {{CompanyName}} is an anonymous-friendly space to chat, meet new people, and join curated communities — with moderation that actually works.
                      </p>

                      <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="margin-bottom:28px;">
                        <tr>
                          <td style="padding:12px 0;border-bottom:1px solid #eef0f2;">
                            <strong style="font-size:14px;color:#1f2328;">Join public rooms</strong>
                            <div style="font-size:13px;color:#6a737d;margin-top:2px;">Categorised, moderated, and active 24/7.</div>
                          </td>
                        </tr>
                        <tr>
                          <td style="padding:12px 0;border-bottom:1px solid #eef0f2;">
                            <strong style="font-size:14px;color:#1f2328;">Start direct messages</strong>
                            <div style="font-size:13px;color:#6a737d;margin-top:2px;">Reach friends one-to-one, with typing and read indicators.</div>
                          </td>
                        </tr>
                        <tr>
                          <td style="padding:12px 0;">
                            <strong style="font-size:14px;color:#1f2328;">Unlock video</strong>
                            <div style="font-size:13px;color:#6a737d;margin-top:2px;">Verify your age once to access 1-on-1 and group video calls.</div>
                          </td>
                        </tr>
                      </table>

                      <table role="presentation" cellpadding="0" cellspacing="0" border="0">
                        <tr>
                          <td style="background:#4f46e5;border-radius:8px;">
                            <a href="{{WebsiteUrl}}" style="display:inline-block;padding:11px 22px;font-size:14px;font-weight:500;color:#ffffff;text-decoration:none;">Open {{CompanyName}}</a>
                          </td>
                        </tr>
                      </table>
                    </td>
                  </tr>

                  <tr>
                    <td style="padding:20px 32px;border-top:1px solid #eef0f2;background:#fafbfc;">
                      <p style="margin:0 0 6px;font-size:12px;line-height:1.5;color:#6a737d;">
                        You're receiving this because you just created a {{CompanyName}} account.
                      </p>
                      <p style="margin:8px 0 0;font-size:11px;color:#9097a0;">
                        © {{year}} {{CompanyName}} · {{CompanyTagline}}<br>
                        Questions? Reply to this email or write to <a href="mailto:{{SupportEmail}}" style="color:#4f46e5;text-decoration:none;">{{SupportEmail}}</a>
                      </p>
                    </td>
                  </tr>

                </table>

                <p style="margin:16px 0 0;font-size:11px;color:#9097a0;text-align:center;">
                  <a href="{{WebsiteUrl}}" style="color:#9097a0;text-decoration:none;">{{WebsiteUrl}}</a>
                </p>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
    }

    private static string BuildWelcomeText(string username)
    {
        var year = DateTime.UtcNow.Year;
        return $"""
        {CompanyName}

        Welcome, {username}.

        Your account is verified. {CompanyName} is an anonymous-friendly space
        to chat, meet new people, and join curated communities — with moderation
        that actually works.

        • Join public rooms — categorised, moderated, active 24/7.
        • Start direct messages — typing and read indicators.
        • Unlock video — verify your age once to access 1-on-1 and group video calls.

        Open {CompanyName}: {WebsiteUrl}

        ────────────────────────────────────────
        You're receiving this because you just created a {CompanyName} account.
        Questions? Write to {SupportEmail}

        © {year} {CompanyName} · {CompanyTagline}
        """;
    }
}
