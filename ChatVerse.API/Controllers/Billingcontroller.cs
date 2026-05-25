using ChatVerse.API.Extensions;
using ChatVerse.Infrastructure.ExternalServices.Razorpay;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ChatVerse.API.Controllers;

[ApiController]
[Route("api/billing")]
public class BillingController : ControllerBase
{
    private readonly PostgresProcService _postgres;
    private readonly RazorpayService _razorpay;
    private readonly ILogger<BillingController> _logger;

    public BillingController(
        PostgresProcService postgres,
        RazorpayService razorpay,
        ILogger<BillingController> logger)
    {
        _postgres = postgres;
        _razorpay = razorpay;
        _logger = logger;
    }

    // ============================================================
    //  GET /api/billing/plans
    //  Available subscription plans + prices
    //  Public — no auth needed
    // ============================================================
    [HttpGet("plans")]
    [AllowAnonymous]
    public IActionResult GetPlans()
    {
        var plans = new[]
        {
            new {
                id          = "basic",
                name        = "Basic",
                priceInr    = 199,
                pricePaise  = 19900,
                features    = new[] {
                    "Access to all public rooms",
                    "Trust score boost (+15)",
                    "Priority support",
                    "Custom avatar"
                }
            },
            new {
                id          = "pro",
                name        = "Pro",
                priceInr    = 499,
                pricePaise  = 49900,
                features    = new[] {
                    "All Basic features",
                    "18+ room access (after age verification)",
                    "Extended video chat",
                    "Message history 90 days",
                    "Pro badge"
                }
            },
            new {
                id          = "elite",
                name        = "Elite",
                priceInr    = 999,
                pricePaise  = 99900,
                features    = new[] {
                    "All Pro features",
                    "Elite badge",
                    "Admin-level trust score",
                    "Private room creation",
                    "Priority moderation review"
                }
            }
        };

        return Ok(ApiResponse<object>.Ok(plans));
    }

    // ============================================================
    //  POST /api/billing/order
    //  Create Razorpay order — returns orderId for frontend SDK
    // ============================================================
    [HttpPost("order")]
    [Authorize]
    public async Task<IActionResult> CreateOrder([FromBody] CreateOrderRequest req)
    {
        var userId = JwtService.GetUserId(User);
        var validPlans = new[] { "basic", "pro", "elite" };

        if (!validPlans.Contains(req.PlanType.ToLower()))
            return BadRequest(ApiResponse.Fail("Invalid plan. Choose: basic, pro, elite"));

        // Create Razorpay order
        var orderResult = await _razorpay.CreateOrderAsync(req.PlanType, userId.ToString());
        if (!orderResult.Success)
            return StatusCode(500, ApiResponse.Fail(orderResult.Error ?? "Order creation failed"));

        // Save to PostgreSQL
        var startsAt = DateTime.UtcNow;
        var endsAt = startsAt.AddMonths(1);

        var (subId, payId, error) = await _postgres.CreateSubscriptionAsync(
            userId,
            req.PlanType,
            orderResult.OrderId,
            orderResult.AmountPaise,
            startsAt,
            endsAt
        );

        if (error != null)
            return StatusCode(500, ApiResponse.Fail("Failed to create subscription record"));

        _logger.LogInformation("Order created for user {UserId} — Plan: {Plan}, OrderId: {OrderId}",
            userId, req.PlanType, orderResult.OrderId);

        return Ok(ApiResponse<object>.Ok(new
        {
            orderId = orderResult.OrderId,
            keyId = orderResult.KeyId,         // Razorpay Key ID for frontend
            amountPaise = orderResult.AmountPaise,
            currency = orderResult.Currency,
            subscriptionId = subId?.ToString(),
            planType = req.PlanType
        }));
    }

