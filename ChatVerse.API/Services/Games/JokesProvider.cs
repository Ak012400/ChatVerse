using ChatVerse.Domain.Constants;
using ChatVerse.Infrastructure.Persistence.Redis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatVerse.API.Services.Games;

// ============================================================
//  JokesProvider — fetches dad jokes from icanhazdadjoke with
//  Redis caching and a local fallback.
//
//  Tier 1 (Redis cache, 1hr TTL) — over-fetched batches so a
//                                   single round usually serves
//                                   straight from cache.
//  Tier 2 (icanhazdadjoke.com)   — free, no auth, no published
//                                   rate limit. Single GET with
//                                   Accept: application/json
//                                   returns one joke.
//  Tier 3 (LocalJokesBank)       — graceful fallback.
//
//  Why one-joke-per-request instead of a batch endpoint?
//    The icanhazdadjoke search endpoint exists but adds quirks
//    (term filter, pagination, search-ranking). For a quiz-style
//    drip, the simple /random endpoint hit N times in parallel is
//    cleaner and the API isn't rate-limit-sensitive enough to
//    worry about. We Task.WhenAll the fetches.
// ============================================================

public class JokesProvider
{
    private readonly HttpClient _http;
    private readonly RedisService _redis;
    private readonly ILogger<JokesProvider> _logger;

    private const string IcanhazJokeUrl = "https://icanhazdadjoke.com/";
    private const int OverFetchSize = 20;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public JokesProvider(HttpClient http, RedisService redis, ILogger<JokesProvider> logger)
    {
        _http = http;
        _redis = redis;
        _logger = logger;

        // icanhazdadjoke gates everything behind the Accept header —
        // browsers get HTML, app/json gets the structured response,
        // text/plain gets the raw joke. We want JSON so we can grab
        // the stable Id field for de-dup.
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        // Polite UA — the API maintainer asks for one in their docs.
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.Add(
            new System.Net.Http.Headers.ProductInfoHeaderValue("ChatVerse", "1.0"));
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    public async Task<List<JokeItem>> FetchAsync(int count, CancellationToken ct)
    {
        count = Math.Clamp(count, 1, 50);
        var cacheKey = RedisKeys.JokesCache;

        // Tier 1: cache hit
        var cached = await TryReadCacheAsync(cacheKey);
        if (cached.Count >= count)
        {
            var picked = PickRandom(cached, count);
            await TrySaveCacheAsync(cacheKey, cached.Except(picked).ToList());
            return picked;
        }

        // Tier 2: live fetch — N parallel requests up to OverFetchSize
        var fresh = await TryFetchUpstreamAsync(OverFetchSize, ct);
        if (fresh.Count >= count)
        {
            var picked = PickRandom(fresh, count);
            await TrySaveCacheAsync(cacheKey, fresh.Except(picked).ToList());
            return picked;
        }

        // Tier 3: top up from local bank
        var local = LocalJokesBank.Pick(count - fresh.Count);
        var combined = fresh.Concat(local).Take(count).ToList();
        _logger.LogInformation(
            "Jokes assembled: cache={Cache}, upstream={Upstream}, local={Local}, total={Total}",
            cached.Count, fresh.Count, local.Count, combined.Count);
        return combined;
    }

    // ─── Cache helpers ─────────────────────────────────────────────

    private async Task<List<JokeItem>> TryReadCacheAsync(string key)
    {
        try
        {
            var raw = await _redis.GetStringAsync(key);
            if (string.IsNullOrEmpty(raw)) return new();
            return JsonSerializer.Deserialize<List<JokeItem>>(raw) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Joke cache read failed");
            return new();
        }
    }

    private async Task TrySaveCacheAsync(string key, List<JokeItem> remaining)
    {
        try
        {
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
            _logger.LogWarning(ex, "Joke cache write failed");
        }
    }

    // ─── icanhazdadjoke fetch ──────────────────────────────────────

    private async Task<List<JokeItem>> TryFetchUpstreamAsync(int amount, CancellationToken ct)
    {
        try
        {
            // Parallel fetches — each request is small and uncorrelated.
            // We use a semaphore to cap concurrency at 5 so we don't
            // hammer the free API into temporarily rate-limiting us.
            using var sem = new SemaphoreSlim(5);
            var tasks = Enumerable.Range(0, amount).Select(async _ =>
            {
                await sem.WaitAsync(ct);
                try { return await FetchOneAsync(ct); }
                finally { sem.Release(); }
            });

            var results = await Task.WhenAll(tasks);
            // De-dupe by id (the API can return the same joke twice
            // for parallel calls — small bank).
            return results
                .Where(j => j != null)
                .Cast<JokeItem>()
                .GroupBy(j => j.Id)
                .Select(g => g.First())
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "icanhazdadjoke fetch failed; falling back to local");
            return new();
        }
    }

    private async Task<JokeItem?> FetchOneAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(IcanhazJokeUrl, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<IcanhazResponse>(body, JsonOpts);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Joke)) return null;
            return new JokeItem
            {
                // Use the upstream id verbatim so we can de-dup across
                // parallel calls in this batch. If it's missing, fall
                // back to a fresh GUID — never leaves the id field empty.
                Id = string.IsNullOrEmpty(parsed.Id) ? Guid.NewGuid().ToString("N")[..12] : parsed.Id,
                Text = parsed.Joke.Trim(),
            };
        }
        catch
        {
            return null;
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private static List<JokeItem> PickRandom(List<JokeItem> pool, int n)
    {
        var rng = new Random();
        var indices = Enumerable.Range(0, pool.Count).ToList();
        var picked = new List<JokeItem>(n);
        for (int i = 0; i < n && indices.Count > 0; i++)
        {
            int idx = rng.Next(indices.Count);
            picked.Add(pool[indices[idx]]);
            indices.RemoveAt(idx);
        }
        // Re-id picked jokes so two rounds don't collide on Redis keys.
        return picked.Select(j => new JokeItem
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Text = j.Text,
        }).ToList();
    }

    // ─── Wire type ────────────────────────────────────────────────
    private sealed class IcanhazResponse
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("joke")] public string Joke { get; set; } = "";
        [JsonPropertyName("status")] public int Status { get; set; }
    }
}
