using System.Text;
using System.Text.Json;

namespace ChatVerse.Infrastructure.ExternalServices.OpenAI;

/// <summary>
/// OpenAI Moderation API wrapper.
/// Called async after every chat message — never blocks message delivery.
/// Results update MongoDB moderation.status + apply trust delta via PostgreSQL.
/// </summary>
public class OpenAIModerationService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger<OpenAIModerationService> _logger;

    // Thresholds — tune these based on false positive rate
    private const double FlagThreshold = 0.5;   // flag for review
    private const double BlockThreshold = 0.85;  // auto-block

    public OpenAIModerationService(
        HttpClient http,
        IConfiguration config,
        ILogger<OpenAIModerationService> logger)
    {
        _http = http;
        _apiKey = config["OpenAI:ApiKey"]!;
        _logger = logger;

        _http.BaseAddress = new Uri("https://api.openai.com/v1/");
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
    }

    // ============================================================
    //  CheckTextAsync
    //  Returns ModerationResult with action + category scores
    // ============================================================
    public async Task<ModerationResult> CheckTextAsync(string content)
    {
        try
        {
            var payload = new { input = content };
            var json = JsonSerializer.Serialize(payload);
            var request = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync("moderations", request);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("OpenAI moderation API error: {Status} — {Error}",
                    response.StatusCode, error);
                // On API failure — default to clean (don't block all messages)
                return ModerationResult.Clean();
            }

            var body = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<OpenAIResponse>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result?.Results == null || result.Results.Length == 0)
                return ModerationResult.Clean();

            var r = result.Results[0];

            // Determine action based on highest category score
            var maxScore = r.CategoryScores.MaxScore();
            var flagReason = r.CategoryScores.TopCategory();
            var isFlagged = r.Flagged || maxScore >= FlagThreshold;

            if (!isFlagged)
                return ModerationResult.Clean();

            var action = maxScore >= BlockThreshold ? "blocked" : "flagged";

            return new ModerationResult
            {
                Action = action,
                FlagReason = flagReason,
                Confidence = maxScore,
                IsFlagged = true,
                RawResponse = body,
                TrustDelta = action == "blocked" ? -15 : -5
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in OpenAI moderation check");
            return ModerationResult.Clean();
        }
    }
}

// ── Result model ──────────────────────────────────────────────
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

// ── OpenAI API response models ────────────────────────────────
public class OpenAIResponse
{
    public OpenAIResult[] Results { get; set; } = Array.Empty<OpenAIResult>();
}

public class OpenAIResult
{
    public bool Flagged { get; set; }
    public OpenAICategories Categories { get; set; } = new();
    public OpenAICategoryScores CategoryScores { get; set; } = new();
}

public class OpenAICategories
{
    public bool Harassment { get; set; }
    public bool HarassmentThreatening { get; set; }
    public bool Hate { get; set; }
    public bool HateThreatening { get; set; }
    public bool SelfHarm { get; set; }
    public bool Sexual { get; set; }
    public bool SexualMinors { get; set; }
    public bool Violence { get; set; }
    public bool ViolenceGraphic { get; set; }
}

public class OpenAICategoryScores
{
    public double Harassment { get; set; }
    public double HarassmentThreatening { get; set; }
    public double Hate { get; set; }
    public double HateThreatening { get; set; }
    public double SelfHarm { get; set; }
    public double Sexual { get; set; }
    public double SexualMinors { get; set; }
    public double Violence { get; set; }
    public double ViolenceGraphic { get; set; }

    public double MaxScore() => new[]
    {
        Harassment, HarassmentThreatening, Hate, HateThreatening,
        SelfHarm, Sexual, SexualMinors, Violence, ViolenceGraphic
    }.Max();

    public string TopCategory()
    {
        var scores = new Dictionary<string, double>
        {
            ["harassment"] = Harassment,
            ["harassment/threatening"] = HarassmentThreatening,
            ["hate"] = Hate,
            ["hate/threatening"] = HateThreatening,
            ["self-harm"] = SelfHarm,
            ["sexual"] = Sexual,
            ["sexual/minors"] = SexualMinors,
            ["violence"] = Violence,
            ["violence/graphic"] = ViolenceGraphic
        };
        return scores.MaxBy(x => x.Value).Key;
    }
}