using System.Text;
using System.Text.Json;

namespace ChatVerse.Infrastructure.ExternalServices.OpenAI;

/// <summary>
/// Groq-powered moderation service (free tier — 14,400 req/day)
/// Uses Llama-3 model to classify chat messages.
/// Drop-in replacement for OpenAI moderation — same ModerationResult output.
/// </summary>
public class OpenAIModerationService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger<OpenAIModerationService> _logger;

    private const string MODEL = "llama-3.1-8b-instant";
    private const string GROQ_URL = "https://api.groq.com/openai/v1/chat/completions";
    private const double FLAG_THRESH = 0.5;
    private const double BLOCK_THRESH = 0.85;

    private const string SYSTEM_PROMPT = """
        You are a content moderation AI. Analyze the given chat message and respond ONLY with a valid JSON object.
        No extra text, no markdown, no explanation.

        Rules:
        - harassment: threats, insults, bullying directed at a person
        - hate: slurs, discrimination based on race/religion/gender
        - sexual: explicit sexual content
        - sexual_minors: any sexual content involving minors (CRITICAL)
        - violence: graphic violence, threats of harm
        - spam: repetitive/promotional content
        - self_harm: encouraging self-harm or suicide

        Respond with exactly this JSON structure:
        {
          "flagged": true/false,
          "confidence": 0.0-1.0,
          "top_category": "category_name or null",
          "action": "clean/flagged/blocked"
        }

        action = "clean" if safe
        action = "flagged" if mildly inappropriate (confidence 0.4-0.84)
        action = "blocked" if seriously harmful (confidence >= 0.85)
        sexual_minors ALWAYS = blocked regardless of confidence.
        """;

    public OpenAIModerationService(
        HttpClient http,
        IConfiguration config,
        ILogger<OpenAIModerationService> logger)
    {
        _http = http;
        _apiKey = config["Groq:ApiKey"] ?? config["OpenAI:ApiKey"] ?? "";
        _logger = logger;
    }

    public async Task<ModerationResult> CheckTextAsync(string content)
    {
        // Skip if no key configured
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey.StartsWith("YOUR_"))
        {
            _logger.LogDebug("Moderation skipped — no API key configured");
            return ModerationResult.Clean();
        }

        try
        {
            var payload = new
            {
                model = MODEL,
                messages = new[]
                {
                    new { role = "system", content = SYSTEM_PROMPT },
                    new { role = "user",   content = $"Moderate this message: \"{content}\"" }
                },
                temperature = 0.0,
                max_tokens = 150,
                stream = false
            };

            var json = JsonSerializer.Serialize(payload);
            var request = new HttpRequestMessage(HttpMethod.Post, GROQ_URL);
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Groq API error: {Status} — {Body}", response.StatusCode, errBody);
                return ModerationResult.Clean();
            }

            var body = await response.Content.ReadAsStringAsync();
            var parsed = JsonSerializer.Deserialize<JsonElement>(body);
            var text = parsed
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            // Parse JSON from Groq response
            var result = ParseGroqResult(text, content);
            _logger.LogDebug("Moderation result for msg [{Preview}]: {Action} (conf: {Conf:P0})",
                content.Length > 30 ? content[..30] + "..." : content,
                result.Action, result.Confidence);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Groq moderation exception — defaulting to clean");
            return ModerationResult.Clean();
        }
    }

    private ModerationResult ParseGroqResult(string text, string originalContent)
    {
        try
        {
            // Extract JSON from response (handle any surrounding text)
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end < 0) return ModerationResult.Clean();

            var jsonStr = text[start..(end + 1)];
            var doc = JsonSerializer.Deserialize<JsonElement>(jsonStr);

            var flagged = doc.TryGetProperty("flagged", out var f) && f.GetBoolean();
            var confidence = doc.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.0;
            var category = doc.TryGetProperty("top_category", out var t) ? t.GetString() : null;
            var action = doc.TryGetProperty("action", out var a) ? a.GetString() ?? "clean" : "clean";

            // Override: sexual_minors always blocked
            if (category == "sexual_minors") action = "blocked";

            if (!flagged || action == "clean")
                return ModerationResult.Clean();

            return new ModerationResult
            {
                IsFlagged = true,
                Action = action,
                FlagReason = category,
                Confidence = confidence,
                TrustDelta = action == "blocked" ? -15 : -5
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Groq moderation response: {Text}", text);
            return ModerationResult.Clean();
        }
    }
}

// ── Result models (shared with ModerationOrchestrator) ───────
public class ModerationResult
{
    public string Action { get; set; } = "clean";
    public string? FlagReason { get; set; }
    public double Confidence { get; set; }
    public bool IsFlagged { get; set; }
    public string? RawResponse { get; set; }
    public int TrustDelta { get; set; } = 0;

    public static ModerationResult Clean() => new()
    {
        Action = "clean",
        IsFlagged = false,
        Confidence = 0,
        TrustDelta = 0
    };
}