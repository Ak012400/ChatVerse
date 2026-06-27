using System.Text;
using System.Text.Json;

namespace ChatVerse.Infrastructure.ExternalServices.AI;

/// <summary>
/// Single chat-completion surface that fans out across multiple free
/// AI providers so we don't pin all load on one quota:
///
///   1. Groq (Llama 3.1 8B)        — fastest, 14.4k req/day free
///   2. Gemini 1.5 Flash (Google)  — fallback, 1.5k req/day free
///
/// Try providers in order; on 429/5xx, transparently fall through to
/// the next one. Caller doesn't know or care which served the reply.
/// All providers normalised to OpenAI-style {role, content} messages
/// at the call site so the shape doesn't leak.
///
/// Used by:
///   • OpenAIModerationService (text moderation)
///   • AiPersonaService (AI hosts)
/// </summary>
public class AiChatProvider
{
    private readonly HttpClient _http;
    private readonly string _groqKey;
    private readonly string _geminiKey;
    // Model names are now config-overridable so we can swap to a newer
    // model (or a model still on the free tier) without a redeploy.
    // Defaults track the current "recommended free-tier chat" model on
    // each provider as of 2026-06; override via env vars:
    //   Groq__Model = llama-3.3-70b-versatile (or whatever Groq publishes)
    //   Gemini__Model = gemini-1.5-flash
    private readonly string _groqModel;
    private readonly string _geminiModel;
    private readonly ILogger<AiChatProvider> _logger;

    private const string GroqUrl = "https://api.groq.com/openai/v1/chat/completions";
    private const string GroqDefaultModel = "llama-3.3-70b-versatile";
    // Gemini URL takes the model in the path; we build it dynamically
    // in TryGeminiAsync so the model env var actually has effect.
    private const string GeminiDefaultModel = "gemini-1.5-flash";

    public AiChatProvider(
        HttpClient http,
        IConfiguration config,
        ILogger<AiChatProvider> logger)
    {
        _http = http;
        _groqKey = config["Groq:ApiKey"] ?? "";
        _geminiKey = config["Gemini:ApiKey"] ?? "";
        _groqModel = config["Groq:Model"] ?? GroqDefaultModel;
        _geminiModel = config["Gemini:Model"] ?? GeminiDefaultModel;
        _logger = logger;
    }

    public record ChatMessage(string Role, string Content);

    public record ChatRequest(
        IReadOnlyList<ChatMessage> Messages,
        double Temperature = 0.7,
        int MaxTokens = 200);

    /// <summary>
    /// Returns the assistant's reply text, or empty string if every
    /// provider failed. Never throws — always degrade gracefully.
    /// </summary>
    public async Task<string> CompleteAsync(ChatRequest req, CancellationToken ct = default)
    {
        // Try Groq first if configured.
        if (IsValidKey(_groqKey))
        {
            var result = await TryGroqAsync(req, ct);
            if (!string.IsNullOrWhiteSpace(result)) return result;
            _logger.LogInformation("Groq returned empty / failed — falling back to Gemini");
        }

        if (IsValidKey(_geminiKey))
        {
            var result = await TryGeminiAsync(req, ct);
            if (!string.IsNullOrWhiteSpace(result)) return result;
        }

        _logger.LogWarning("All AI providers exhausted, returning empty reply");
        return "";
    }

    private static bool IsValidKey(string k) =>
        !string.IsNullOrWhiteSpace(k) && !k.StartsWith("YOUR_");

    // ============================================================
    //  Groq
    // ============================================================
    private async Task<string> TryGroqAsync(ChatRequest req, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                model = _groqModel,
                messages = req.Messages.Select(m => new { role = m.Role, content = m.Content }),
                temperature = req.Temperature,
                max_tokens = req.MaxTokens,
                stream = false,
            };

            var http = new HttpRequestMessage(HttpMethod.Post, GroqUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            http.Headers.Add("Authorization", $"Bearer {_groqKey}");

            var res = await _http.SendAsync(http, ct);
            if (!res.IsSuccessStatusCode)
            {
                // 429 (rate limited) and 5xx are the cases where falling
                // back to Gemini makes sense. 401/403/400 are config bugs
                // — log loudly so they get noticed.
                var body = await res.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Groq {Status}: {Body}", res.StatusCode, body);
                return "";
            }

            var json = await res.Content.ReadAsStringAsync(ct);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            return doc.GetProperty("choices")[0]
                      .GetProperty("message")
                      .GetProperty("content")
                      .GetString()?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Groq call threw");
            return "";
        }
    }

    // ============================================================
    //  Gemini (Google AI Studio)
    //  Different request shape — collapses our [{role, content}, ...]
    //  into Gemini's "contents" array with role mapping. system
    //  messages go into a separate systemInstruction field.
    // ============================================================
    private async Task<string> TryGeminiAsync(ChatRequest req, CancellationToken ct)
    {
        try
        {
            // Gemini takes ?key=... in the URL rather than an Authorization
            // header. Model name is config-overridable so we can swap to
            // gemini-2.0-flash etc without a redeploy.
            var geminiUrl =
                $"https://generativelanguage.googleapis.com/v1beta/models/{_geminiModel}:generateContent";
            var url = $"{geminiUrl}?key={_geminiKey}";

            // Split system from user/assistant turns.
            var systemText = string.Join("\n",
                req.Messages.Where(m => m.Role == "system").Select(m => m.Content));

            var contents = req.Messages
                .Where(m => m.Role != "system")
                .Select(m => new
                {
                    role = m.Role == "assistant" ? "model" : "user",
                    parts = new[] { new { text = m.Content } }
                })
                .ToList<object>();

            // Gemini requires at least one user turn — synthesise one if
            // the caller sent only a system message.
            if (contents.Count == 0)
                contents.Add(new { role = "user", parts = new[] { new { text = "(respond)" } } });

            object payload;
            if (!string.IsNullOrWhiteSpace(systemText))
            {
                payload = new
                {
                    systemInstruction = new { parts = new[] { new { text = systemText } } },
                    contents,
                    generationConfig = new
                    {
                        temperature = req.Temperature,
                        maxOutputTokens = req.MaxTokens,
                    },
                };
            }
            else
            {
                payload = new
                {
                    contents,
                    generationConfig = new
                    {
                        temperature = req.Temperature,
                        maxOutputTokens = req.MaxTokens,
                    },
                };
            }

            var http = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            var res = await _http.SendAsync(http, ct);
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Gemini {Status}: {Body}", res.StatusCode, body);
                return "";
            }

            var json = await res.Content.ReadAsStringAsync(ct);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            // candidates[0].content.parts[0].text
            return doc.GetProperty("candidates")[0]
                      .GetProperty("content")
                      .GetProperty("parts")[0]
                      .GetProperty("text")
                      .GetString()?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini call threw");
            return "";
        }
    }
}
