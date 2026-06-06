using ChatVerse.API.Models.Games;
using ChatVerse.Domain.Constants;
using ChatVerse.Infrastructure.Persistence.Redis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatVerse.API.Services.Games;

// ============================================================
//  QuizQuestionProvider — fetches trivia from OpenTriviaDB with
//  multi-tier resilience.
//
//  Tier 1: Redis cache (1hr TTL). With 4000+ Q's at the source,
//          and a tiny 10-Q window per round, caching a much larger
//          batch (50 Q's per cache miss) gives us 5+ rounds before
//          another upstream call.
//
//  Tier 2: Live opentdb.com call with base64 encoding. Base64 sidesteps
//          OpenTriviaDB's notorious HTML-entity bugs (&quot;, &#039; etc.)
//          which used to render literally on clients before the encode
//          parameter was added by the maintainers.
//
//  Tier 3: LocalQuizBank — 30 hand-curated questions. Used when
//          opentdb is down OR rate-limited OR returns response_code != 0.
//          Quality is high but quantity is limited — so this is a true
//          "the show must go on" fallback, not a primary source.
//
//  Why a separate "fetched-but-unused" cache larger than one round?
//    OpenTriviaDB rate-limits 5 req per 5 seconds per IP. If 10 quiz
//    rooms boot simultaneously on launch, we'd 429 immediately. The
//    over-fetch pattern (request 50, serve 10 at a time) flattens those
//    bursts cheaply.
// ============================================================

public class QuizQuestionProvider
{
    private readonly HttpClient _http;
    private readonly RedisService _redis;
    private readonly ILogger<QuizQuestionProvider> _logger;

