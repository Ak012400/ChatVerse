using ChatVerse.API.Models.Games;
using ChatVerse.Domain.Constants;
using ChatVerse.Infrastructure.Persistence.Redis;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatVerse.API.Services.Games;

// ============================================================
//  GameSessionRegistry — central directory of active game sessions.
//
//  Acts as both factory (creates the right IGameSession impl based
//  on the game type stored in Redis meta) and cache (returns the
//  same instance for repeat slug lookups within the dyno's lifetime).
//
//  Why a registry at all?
//    The hub and ticker both need to reach a session by slug. If
//    each created its own instance, they'd diverge — two QuizSessions
//    for the same slug would race on Redis writes and the user would
//    see flickering state. One shared instance per slug + a shared
//    SemaphoreSlim inside the session keeps everything consistent.
//
//  Multi-dyno note:
//    The registry is per-dyno (singleton in DI). If we scale to >1
//    Render instance, two dynos could end up holding different
//    in-memory copies of the same slug — that's why every read/write
//    inside QuizSession touches Redis. The registry is purely a
//    deduplication-within-a-dyno layer, not a source of truth.
// ============================================================

public sealed class GameSessionRegistry
{
    private readonly RedisService _redis;
    private readonly QuizQuestionProvider _questions;
    private readonly JokesProvider _jokes;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<GameSessionRegistry> _logger;

