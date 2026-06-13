using System.Text.Json;
using ChatVerse.Infrastructure.Persistence.Redis;
using Microsoft.Extensions.Logging;

namespace ChatVerse.Infrastructure.ExternalServices.Spotify;

/// <summary>
/// Fetches public Spotify oEmbed metadata (title + thumbnail) for a
/// given track / album / playlist URL.
///
/// Why: the message DTO already carries an embed URL the iframe can
/// render, but until we enrich with a human-readable title the chat
/// panel just says "Spotify track" which feels unfinished. oEmbed is
/// public — no client credentials required — so this is the cheapest
/// way to get a decent label.
///
/// Caching: results are stored in Redis for an hour. The same track
/// shared 20 times across the room only ever causes one outbound
/// HTTPS call. We tolerate the 1-hour staleness because track titles
/// don't change.
///
/// Failure mode: oEmbed downtime / network blips simply return null —
/// the caller falls back to the generic embed metadata and the
/// frontend keeps working.
/// </summary>
public class SpotifyOEmbedService
{
    private readonly HttpClient _http;
    private readonly RedisService _redis;
    private readonly ILogger<SpotifyOEmbedService> _logger;

    private const string OEMBED_BASE = "https://open.spotify.com/oembed";

    // Stable-ish endpoint that occasionally rate-limits — keep timeouts low.
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    public SpotifyOEmbedService(
        HttpClient http,
        RedisService redis,
        ILogger<SpotifyOEmbedService> logger)
    {
        _http = http;
        _redis = redis;
        _logger = logger;
    }

    /// <summary>
    /// Returns (title, thumbnailUrl) for the given canonical Spotify URL,
    /// or (null, null) on miss / failure. Safe to call inline from
    /// SendMessage — oEmbed responses are tiny and the cache eats most
    /// of the latency.
    /// </summary>
    public async Task<(string? Title, string? ThumbnailUrl)> FetchAsync(string webUrl)
    {
        if (string.IsNullOrWhiteSpace(webUrl)) return (null, null);

        var cacheKey = $"spotify:oembed:{webUrl}";

        // Cache hit?
        var cached = await _redis.GetStringAsync(cacheKey);
        if (!string.IsNullOrEmpty(cached))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<OEmbedCache>(cached);
                if (doc != null) return (doc.Title, doc.ThumbnailUrl);
            }
            catch
            {
                // Malformed cache row — fall through and re-fetch.
            }
        }

        // Cache miss → fetch.
        try
        {
            using var cts = new CancellationTokenSource(FetchTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{OEMBED_BASE}?url={Uri.EscapeDataString(webUrl)}");
            req.Headers.Add("Accept", "application/json");

            using var resp = await _http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Spotify oEmbed returned {Status} for {Url}",
                    resp.StatusCode, webUrl);
                // Cache the miss briefly so a broken URL doesn't get hammered.
                await _redis.SetStringAsync(cacheKey,
                    JsonSerializer.Serialize(new OEmbedCache()),
                    TimeSpan.FromMinutes(5));
                return (null, null);
            }

            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;

            string? title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            string? thumb = root.TryGetProperty("thumbnail_url", out var th) ? th.GetString() : null;

            var payload = new OEmbedCache { Title = title, ThumbnailUrl = thumb };
            await _redis.SetStringAsync(cacheKey, JsonSerializer.Serialize(payload), CacheTtl);

            return (title, thumb);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Spotify oEmbed timed out for {Url}", webUrl);
            return (null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spotify oEmbed fetch failed for {Url}", webUrl);
            return (null, null);
        }
    }

    private class OEmbedCache
    {
        public string? Title { get; set; }
        public string? ThumbnailUrl { get; set; }
    }
}