    private const string OpenTriviaUrl = "https://opentdb.com/api.php";
    private const int OverFetchSize = 50;     // fetch in bigger batches than we serve
    private const int MaxRequested = 50;      // OpenTriviaDB's hard cap

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };

    public QuizQuestionProvider(
        HttpClient http,
        RedisService redis,
        ILogger<QuizQuestionProvider> logger)
    {
        _http = http;
        _redis = redis;
        _logger = logger;

        // Keep this short — the user is staring at a spinner waiting for
        // their quiz to start. If opentdb is slow, fall back to local fast.
        _http.Timeout = TimeSpan.FromSeconds(6);
    }

    /// <summary>
    /// Returns <paramref name="count"/> shuffled questions matching the
    /// requested category/difficulty. Order is randomised per call so
    /// two rooms with identical settings don't get identical rounds.
    /// </summary>
    public async Task<List<QuizQuestion>> FetchAsync(
        QuizCategory category,
        QuizDifficulty difficulty,
        int count,
        CancellationToken ct)
    {
        count = Math.Clamp(count, 1, MaxRequested);
        var cacheKey = RedisKeys.QuizQuestionCache(
            CategorySlug(category),
            DifficultySlug(difficulty));

        // ── Tier 1: cache hit ────────────────────────────────────
        var cached = await TryReadCacheAsync(cacheKey);
        if (cached.Count >= count)
        {
            var picked = PickRandom(cached, count);
            await TrySaveCacheAsync(cacheKey, cached.Except(picked).ToList());
            return picked;
        }

        // ── Tier 2: live fetch ───────────────────────────────────
        var fresh = await TryFetchUpstreamAsync(category, difficulty, OverFetchSize, ct);
        if (fresh.Count >= count)
        {
            var picked = PickRandom(fresh, count);
            await TrySaveCacheAsync(cacheKey, fresh.Except(picked).ToList());
            return picked;
        }

        // ── Tier 3: local bank ───────────────────────────────────
        // We might have gotten SOME from cache or upstream — top up
        // from local rather than discarding what we have.
        var local = LocalQuizBank.Pick(category, difficulty, count - fresh.Count);
        var combined = fresh.Concat(local).Take(count).ToList();
        if (combined.Count < count)
        {
            // Last resort: pad with any-category local questions.
            var pad = LocalQuizBank.Pick(QuizCategory.Any, QuizDifficulty.Any, count - combined.Count);
            combined.AddRange(pad);
        }
        _logger.LogInformation(
            "Quiz questions assembled from cache/upstream/local: cache={Cache}, upstream={Upstream}, local={Local}, total={Total}",
            cached.Count, fresh.Count, local.Count, combined.Count);
        return combined.Take(count).ToList();
    }

    // ─── Cache helpers ────────────────────────────────────────────

    private async Task<List<QuizQuestion>> TryReadCacheAsync(string key)
    {
        try
        {
            var raw = await _redis.GetStringAsync(key);
            if (string.IsNullOrEmpty(raw)) return new();
            var decoded = JsonSerializer.Deserialize<List<QuizQuestion>>(raw);
            return decoded ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quiz cache read failed for {Key}", key);
            return new();
        }
    }

    private async Task TrySaveCacheAsync(string key, List<QuizQuestion> remaining)
    {
        try
        {
            // If we drained the cache empty, delete the key so the next
            // call falls through to a fresh upstream fetch instead of
            // reading "[]" and assuming cache miss.
            if (remaining.Count == 0)
            {
                await _redis.DeleteKeyAsync(key);
                return;
            }
            var json = JsonSerializer.Serialize(remaining);
            await _redis.SetStringAsync(key, json, RedisTTL.QuizCache);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quiz cache write failed for {Key}", key);
        }
    }

    // ─── OpenTriviaDB fetch ────────────────────────────────────────

    private async Task<List<QuizQuestion>> TryFetchUpstreamAsync(
        QuizCategory category, QuizDifficulty difficulty, int amount, CancellationToken ct)
    {
        try
        {
            var url = BuildUrl(category, difficulty, amount);
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "OpenTriviaDB returned HTTP {Status} for category={Category} difficulty={Difficulty}",
                    (int)resp.StatusCode, category, difficulty);
                return new();
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<OpenTriviaResponse>(body, JsonOpts);
            if (parsed is null) return new();

            // OpenTriviaDB response_code:
            //   0 = success, 1 = no results, 2 = invalid param,
            //   3 = token not found, 4 = token empty, 5 = rate limit
            if (parsed.ResponseCode != 0 || parsed.Results is null || parsed.Results.Count == 0)
            {
                _logger.LogInformation(
                    "OpenTriviaDB non-success response_code={Code} for category={Category} difficulty={Difficulty}",
                    parsed.ResponseCode, category, difficulty);
                return new();
            }

            return parsed.Results.Select(ConvertOne).ToList();
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("OpenTriviaDB request timed out (6s) — falling back to local");
            return new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenTriviaDB request failed");
            return new();
        }
    }

    private static QuizQuestion ConvertOne(OpenTriviaItem item)
    {
        // Base64-decode every text field. The API guarantees no entity
        // escapes inside base64 payloads, so we get clean unicode strings.
        static string Dec(string s)
            => string.IsNullOrEmpty(s) ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(s));

        var question = Dec(item.Question);
        var correct = Dec(item.CorrectAnswer);
        var incorrect = (item.IncorrectAnswers ?? new()).Select(Dec).ToList();

        var all = new List<string>(incorrect.Count + 1) { correct };
        all.AddRange(incorrect);
        // Fisher-Yates with CSPRNG — same reason as LocalQuizBank.
        for (int i = all.Count - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (all[i], all[j]) = (all[j], all[i]);
        }

        return new QuizQuestion(
            Id: Guid.NewGuid().ToString("N").Substring(0, 12),
            Category: Dec(item.Category),
            Difficulty: Dec(item.Difficulty),
            Question: question,
            Options: all,
            CorrectIndex: all.IndexOf(correct));
    }

    private static string BuildUrl(QuizCategory category, QuizDifficulty difficulty, int amount)
    {
        var sb = new StringBuilder(OpenTriviaUrl);
        sb.Append("?amount=").Append(amount);
        sb.Append("&type=multiple");
        sb.Append("&encode=base64");

        var catId = MapCategoryToOpenTriviaId(category);
        if (catId.HasValue) sb.Append("&category=").Append(catId.Value);

        if (difficulty != QuizDifficulty.Any)
            sb.Append("&difficulty=").Append(difficulty.ToString().ToLowerInvariant());

        return sb.ToString();
    }

    /// <summary>
    /// OpenTriviaDB uses numeric category IDs. Mapping comes from
    /// https://opentdb.com/api_category.php — verified at write-time
    /// but stable for years. Returning null = "any category".
    /// </summary>
    private static int? MapCategoryToOpenTriviaId(QuizCategory c) => c switch
    {
        QuizCategory.General => 9,
        QuizCategory.Books => 10,
        QuizCategory.Film => 11,
        QuizCategory.Music => 12,
        QuizCategory.Sports => 21,
        QuizCategory.Geography => 22,
        QuizCategory.History => 23,
        QuizCategory.Politics => 24,
        QuizCategory.Science => 17,     // "Science & Nature"
        QuizCategory.Computers => 18,
        QuizCategory.Mythology => 20,
        QuizCategory.Animals => 27,
        _ => null,
    };

    // ─── Misc helpers ──────────────────────────────────────────────

    private static List<QuizQuestion> PickRandom(List<QuizQuestion> pool, int n)
    {
        var indices = Enumerable.Range(0, pool.Count).ToList();
        var picked = new List<QuizQuestion>(n);
        for (int i = 0; i < n && indices.Count > 0; i++)
        {
            int idx = RandomNumberGenerator.GetInt32(indices.Count);
            picked.Add(pool[indices[idx]]);
            indices.RemoveAt(idx);
        }
        return picked;
    }

    private static string CategorySlug(QuizCategory c) =>
        c.ToString().ToLowerInvariant();

    private static string DifficultySlug(QuizDifficulty d) =>
        d.ToString().ToLowerInvariant();

    // ─── OpenTriviaDB wire types ───────────────────────────────────

    private sealed class OpenTriviaResponse
    {
        [JsonPropertyName("response_code")] public int ResponseCode { get; set; }
        [JsonPropertyName("results")] public List<OpenTriviaItem>? Results { get; set; }
    }

    private sealed class OpenTriviaItem
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("difficulty")] public string Difficulty { get; set; } = "";
        [JsonPropertyName("category")] public string Category { get; set; } = "";
        [JsonPropertyName("question")] public string Question { get; set; } = "";
        [JsonPropertyName("correct_answer")] public string CorrectAnswer { get; set; } = "";
        [JsonPropertyName("incorrect_answers")] public List<string> IncorrectAnswers { get; set; } = new();
    }
}