    private readonly ConcurrentDictionary<string, IGameSession> _sessions = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public GameSessionRegistry(
        RedisService redis,
        QuizQuestionProvider questions,
        JokesProvider jokes,
        ILoggerFactory loggerFactory,
        ILogger<GameSessionRegistry> logger)
    {
        _redis = redis;
        _questions = questions;
        _jokes = jokes;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Snapshot of all currently-active session instances. Used by the
    /// ticker — it iterates this set every second to drive TickAsync.
    /// </summary>
    public IReadOnlyCollection<IGameSession> AllActive => _sessions.Values.ToList();

    /// <summary>
    /// Get or create a session for a slug. Returns null if no meta
    /// exists in Redis (i.e. the room was never created via the
    /// controller, or has expired past its TTL).
    /// </summary>
    public async Task<IGameSession?> GetOrLoadAsync(string slug, CancellationToken ct)
    {
        if (_sessions.TryGetValue(slug, out var existing)) return existing;

        var meta = await LoadMetaAsync(slug);
        if (meta is null) return null;

        var session = await CreateAsync(slug, meta, ct);
        // Race: if two requests created concurrently, keep whichever
        // landed in the dict first to avoid duplicate instances.
        var actual = _sessions.GetOrAdd(slug, session);
        if (!ReferenceEquals(actual, session))
        {
            await session.DisposeAsync();
            return actual;
        }
        return session;
    }

    /// <summary>
    /// Create a new room. Persists meta + adds to active-rooms index
    /// + returns a hot session.
    /// </summary>
    public async Task<IGameSession> CreateAsync(
        string slug,
        GameType type,
        string name,
        string hostUserId,
        string hostUsername,
        QuizSettings settings,
        CancellationToken ct)
    {
        var meta = new QuizRoomMeta
        {
            Slug = slug,
            Name = name,
            HostUserId = hostUserId,
            HostUsername = hostUsername,
            CreatedAtUtc = DateTime.UtcNow,
            Settings = settings,
            Type = type,
        };
        await SaveMetaAsync(meta);
        await AddToIndexAsync(slug);

        var session = await CreateAsync(slug, meta, ct);
        _sessions.TryAdd(slug, session);
        return session;
    }

    /// <summary>
    /// Forget a slug. Called when a room ends + ticker decides to
    /// release the in-memory instance, OR when an admin nukes it.
    /// </summary>
    public async Task DropAsync(string slug)
    {
        if (_sessions.TryRemove(slug, out var s))
            await s.DisposeAsync();
        await RemoveFromIndexAsync(slug);
        await _redis.DeleteKeyAsync(RedisKeys.GameRoomState(slug));
        await _redis.DeleteKeyAsync(RedisKeys.GameRoomMeta(slug));
    }

    /// <summary>
    /// List active rooms for the lobby page. Reads ONLY meta — never
    /// hydrates full sessions — so this is cheap even at 100+ rooms.
    /// </summary>
    public async Task<List<GameRoomDto>> ListActiveAsync(CancellationToken ct)
    {
        var slugs = await ReadIndexAsync();
        var rooms = new List<GameRoomDto>(slugs.Count);

        foreach (var slug in slugs)
        {
            var meta = await LoadMetaAsync(slug);
            if (meta is null) continue;

            // For room counts, prefer the hot session (most up-to-date)
            // but fall back to the persisted state for rooms that aren't
            // currently loaded into memory.
            if (_sessions.TryGetValue(slug, out var hot))
            {
                rooms.Add(new GameRoomDto(
                    Slug: slug,
                    Name: meta.Name,
                    Type: hot.Type,
                    Status: hot.Status,
                    PlayerCount: hot.PlayerCount,
                    MaxPlayers: meta.Settings.MaxPlayers,
                    SpectatorCount: hot.SpectatorCount,
                    HostUsername: meta.HostUsername,
                    CreatedAtUtc: meta.CreatedAtUtc));
            }
            else
            {
                var snap = await LoadStateSnapshotAsync(slug);
                rooms.Add(new GameRoomDto(
                    Slug: slug,
                    Name: meta.Name,
                    Type: meta.Type,
                    Status: snap?.Status ?? GameStatus.Lobby,
                    PlayerCount: snap?.PlayerCount ?? 0,
                    MaxPlayers: meta.Settings.MaxPlayers,
                    SpectatorCount: snap?.SpectatorCount ?? 0,
                    HostUsername: meta.HostUsername,
                    CreatedAtUtc: meta.CreatedAtUtc));
            }
        }

        // Most recent first — easier to scan for "is my friend's room
        // up yet" without scrolling.
        return rooms.OrderByDescending(r => r.CreatedAtUtc).ToList();
    }

    // ───────────────────────────────────────────────────────────────
    //  Concrete factory step
    // ───────────────────────────────────────────────────────────────

    private async Task<IGameSession> CreateAsync(
        string slug, QuizRoomMeta meta, CancellationToken ct)
    {
        // Factory dispatch keyed off the persisted meta.Type. Adding a
        // new game (Chess, Ludo, …) is a single switch arm here plus a
        // new IGameSession implementation — the hub and ticker don't
        // need to know about specific game types.
        IGameSession session = meta.Type switch
        {
            GameType.Jokes => new JokesSession(
                slug, meta, _redis, _jokes,
                _loggerFactory.CreateLogger<JokesSession>()),
            // Quiz, Trivia, and anything else default to QuizSession.
            // (Trivia is a frontend preset that reuses Quiz mechanics.)
            _ => new QuizSession(
                slug, meta, _redis, _questions,
                _loggerFactory.CreateLogger<QuizSession>()),
        };
        await session.InitializeAsync(ct);
        return session;
    }

    // ───────────────────────────────────────────────────────────────
    //  Redis-backed meta + index
    // ───────────────────────────────────────────────────────────────

    private async Task<QuizRoomMeta?> LoadMetaAsync(string slug)
    {
        try
        {
            var raw = await _redis.GetStringAsync(RedisKeys.GameRoomMeta(slug));
            if (string.IsNullOrEmpty(raw)) return null;
            return JsonSerializer.Deserialize<QuizRoomMeta>(raw, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GameRoomMeta load failed for {Slug}", slug);
            return null;
        }
    }

    private async Task SaveMetaAsync(QuizRoomMeta meta)
    {
        var json = JsonSerializer.Serialize(meta, JsonOpts);
        await _redis.SetStringAsync(
            RedisKeys.GameRoomMeta(meta.Slug), json, RedisTTL.GameRoom);
    }

    // Index is a delimited string in a single key — we'd prefer a Set
    // but RedisService doesn't expose SADD/SREM here, and a string
    // suffices at v1 scale. Easy to migrate later.
    private async Task<List<string>> ReadIndexAsync()
    {
        var raw = await _redis.GetStringAsync(RedisKeys.GameRoomIndex);
        if (string.IsNullOrEmpty(raw)) return new();
        return raw.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private async Task AddToIndexAsync(string slug)
    {
        var slugs = await ReadIndexAsync();
        if (slugs.Contains(slug)) return;
        slugs.Add(slug);
        await _redis.SetStringAsync(
            RedisKeys.GameRoomIndex, string.Join('|', slugs), RedisTTL.GameRoom);
    }

    private async Task RemoveFromIndexAsync(string slug)
    {
        var slugs = await ReadIndexAsync();
        slugs.RemoveAll(s => s == slug);
        await _redis.SetStringAsync(
            RedisKeys.GameRoomIndex, string.Join('|', slugs), RedisTTL.GameRoom);
    }

    private async Task<StateSnapshot?> LoadStateSnapshotAsync(string slug)
    {
        try
        {
            var raw = await _redis.GetStringAsync(RedisKeys.GameRoomState(slug));
            if (string.IsNullOrEmpty(raw)) return null;
            var state = JsonSerializer.Deserialize<QuizSession.QuizPersistedState>(raw, JsonOpts);
            if (state is null) return null;
            return new StateSnapshot(
                state.Status,
                state.Participants.Count(p => p.Role == GameRole.Player),
                state.Participants.Count(p => p.Role == GameRole.Spectator));
        }
        catch
        {
            return null;
        }
    }

    private sealed record StateSnapshot(GameStatus Status, int PlayerCount, int SpectatorCount);
}
