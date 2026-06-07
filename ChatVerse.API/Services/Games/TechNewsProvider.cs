using ChatVerse.Domain.Constants;
using ChatVerse.Infrastructure.Persistence.Redis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatVerse.API.Services.Games;

// ============================================================
//  TechNewsProvider — feeds the Tech Talk chat room's "Live
//  News" sidebar with a unified stream of HackerNews + dev.to.
//
//  Source choice:
//    • HackerNews via the Algolia search API — free, no auth,
//      generous rate. Returns the latest front-page popular
//      stories already sorted by relevance + points.
//    • dev.to public API — also free, no auth. Returns trending
//      articles tagged "programming" / "javascript" / etc.
//
//  Both sources are cached in Redis for 10 minutes — by the time
//  one expires the next ChatPage poll will trigger a refresh
//  transparently. We don't push these via SignalR because the
//  list updates slowly (single-digit changes per hour) and a 30s
//  client poll is cheaper than maintaining per-room subscriptions.
// ============================================================

public sealed class TechNewsProvider
{
    private readonly HttpClient _http;
    private readonly RedisService _redis;
    private readonly ILogger<TechNewsProvider> _logger;

    private const string HnUrl =
        "https://hn.algolia.com/api/v1/search?tags=front_page&hitsPerPage=15";
    private const string DevToUrl =
        "https://dev.to/api/articles?per_page=10&top=7";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public TechNewsProvider(HttpClient http, RedisService redis, ILogger<TechNewsProvider> logger)
    {
        _http = http;
        _redis = redis;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(6);
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.Add(
            new System.Net.Http.Headers.ProductInfoHeaderValue("ChatVerse", "1.0"));
    }

    public async Task<List<TechNewsItem>> FetchAsync(CancellationToken ct)
    {
        // Tier 1: cache
        try
        {
            var cached = await _redis.GetStringAsync(RedisKeys.TechNewsCache);
            if (!string.IsNullOrEmpty(cached))
            {
                var parsed = JsonSerializer.Deserialize<List<TechNewsItem>>(cached);
                if (parsed is { Count: > 0 }) return parsed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tech news cache read failed");
        }

        // Tier 2: parallel fetch upstream
        var hnTask = TryFetchHnAsync(ct);
        var devToTask = TryFetchDevToAsync(ct);
        await Task.WhenAll(hnTask, devToTask);

        // Interleave for visual variety — alternate sources so the
        // feed doesn't look like one site swallowed the other.
        var merged = Interleave(hnTask.Result, devToTask.Result).Take(20).ToList();

        if (merged.Count > 0)
        {
            try
            {
                await _redis.SetStringAsync(
                    RedisKeys.TechNewsCache,
                    JsonSerializer.Serialize(merged),
                    RedisTTL.TechNews);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tech news cache write failed");
            }
        }
        return merged;
    }

    // ─── HackerNews via Algolia ────────────────────────────────────
    private async Task<List<TechNewsItem>> TryFetchHnAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(HnUrl, ct);
            if (!resp.IsSuccessStatusCode) return new();
            var body = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<HnResponse>(body, JsonOpts);
            if (parsed?.Hits is null) return new();
            return parsed.Hits
                .Where(h => !string.IsNullOrEmpty(h.Title))
                .Select(h => new TechNewsItem(
                    Source: "HackerNews",
                    Title: h.Title!,
                    Url: !string.IsNullOrEmpty(h.Url)
                        ? h.Url!
                        : $"https://news.ycombinator.com/item?id={h.ObjectID}",
                    Points: h.Points ?? 0,
                    Author: h.Author ?? "",
                    CommentsUrl: $"https://news.ycombinator.com/item?id={h.ObjectID}",
                    CreatedAtUtc: h.CreatedAt))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HN fetch failed");
            return new();
        }
    }

    // ─── dev.to ────────────────────────────────────────────────────
    private async Task<List<TechNewsItem>> TryFetchDevToAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(DevToUrl, ct);
            if (!resp.IsSuccessStatusCode) return new();
            var body = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<List<DevToItem>>(body, JsonOpts);
            if (parsed is null) return new();
            return parsed
                .Where(a => !string.IsNullOrEmpty(a.Title))
                .Select(a => new TechNewsItem(
                    Source: "dev.to",
                    Title: a.Title!,
                    Url: a.Url ?? "",
                    Points: a.PositiveReactionsCount,
                    Author: a.User?.Name ?? a.User?.Username ?? "",
                    CommentsUrl: a.Url ?? "",
                    CreatedAtUtc: a.PublishedAt))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "dev.to fetch failed");
            return new();
        }
    }

    private static List<TechNewsItem> Interleave(
        List<TechNewsItem> a, List<TechNewsItem> b)
    {
        var result = new List<TechNewsItem>(a.Count + b.Count);
        int i = 0, j = 0;
        while (i < a.Count || j < b.Count)
        {
            if (i < a.Count) result.Add(a[i++]);
            if (j < b.Count) result.Add(b[j++]);
        }
        return result;
    }

    // ─── Wire types ───────────────────────────────────────────────
    private sealed class HnResponse
    {
        [JsonPropertyName("hits")] public List<HnHit>? Hits { get; set; }
    }
    private sealed class HnHit
    {
        [JsonPropertyName("title")]      public string? Title { get; set; }
        [JsonPropertyName("url")]        public string? Url { get; set; }
        [JsonPropertyName("points")]     public int? Points { get; set; }
        [JsonPropertyName("author")]     public string? Author { get; set; }
        [JsonPropertyName("objectID")]   public string? ObjectID { get; set; }
        [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    }
    private sealed class DevToItem
    {
        [JsonPropertyName("title")]                    public string? Title { get; set; }
        [JsonPropertyName("url")]                      public string? Url { get; set; }
        [JsonPropertyName("positive_reactions_count")] public int PositiveReactionsCount { get; set; }
        [JsonPropertyName("published_at")]             public DateTime PublishedAt { get; set; }
        [JsonPropertyName("user")]                     public DevToUser? User { get; set; }
    }
    private sealed class DevToUser
    {
        [JsonPropertyName("name")]     public string? Name { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
    }
}

public sealed record TechNewsItem(
    string Source,
    string Title,
    string Url,
    int Points,
    string Author,
    string CommentsUrl,
    DateTime CreatedAtUtc);
