using ChatVerse.API.Models.Games;
using ChatVerse.Domain.Constants;
using ChatVerse.Infrastructure.Persistence.Redis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatVerse.API.Services.Games;

// ============================================================
//  TrendingQuestionProvider — turns LIVE headlines into ambient
//  discussion prompts for the #general chat (and any other
//  whitelisted slug).
//
//  The hand-curated bank in AmbientQuestionProvider is fine for
//  evergreen prompts ("pineapple on pizza?") but it goes stale —
//  same 30 lines on a 7-min loop reads like a chatbot after one
//  evening. This provider keeps the room feeling current: pulls
//  actual trending headlines and wraps each as a discussion
//  starter ("Trending: '<headline>' — your take?").
//
//  Sources (all free, no API key required):
//    • Hacker News (Algolia search API)  → Tech topics, world-wide
//    • Reddit JSON                        → Country + topic feeds
//        - r/India / r/IndiaSpeaks     (country: India)
//        - r/worldnews                  (country: world)
//        - r/sports / r/cricket          (topic: sports — Indian cricket
//                                         skew because that's the audience)
//        - r/movies / r/bollywood        (topic: entertainment)
//        - r/AskReddit                   (topic: random / lifestyle)
//
//  Why Reddit unauthenticated JSON? It's free, no key, and gets us
//  topic-segmented + popularity-sorted lists for free. The 60 req/min
//  unauth limit per IP is plenty given our 7-min cadence (~0.15 rpm).
//  We also cache the merged pool in Redis (15 min TTL) so even if a
//  source is slow once we serve cached headlines for the next tick.
//
//  Output shape stays the same `AmbientQuestion` record consumed by
//  AmbientQuestionService — no client work needed.
// ============================================================

public sealed class TrendingQuestionProvider
{
    private readonly HttpClient _http;
    private readonly RedisService _redis;
    private readonly ILogger<TrendingQuestionProvider> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // 15-minute cache. Trending lists move slowly — even an hour-old
    // headline is still "current" by the time it hits a chat room.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);
    private const string CacheKey = "ambient:trending:pool:v1";

    // Each source becomes a Topic bucket on the resulting AmbientQuestion
    // so the client can colour-code the chip if it wants (e.g. red dot
    // for India, blue for tech).
    private static readonly TrendingFeed[] Feeds =
    {
        new("HackerNews", "Tech",        "https://hn.algolia.com/api/v1/search?tags=front_page&hitsPerPage=10", FeedKind.Hn),
        new("r/India",    "India",       "https://www.reddit.com/r/india/top.json?limit=10&t=day",              FeedKind.Reddit),
        new("r/IndiaSpeaks","India",     "https://www.reddit.com/r/IndiaSpeaks/top.json?limit=10&t=day",        FeedKind.Reddit),
        new("r/worldnews","World",       "https://www.reddit.com/r/worldnews/top.json?limit=10&t=day",          FeedKind.Reddit),
        new("r/cricket",  "Sports",      "https://www.reddit.com/r/cricket/top.json?limit=10&t=week",           FeedKind.Reddit),
        new("r/movies",   "Entertainment","https://www.reddit.com/r/movies/top.json?limit=10&t=week",           FeedKind.Reddit),
        new("r/bollywood","Entertainment","https://www.reddit.com/r/bollywood/top.json?limit=10&t=week",         FeedKind.Reddit),
        new("r/AskReddit","Random",      "https://www.reddit.com/r/AskReddit/top.json?limit=15&t=day",          FeedKind.Reddit),
    };

    public TrendingQuestionProvider(
        HttpClient http,
        RedisService redis,
        ILogger<TrendingQuestionProvider> logger)
    {
        _http = http;
        _redis = redis;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(8);
        _http.DefaultRequestHeaders.UserAgent.Clear();
        // Reddit returns 429 / fake-bot pages without a real User-Agent.
        // Use a stable bot-style name so the rate-limit bucket is ours,
        // not shared with a generic crawler heuristic.
        _http.DefaultRequestHeaders.UserAgent.Add(
            new System.Net.Http.Headers.ProductInfoHeaderValue("ChatVerseAmbientBot", "1.0"));
    }

    /// <summary>
    /// Returns a single ambient discussion prompt derived from a live
    /// trending headline, or null if every source is down. The caller
    /// is expected to fall back to a curated bank in that case.
    /// </summary>
    public async Task<AmbientQuestion?> NextAsync(CancellationToken ct)
    {
        var pool = await EnsurePoolAsync(ct);
        if (pool.Count == 0) return null;

        var pick = pool[RandomNumberGenerator.GetInt32(pool.Count)];
        return new AmbientQuestion(
            Id: Guid.NewGuid().ToString("N")[..12],
            Mode: AmbientQuestionMode.Discussion,
            Text: ToPrompt(pick),
            Options: null,
            Category: pick.Topic,
            EmittedAtUtc: DateTime.UtcNow);
    }