    // ============================================================
    //  POST /api/billing/verify-payment
    //  Frontend calls after Razorpay payment success
    //  Verifies signature + activates subscription
    // ============================================================
    [HttpPost("verify-payment")]
    [Authorize]
    public async Task<IActionResult> VerifyPayment([FromBody] VerifyPaymentRequest req)
    {
        // Verify Razorpay signature
        var isValid = _razorpay.VerifyPaymentSignature(
            req.OrderId, req.PaymentId, req.Signature);

        if (!isValid)
        {
            _logger.LogWarning("Invalid payment signature for order {OrderId}", req.OrderId);
            return BadRequest(ApiResponse.Fail("Payment verification failed — invalid signature"));
        }

        // Activate subscription in PostgreSQL
        var (success, userId, error) = await _postgres.ActivateSubscriptionAsync(
            req.OrderId, req.PaymentId);

        if (!success)
        {
            _logger.LogError("Subscription activation failed for order {OrderId} — {Error}",
                req.OrderId, error);
            return StatusCode(500, ApiResponse.Fail("Subscription activation failed"));
        }

        _logger.LogInformation("Payment verified + subscription activated — User: {UserId}, Order: {OrderId}",
            userId, req.OrderId);

        return Ok(ApiResponse.Ok("Payment successful! Subscription activated."));
    }

    // ============================================================
    //  POST /api/billing/webhook
    //  Razorpay webhook — backup activation path
    //  HMAC signature verified before processing
    // ============================================================
    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook()
    {
        // Read raw body for signature verification
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync();
        Request.Body.Position = 0;

        // Get Razorpay signature from header
        var signature = Request.Headers["X-Razorpay-Signature"].FirstOrDefault();
        if (string.IsNullOrEmpty(signature))
            return BadRequest("Missing signature header");

        // Verify signature
        if (!_razorpay.VerifyWebhookSignature(rawBody, signature))
        {
            _logger.LogWarning("Webhook signature verification failed");
            return Unauthorized("Invalid webhook signature");
        }

        // Parse event
        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(rawBody);
            var eventType = payload.GetProperty("event").GetString();

            _logger.LogInformation("Razorpay webhook received: {Event}", eventType);

            if (eventType == "payment.captured")
            {
                var paymentEntity = payload
                    .GetProperty("payload")
                    .GetProperty("payment")
                    .GetProperty("entity");

                var orderId = paymentEntity.GetProperty("order_id").GetString()!;
                var paymentId = paymentEntity.GetProperty("id").GetString()!;

                var (success, userId, error) = await _postgres.ActivateSubscriptionAsync(
                    orderId, paymentId);

                if (success)
                    _logger.LogInformation("Webhook activated subscription for user {UserId}", userId);
                else
                    _logger.LogWarning("Webhook activation failed: {Error}", error);
            }
            else if (eventType == "payment.failed")
            {
                var orderId = payload
                    .GetProperty("payload")
                    .GetProperty("payment")
                    .GetProperty("entity")
                    .GetProperty("order_id").GetString()!;

                // Mark payment failed
                _logger.LogWarning("Payment failed for order {OrderId}", orderId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook processing error");
        }

        // Always return 200 to Razorpay — prevents retries
        return Ok();
    }

    // ============================================================
    //  GET /api/billing/subscription
    //  Current user's active subscription
    // ============================================================
    [HttpGet("subscription")]
    [Authorize]
    public async Task<IActionResult> GetSubscription()
    {
        var userId = JwtService.GetUserId(User);
        var conn = await GetConnectionAsync();

        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT * FROM billing.usp_get_active_subscription(@p_user_id)", conn);
        cmd.Parameters.AddWithValue("p_user_id", userId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return Ok(ApiResponse<object>.Ok(new { plan = "free", status = "no_subscription" }));

        return Ok(ApiResponse<object>.Ok(new
        {
            subscriptionId = reader.GetGuid(0).ToString(),
            planType = reader.GetString(1),
            status = reader.GetString(2),
            startsAt = reader.GetDateTime(3).ToString("o"),
            endsAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4).ToString("o"),
            amountPaise = reader.IsDBNull(6) ? 0 : reader.GetInt32(6)
        }));
    }

    // ── Private helper ────────────────────────────────────────
    private async Task<Npgsql.NpgsqlConnection> GetConnectionAsync()
    {
        var conn = (Npgsql.NpgsqlConnection)HttpContext.RequestServices
            .GetRequiredService<Infrastructure.Persistence.PostgreSQL.ChatVerseDbContext>()
            .Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();
        return conn;
    }
}

// ── Request DTOs ──────────────────────────────────────────────
public record CreateOrderRequest(
    [System.ComponentModel.DataAnnotations.Required] string PlanType
);

public record VerifyPaymentRequest(
    [System.ComponentModel.DataAnnotations.Required] string OrderId,
    [System.ComponentModel.DataAnnotations.Required] string PaymentId,
    [System.ComponentModel.DataAnnotations.Required] string Signature
);