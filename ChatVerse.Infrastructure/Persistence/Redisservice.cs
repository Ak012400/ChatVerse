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

    // Global presence set keyed by userId. SADD on online, SREM on
    // offline. SCARD is O(1) so we can publish a live "1,247 online"
    // count without scanning keys.
    private const string PresenceGlobalSet = "presence:global";
    // Per-room presence sets so each chat room can report its own
    // "12 active" badge without touching Postgres or Mongo.
    private static string PresenceRoomSet(string roomSlug) => $"presence:room:{roomSlug}";

    public async Task SetUserOnlineAsync(string userId)
    {
        await _db.StringSetAsync(
            RedisKeys.UserOnline(userId),
            "1",
            RedisTTL.UserOnline
        );
        // Add to the global set. We intentionally don't TTL the set
        // itself — individual membership is removed on disconnect /
        // by the periodic janitor below.
        await _db.SetAddAsync(PresenceGlobalSet, userId);
    }

    public async Task SetUserOfflineAsync(string userId)
    {
        await _db.KeyDeleteAsync(RedisKeys.UserOnline(userId));
        await _db.SetRemoveAsync(PresenceGlobalSet, userId);
    }

    public async Task<bool> IsUserOnlineAsync(string userId)
    {
        return await _db.KeyExistsAsync(RedisKeys.UserOnline(userId));
    }

    /// <summary>
    /// Renew online TTL — called every 2 min from SignalR heartbeat.
    /// Also re-adds the user to the global set in case the prior
    /// disconnect was a crash that left the set stale.
    /// </summary>
    public async Task RenewOnlineStatusAsync(string userId)
    {
        await _db.KeyExpireAsync(RedisKeys.UserOnline(userId), RedisTTL.UserOnline);
        await _db.SetAddAsync(PresenceGlobalSet, userId);
    }

    /// <summary>
    /// O(1) read of currently-online users. Note: counts logical
    /// users, not connections — so a user with 3 tabs counts once.
    /// </summary>
    public async Task<long> GetGlobalOnlineCountAsync()
    {
        return await _db.SetLengthAsync(PresenceGlobalSet);
    }

    public async Task JoinRoomPresenceAsync(string roomSlug, string userId)
    {
        await _db.SetAddAsync(PresenceRoomSet(roomSlug), userId);
    }

    public async Task LeaveRoomPresenceAsync(string roomSlug, string userId)
    {
        await _db.SetRemoveAsync(PresenceRoomSet(roomSlug), userId);
    }

    public async Task<long> GetRoomOnlineCountAsync(string roomSlug)
    {
        return await _db.SetLengthAsync(PresenceRoomSet(roomSlug));
    }

    /// <summary>
    /// Returns the current online count for every requested room slug
    /// in one round-trip. Used by the rooms list to populate badges.
    /// </summary>
    public async Task<Dictionary<string, long>> GetRoomOnlineCountsAsync(IEnumerable<string> roomSlugs)
    {
        var slugs = roomSlugs.ToArray();
        if (slugs.Length == 0) return new();

        var batch = _db.CreateBatch();
        var tasks = slugs.Select(s => batch.SetLengthAsync(PresenceRoomSet(s))).ToArray();
        batch.Execute();
        var results = await Task.WhenAll(tasks);
        return slugs.Zip(results, (slug, count) => (slug, count))
                    .ToDictionary(p => p.slug, p => p.count);
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
    //  RANDOM GROUP LOBBIES
    //  Sorted set of open LiveKit rooms — score = current participant
    //  count. Lets us pick the most-filled room that still has room
    //  (good UX — newcomers join an existing convo rather than sitting
    //  in an empty room).
    // ============================================================

    private const string RandomGroupOpenKey = "randomgroup:open";

    /// <summary>
    /// Pick the open lobby with the highest fill (but still under max).
    /// Returns the room name + current count, or null if none available.
    /// </summary>
    public async Task<(string RoomName, int Count)?> FindOpenRandomGroupAsync(int maxParticipants)
    {
        // Score range: 1..(max-1). 0 means empty (should already be cleaned).
        var entries = await _db.SortedSetRangeByScoreWithScoresAsync(
            RandomGroupOpenKey,
            start: 0,
            stop: maxParticipants - 1,
            order: Order.Descending,
            take: 1);

        if (entries.Length == 0) return null;
        var entry = entries[0];
        return (entry.Element.ToString(), (int)entry.Score);
    }

    /// <summary>Add a brand-new lobby with one participant.</summary>
    public async Task RegisterRandomGroupAsync(string roomName)
    {
        await _db.SortedSetAddAsync(RandomGroupOpenKey, roomName, 1);
    }

    /// <summary>
    /// Increment a lobby's participant count by one. Returns the new count.
    /// </summary>
    public async Task<double> IncrementRandomGroupAsync(string roomName)
    {
        return await _db.SortedSetIncrementAsync(RandomGroupOpenKey, roomName, 1);
    }

    /// <summary>
    /// Decrement participant count. Removes the lobby from the open set
    /// if it hits zero so it isn't picked for new joins.
    /// </summary>
    public async Task<double> DecrementRandomGroupAsync(string roomName)
    {
        var newScore = await _db.SortedSetIncrementAsync(RandomGroupOpenKey, roomName, -1);
        if (newScore <= 0) await _db.SortedSetRemoveAsync(RandomGroupOpenKey, roomName);
        return newScore;
    }

    /// <summary>Force-remove a lobby (e.g. after it hits max capacity).</summary>
    public async Task UnlistRandomGroupAsync(string roomName)
    {
        await _db.SortedSetRemoveAsync(RandomGroupOpenKey, roomName);
    }

    /// <summary>
    /// Snapshot of all open lobbies with their counts — useful for an
    /// "active rooms" dashboard or admin view.
    /// </summary>
    public async Task<List<(string RoomName, int Count)>> ListRandomGroupsAsync()
    {
        var entries = await _db.SortedSetRangeByScoreWithScoresAsync(RandomGroupOpenKey);
        return entries.Select(e => (e.Element.ToString(), (int)e.Score)).ToList();
    }

    // ============================================================
    //  REGISTRATION INTENT
    //  Hold the user's submitted username + email + hashed password in
    //  Redis until the OTP is verified. The actual user row in
    //  user_auth.users is created only on successful verify — keeps
    //  the DB free of "zombie" half-registered accounts.
    // ============================================================

    private static string RegIntentKey(string email) => $"registration:intent:{email.ToLower()}";
    private static string RegOtpKey(string email)    => $"registration:otp:{email.ToLower()}";

    private static readonly TimeSpan RegIntentTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RegOtpTtl    = TimeSpan.FromMinutes(10);

    public async Task SetRegistrationIntentAsync(string email, string jsonIntent)
    {
        await _db.StringSetAsync(RegIntentKey(email), jsonIntent, RegIntentTtl);
    }

    public async Task<string?> GetRegistrationIntentAsync(string email)
    {
        var val = await _db.StringGetAsync(RegIntentKey(email));
        return val.HasValue ? (string?)val : null;
    }

    public async Task DeleteRegistrationIntentAsync(string email)
    {
        await _db.KeyDeleteAsync(RegIntentKey(email));
    }

    public async Task SetRegistrationOtpAsync(string email, string code)
    {
        await _db.StringSetAsync(RegOtpKey(email), code, RegOtpTtl);
    }

    public async Task<string?> GetRegistrationOtpAsync(string email)
    {
        var val = await _db.StringGetAsync(RegOtpKey(email));
        return val.HasValue ? (string?)val : null;
    }

    public async Task DeleteRegistrationOtpAsync(string email)
    {
        await _db.KeyDeleteAsync(RegOtpKey(email));
    }

    // ============================================================
    //  PASSWORD RESET
    //  Single-use code emailed to a user who lost their password.
    //  Code is stored against the email (not user-id) to avoid
    //  leaking whether an account exists for that address.
    // ============================================================

    private static string PwResetKey(string email) => $"password:reset:{email.ToLower()}";
    private static readonly TimeSpan PwResetTtl = TimeSpan.FromMinutes(15);

    public async Task SetPasswordResetCodeAsync(string email, string code)
    {
        await _db.StringSetAsync(PwResetKey(email), code, PwResetTtl);
    }

    public async Task<string?> GetPasswordResetCodeAsync(string email)
    {
        var v = await _db.StringGetAsync(PwResetKey(email));
        return v.HasValue ? (string?)v : null;
    }

    public async Task DeletePasswordResetCodeAsync(string email)
    {
        await _db.KeyDeleteAsync(PwResetKey(email));
    }

    // ============================================================
    //  PER-USER JOINED PRIVATE ROOMS
    //  A user's "I have access to these private rooms" set, keyed by
    //  user-id (works for guests too since they get a UUID).
    // ============================================================
    private static string JoinedRoomsKey(string userId) => $"user:rooms:{userId}";

    public async Task AddJoinedRoomAsync(string userId, string roomSlug)
    {
        var key = JoinedRoomsKey(userId);
        await _db.SetAddAsync(key, roomSlug);
        // 1-year sliding TTL — active users keep their membership cached.
        await _db.KeyExpireAsync(key, TimeSpan.FromDays(365));
    }

    public async Task<List<string>> GetJoinedRoomsAsync(string userId)
    {
        var members = await _db.SetMembersAsync(JoinedRoomsKey(userId));
        return members.Select(v => (string)v!).ToList();
    }

    public async Task RemoveJoinedRoomAsync(string userId, string roomSlug)
    {
        await _db.SetRemoveAsync(JoinedRoomsKey(userId), roomSlug);
    }

    // ============================================================
    //  USER-STATE CACHE (trust score, age verified)
    //  Short TTL — lets bans / verifications take effect quickly
    //  without re-issuing JWT. Hubs and controllers MUST read these
    //  instead of JWT claims so a penalised user can't keep posting
    //  just because their token is still warm.
    // ============================================================

    public async Task<short?> GetTrustScoreCacheAsync(string userId)
    {
        var val = await _db.StringGetAsync(RedisKeys.UserTrust(userId));
        return val.HasValue && short.TryParse(val, out var s) ? s : (short?)null;
    }

    public async Task SetTrustScoreCacheAsync(string userId, short score)
        => await _db.StringSetAsync(RedisKeys.UserTrust(userId), score, RedisTTL.UserState);

    public async Task<bool?> GetAgeVerifiedCacheAsync(string userId)
    {
        var val = await _db.StringGetAsync(RedisKeys.UserAgeVerified(userId));
        if (!val.HasValue) return null;
        return val == "1";
    }

    public async Task SetAgeVerifiedCacheAsync(string userId, bool verified)
        => await _db.StringSetAsync(
            RedisKeys.UserAgeVerified(userId),
            verified ? "1" : "0",
            RedisTTL.UserState);

    /// <summary>
    /// Drop cached trust/age values for a user. Call this immediately
    /// after any state mutation (trust event, age verification pass,
    /// manual ban) so the next gate check sees the fresh value.
    /// </summary>
    public async Task InvalidateUserStateAsync(string userId)
    {
        await _db.KeyDeleteAsync(RedisKeys.UserTrust(userId));
        await _db.KeyDeleteAsync(RedisKeys.UserAgeVerified(userId));
    }

    // ============================================================
    //  CHAT MATCH QUEUE
    //  Distributed replacement for ChatHub's in-memory ConcurrentQueue.
    //  Atomic LPUSH/LPOP so two API replicas behind a load balancer can
    //  pair users with each other.
    // ============================================================

    public async Task EnqueueForChatMatchAsync(string userId)
        => await _db.ListRightPushAsync(RedisKeys.ChatMatchQueue, userId);

    public async Task<string?> DequeueForChatMatchAsync()
    {
        var val = await _db.ListLeftPopAsync(RedisKeys.ChatMatchQueue);
        return val.HasValue ? (string?)val : null;
    }

    public async Task RemoveFromChatMatchQueueAsync(string userId)
        => await _db.ListRemoveAsync(RedisKeys.ChatMatchQueue, userId);

    public async Task<bool> IsInChatMatchQueueAsync(string userId)
    {
        // Queue is small in practice (single-digit waiting users at a time)
        var all = await _db.ListRangeAsync(RedisKeys.ChatMatchQueue);
        foreach (var v in all)
            if (v == userId) return true;
        return false;
    }

    // ============================================================
    //  VIDEO SESSION PARTICIPANT LOOKUP
    //  MatchingService writes "userA,userB" to video:session:{id}.
    //  Used by VideoHub.ReportNsfw to verify the reporter is actually
    //  a participant before any penalty is considered.
    // ============================================================

    public async Task<(string?, string?)> GetVideoSessionParticipantsAsync(string sessionId)
    {
        var val = await _db.StringGetAsync($"video:session:{sessionId}");
        if (!val.HasValue) return (null, null);
        var parts = ((string)val!).Split(',');
        return parts.Length == 2 ? (parts[0], parts[1]) : (null, null);
    }

    // ============================================================
    //  NSFW REPORT AGGREGATION
    //  Distinct-reporter SET per (session, violator) with short TTL.
    //  Trust penalty only fires when threshold met — stops one-click
    //  griefing where a single user can dock another 25 trust points.
    // ============================================================

    /// <summary>
    /// Adds reporterId to the set, returns the new distinct-reporter count.
    /// Sets an expiry on first add so abandoned reports clear themselves.
    /// </summary>
    public async Task<long> AddNsfwReporterAsync(string sessionId, string violatorId, string reporterId)
    {
        var key = RedisKeys.NsfwReports(sessionId, violatorId);
        var added = await _db.SetAddAsync(key, reporterId);
        if (added)
            await _db.KeyExpireAsync(key, RedisTTL.NsfwReportWindow);
        return await _db.SetLengthAsync(key);
    }

    public async Task ClearNsfwReportsAsync(string sessionId, string violatorId)
        => await _db.KeyDeleteAsync(RedisKeys.NsfwReports(sessionId, violatorId));

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