    /// <summary>
    /// Loads the cached pool or refreshes it from all upstream feeds in
    /// parallel. Cached version is preferred to keep cadence cost ~0.
    /// </summary>
    private async Task<List<TrendingItem>> EnsurePoolAsync(CancellationToken ct)
    {
        // Tier 1: Redis cache
        try
        {
            var cached = await _redis.GetStringAsync(CacheKey);
            if (!string.IsNullOrEmpty(cached))
            {
                var parsed = JsonSerializer.Deserialize<List<TrendingItem>>(cached);
                if (parsed is { Count: > 0 }) return parsed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Trending cache read failed");
        }

        // Tier 2: parallel fetch.
        var tasks = Feeds.Select(f => FetchFeedAsync(f, ct)).ToArray();
        await Task.WhenAll(tasks);
        var merged = tasks
            .SelectMany(t => t.Result)
            // De-dup by title (Reddit cross-posts the same story in
            // multiple subs sometimes).
            .GroupBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            // Don't ship titles that are too short (low-value) or too
            // long (drowns the chat).
            .Where(i => i.Title.Length is >= 18 and <= 180)
            .ToList();

        if (merged.Count > 0)
        {
            try
            {
                await _redis.SetStringAsync(
                    CacheKey,
                    JsonSerializer.Serialize(merged),
                    CacheTtl);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Trending cache write failed");
            }
        }

        _logger.LogInformation(
            "TrendingQuestionProvider refreshed pool: {Count} items across {Feeds} feeds",
            merged.Count,
            Feeds.Length);
        return merged;
    }

    private async Task<List<TrendingItem>> FetchFeedAsync(TrendingFeed feed, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(feed.Url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Trending feed {Source} returned {Status}", feed.Source, (int)resp.StatusCode);
                return new();
            }
            var body = await resp.Content.ReadAsStringAsync(ct);

            return feed.Kind switch
            {
                FeedKind.Hn     => ParseHn(body, feed),
                FeedKind.Reddit => ParseReddit(body, feed),
                _ => new(),
            };
        }
        catch (Exception ex)
        {
            // Single-feed failure mustn't poison the pool. Log and skip.
            _logger.LogDebug(ex, "Trending feed {Source} fetch failed", feed.Source);
            return new();
        }
    }

    // ─── Parsers ────────────────────────────────────────────────────

    private static List<TrendingItem> ParseHn(string body, TrendingFeed feed)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<HnResponse>(body, JsonOpts);
            if (parsed?.Hits is null) return new();
            return parsed.Hits
                .Where(h => !string.IsNullOrEmpty(h.Title))
                .Select(h => new TrendingItem(
                    Source: feed.Source,
                    Topic: feed.Topic,
                    Title: h.Title!,
                    Url: !string.IsNullOrEmpty(h.Url)
                        ? h.Url!
                        : $"https://news.ycombinator.com/item?id={h.ObjectID}",
                    Score: h.Points ?? 0))
                .ToList();
        }
        catch { return new(); }
    }

    private static List<TrendingItem> ParseReddit(string body, TrendingFeed feed)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<RedditResponse>(body, JsonOpts);
            var children = parsed?.Data?.Children;
            if (children is null) return new();
            return children
                .Where(c => c?.Data is not null && !c.Data!.Stickied && !string.IsNullOrEmpty(c.Data.Title))
                .Select(c => new TrendingItem(
                    Source: feed.Source,
                    Topic: feed.Topic,
                    Title: SafeTrim(c.Data!.Title!),
                    // Permalink path → full URL.
                    Url: !string.IsNullOrEmpty(c.Data.Permalink)
                        ? $"https://reddit.com{c.Data.Permalink}"
                        : (c.Data.Url ?? ""),
                    Score: c.Data.Score ?? 0))
                .ToList();
        }
        catch { return new(); }
    }

    private static string SafeTrim(string s)
    {
        var clean = s.Replace("\n", " ").Replace("\r", " ").Trim();
        return clean.Length > 180 ? clean[..177] + "…" : clean;
    }

    // ─── Prompt-style wrapping ──────────────────────────────────────
    //
    // The same headline can fall into multiple "framings" so the room
    // doesn't see "Trending: X" every single time. Pick uniformly at
    // random per emission.
    private static readonly string[] FramingsHeadline =
    {
        "Trending: \"{0}\" — your take?",
        "Just saw: \"{0}\". Big deal or nothingburger?",
        "{1}: \"{0}\". What do you think?",
        "Headlines today — \"{0}\". Agree?",
        "\"{0}\" — what's the room's hot take?",
    };
    private static readonly string[] FramingsAskReddit =
    {
        "From r/AskReddit: {0}",
        "Quick room poll: {0}",
        "Discussion: {0}",
    };

    private static string ToPrompt(TrendingItem item)
    {
        // r/AskReddit titles are already questions — don't add wrapping.
        if (item.Source.Equals("r/AskReddit", StringComparison.OrdinalIgnoreCase))
        {
            var idx = RandomNumberGenerator.GetInt32(FramingsAskReddit.Length);
            return string.Format(FramingsAskReddit[idx], item.Title.TrimEnd('?') + "?");
        }
        var fidx = RandomNumberGenerator.GetInt32(FramingsHeadline.Length);
        return string.Format(FramingsHeadline[fidx], item.Title, item.Topic);
    }

    // ─── DTOs ───────────────────────────────────────────────────────

    private enum FeedKind { Hn, Reddit }
    private sealed record TrendingFeed(string Source, string Topic, string Url, FeedKind Kind);
    /// <summary>One normalized headline from any source.</summary>
    public sealed record TrendingItem(string Source, string Topic, string Title, string Url, int Score);

    private sealed class HnResponse
    {
        [JsonPropertyName("hits")] public List<HnHit>? Hits { get; set; }
    }
    private sealed class HnHit
    {
        [JsonPropertyName("title")]    public string? Title { get; set; }
        [JsonPropertyName("url")]      public string? Url { get; set; }
        [JsonPropertyName("objectID")] public string? ObjectID { get; set; }
        [JsonPropertyName("points")]   public int? Points { get; set; }
    }
    private sealed class RedditResponse { public RedditListing? Data { get; set; } }
    private sealed class RedditListing  { public List<RedditChild>? Children { get; set; } }
    private sealed class RedditChild    { public RedditPost? Data { get; set; } }
    private sealed class RedditPost
    {
        public string? Title { get; set; }
        public string? Url { get; set; }
        public string? Permalink { get; set; }
        public int? Score { get; set; }
        public bool Stickied { get; set; }
    }
}
