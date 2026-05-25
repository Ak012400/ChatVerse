using ChatVerse.Domain.Constants;
using StackExchange.Redis;
using System.Text.Json;

namespace ChatVerse.Infrastructure.Persistence.Redis;

/// <summary>
/// All Redis operations for ChatVerse.
/// Handles: JWT session cache, user online status,
///          OTP rate limiting, room active counts,
///          SignalR backplane is configured in Program.cs separately.
/// </summary>
public class RedisService
{
    private readonly IDatabase _db;

    public RedisService(IConnectionMultiplexer redis)
    {
        _db = redis.GetDatabase();
    }

    // ============================================================
    //  SESSION CACHE
    //  Store JWT payload in Redis so we can invalidate on logout
    //  without waiting for token expiry
    // ============================================================

    public async Task SetSessionAsync(string userId, object sessionData)
    {
        var key = RedisKeys.Session(userId);
        var value = JsonSerializer.Serialize(sessionData);
        await _db.StringSetAsync(key, value, RedisTTL.Session);
    }

    public async Task<T?> GetSessionAsync<T>(string userId)
    {
        var key = RedisKeys.Session(userId);
        var value = await _db.StringGetAsync(key);
        if (!value.HasValue) return default;
        return JsonSerializer.Deserialize<T>(value!);
    }

    public async Task DeleteSessionAsync(string userId)
    {
        await _db.KeyDeleteAsync(RedisKeys.Session(userId));
    }

    public async Task<bool> SessionExistsAsync(string userId)
    {
        return await _db.KeyExistsAsync(RedisKeys.Session(userId));
    }

    // ============================================================
    //  USER ONLINE STATUS
    //  SignalR OnConnected/OnDisconnected updates this
    //  TTL auto-expires after 5 min — heartbeat renews it
    // ============================================================

    public async Task SetUserOnlineAsync(string userId)
    {
        await _db.StringSetAsync(
            RedisKeys.UserOnline(userId),
            "1",
            RedisTTL.UserOnline
        );
    }

    public async Task SetUserOfflineAsync(string userId)
    {
        await _db.KeyDeleteAsync(RedisKeys.UserOnline(userId));
    }

    public async Task<bool> IsUserOnlineAsync(string userId)
    {
        return await _db.KeyExistsAsync(RedisKeys.UserOnline(userId));
    }

    /// <summary>
    /// Renew online TTL — called every 2 min from SignalR heartbeat.
    /// </summary>
    public async Task RenewOnlineStatusAsync(string userId)
    {
        await _db.KeyExpireAsync(RedisKeys.UserOnline(userId), RedisTTL.UserOnline);
    }

    // ============================================================
    //  ROOM ACTIVE COUNTS
    //  Fast read for rooms list — updated by SignalR hub events
    //  TTL is 30 sec — stale is fine for room counts
    // ============================================================

    public async Task SetRoomActiveCountAsync(string roomSlug, int count)
    {
        await _db.StringSetAsync(
            RedisKeys.RoomActiveCount(roomSlug),
            count,
            RedisTTL.RoomCount
        );
    }

    public async Task<int> GetRoomActiveCountAsync(string roomSlug)
    {
        var value = await _db.StringGetAsync(RedisKeys.RoomActiveCount(roomSlug));
        return value.HasValue ? (int)value : 0;
    }

    public async Task IncrementRoomCountAsync(string roomSlug)
    {
        var key = RedisKeys.RoomActiveCount(roomSlug);
        await _db.StringIncrementAsync(key);
        await _db.KeyExpireAsync(key, RedisTTL.RoomCount);
    }

    public async Task DecrementRoomCountAsync(string roomSlug)
    {
        var key = RedisKeys.RoomActiveCount(roomSlug);
        var count = await _db.StringDecrementAsync(key);
        // Never go below 0
        if (count < 0) await _db.StringSetAsync(key, 0, RedisTTL.RoomCount);
        else await _db.KeyExpireAsync(key, RedisTTL.RoomCount);
    }

    // ============================================================
    //  OTP RATE LIMITING
    //  Max 3 OTP requests per 15 min window per email
    // ============================================================

    /// <summary>
    /// Returns true if OTP request is allowed, false if rate limited.
    /// Increments counter on each call.
    /// </summary>
    public async Task<bool> TryAllowOtpRequestAsync(string email)
    {
        var key = RedisKeys.OtpRateLimit(email.ToLower());
        var count = await _db.StringIncrementAsync(key);

        // Set TTL only on first request
        if (count == 1)
            await _db.KeyExpireAsync(key, RedisTTL.OtpRateLimit);

        return count <= Otp.MaxRequestsPerWindow;
    }

    public async Task<long> GetOtpRequestCountAsync(string email)
    {
        var value = await _db.StringGetAsync(RedisKeys.OtpRateLimit(email.ToLower()));
        return value.HasValue ? (long)value : 0;
    }

    // ============================================================
    //  VIDEO MATCHING QUEUE
    //  Waiting users for random video chat
    //  Simple list — push when waiting, pop when matched
    // ============================================================

    private const string VideoQueueKey = "video:queue:waiting";

    public async Task EnqueueForVideoAsync(string userId)
    {
        await _db.ListRightPushAsync(VideoQueueKey, userId);
    }

    public async Task<string?> DequeueForVideoAsync()
    {
        var value = await _db.ListLeftPopAsync(VideoQueueKey);
        return value.HasValue ? (string?)value : null;
    }

    public async Task RemoveFromVideoQueueAsync(string userId)
    {
        await _db.ListRemoveAsync(VideoQueueKey, userId);
    }

    public async Task<long> GetVideoQueueLengthAsync()
    {
        return await _db.ListLengthAsync(VideoQueueKey);
    }

    // ============================================================
    //  GENERIC HELPERS
    // ============================================================

    public async Task<bool> KeyExistsAsync(string key)
        => await _db.KeyExistsAsync(key);

    public async Task SetStringAsync(string key, string value, TimeSpan? ttl = null)
    {
        await _db.StringSetAsync(key, value, ttl.HasValue ? (Expiration)ttl.Value : Expiration.Default);
    }

    public async Task<string?> GetStringAsync(string key)
    {
        var val = await _db.StringGetAsync(key);
        return val.HasValue ? (string?)val : null;
    }

    public async Task DeleteKeyAsync(string key)
        => await _db.KeyDeleteAsync(key);
}