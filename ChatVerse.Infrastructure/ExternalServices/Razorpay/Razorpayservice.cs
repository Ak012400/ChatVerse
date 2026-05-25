using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatVerse.Infrastructure.ExternalServices.Razorpay;

/// <summary>
/// Razorpay payment service.
/// Creates orders + verifies webhook signatures.
/// Actual payment happens on frontend via Razorpay JS SDK.
/// </summary>
public class RazorpayService
{
    private readonly HttpClient _http;
    private readonly string _keyId;
    private readonly string _keySecret;
    private readonly string _webhookSecret;
    private readonly ILogger<RazorpayService> _logger;

    // Plan prices in paise (1 INR = 100 paise)
    public static readonly Dictionary<string, int> PlanPrices = new()
    {
        ["basic"] = 19900,    // ₹199/month
        ["pro"] = 49900,    // ₹499/month
        ["elite"] = 99900     // ₹999/month
    };

    public RazorpayService(
        HttpClient http,
        IConfiguration config,
        ILogger<RazorpayService> logger)
    {
        _http = http;
        _keyId = config["Razorpay:KeyId"]!;
        _keySecret = config["Razorpay:KeySecret"]!;
        _webhookSecret = config["Razorpay:WebhookSecret"]!;
        _logger = logger;

        // Basic auth for Razorpay API
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_keyId}:{_keySecret}"));
        _http.DefaultRequestHeaders.Add("Authorization", $"Basic {credentials}");
        _http.BaseAddress = new Uri("https://api.razorpay.com/v1/");
    }

    // ============================================================
    //  CreateOrderAsync
    //  Creates a Razorpay order — returns orderId for frontend
    // ============================================================
    public async Task<RazorpayOrderResult> CreateOrderAsync(
        string planType, string userId, string currency = "INR")
    {
        try
        {
            if (!PlanPrices.TryGetValue(planType.ToLower(), out var amountPaise))
                return RazorpayOrderResult.Failed("Invalid plan type");

            var payload = new
            {
                amount = amountPaise,
                currency,
                receipt = $"cv_{userId[..8]}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                notes = new { userId, planType }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("orders", content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Razorpay order creation failed: {Status} — {Body}",
                    response.StatusCode, body);
                return RazorpayOrderResult.Failed("Order creation failed");
            }

            var result = JsonSerializer.Deserialize<RazorpayOrderResponse>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return new RazorpayOrderResult
            {
                Success = true,
                OrderId = result?.Id ?? "",
                AmountPaise = amountPaise,
                Currency = currency,
                KeyId = _keyId    // sent to frontend for Razorpay JS SDK
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception creating Razorpay order");
            return RazorpayOrderResult.Failed(ex.Message);
        }
    }

    // ============================================================
    //  VerifyWebhookSignature
    //  Called in BillingController webhook endpoint
    //  HMAC-SHA256 verification — must verify before processing
    // ============================================================
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_webhookSecret));
            var computed = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var expected = Convert.ToHexString(computed).ToLower();
            return expected == signature.ToLower();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook signature verification failed");
            return false;
        }
    }

    // ============================================================
    //  VerifyPaymentSignature
    //  Called after frontend payment — verify before activating
    // ============================================================
    public bool VerifyPaymentSignature(
        string orderId, string paymentId, string signature)
    {
        try
        {
            var message = $"{orderId}|{paymentId}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_keySecret));
            var computed = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
            var expected = Convert.ToHexString(computed).ToLower();
            return expected == signature.ToLower();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Payment signature verification failed");
            return false;
        }
    }
}

// ── Result models ─────────────────────────────────────────────
public class RazorpayOrderResult
{
    public bool Success { get; set; }
    public string OrderId { get; set; } = "";
    public int AmountPaise { get; set; }
    public string Currency { get; set; } = "INR";
    public string KeyId { get; set; } = "";
    public string? Error { get; set; }

    public static RazorpayOrderResult Failed(string error) => new()
    {
        Success = false,
        Error = error
    };
}

public class RazorpayOrderResponse
{
    public string? Id { get; set; }
    public int Amount { get; set; }
    public string? Currency { get; set; }
    public string? Status { get; set; }
}