using ChatVerse.API.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace ChatVerse.API.Hubs;

// ============================================================
//  SoundboardHub — live energy drops for shared-stage rooms.
//
//  Drama-flavoured 8-sound pack: laugh / gasp / drumroll / applause /
//  fail / suspense / oooh / heartbeat. A user taps a button, the server
//  fans the sound id to every connection in the matching SignalR group,
//  and every client plays the SAME mp3 at the SAME moment.
//
//  Scopes (the room shape the sound belongs to):
//    • theater    — TheaterRoomPage: host + audience watch-party
//    • pyaar-live — PYAAR LIVE spectator grid (Saturday show)
//    • mehfil     — Mehfil room (open-mic / podcast / debate / …)
//    • video      — Hosted group video room (50-cap)
//
//  Group key shape: "sb:{scope}:{scopeId}"
//
//  Methods:
//    • JoinScope(scope, scopeId)
//    • LeaveScope(scope, scopeId)
//    • PlaySound(scope, scopeId, soundId)
//        Server validates soundId against the allowlist, rate-limits
//        per user via Redis, then broadcasts SoundPlayed to the group.
//
//  Server-push events:
//    • SoundPlayed { soundId, byUserId, byUsername, atUtc }
//
//  Rate-limiting:
//    • Default 3 sounds/sec per user — matches the spec ("no DDoS audio").
//    • PYAAR LIVE is tighter (1/sec) because the spectator audience is
//      bigger and one user spamming = everyone's eardrums.
//    • Implemented as a fixed-window Redis counter with TTL == window.
//
//  Async + cancellable everywhere (Context.ConnectionAborted) per the
//  ChatVerse house rule that hub methods never block forever.
// ============================================================

[Authorize]
public class SoundboardHub : Hub
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<SoundboardHub> _logger;

    public SoundboardHub(IConnectionMultiplexer redis, ILogger<SoundboardHub> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    // Allowlisted sounds — the client cannot ask the server to broadcast
    // an arbitrary string. Keeping the list short and themed keeps the
    // payload tight and the experience cohesive (each sound has a
    // distinct dramatic beat).
    public static readonly HashSet<string> AllowedSounds = new(StringComparer.OrdinalIgnoreCase)
    {
        "laugh", "gasp", "drumroll", "applause",
        "fail", "suspense", "oooh", "heartbeat",
    };

    // Allowlisted scopes — same shape-check for the same reason.
    private static readonly HashSet<string> AllowedScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        "theater", "pyaar-live", "mehfil", "video",
    };

    private static string GroupKey(string scope, string scopeId) => $"sb:{scope}:{scopeId}";
    private static string RateLimitKey(Guid userId, string scope) => $"sb:rl:{scope}:{userId}";

    /// <summary>Per-second cap. PYAAR LIVE is tighter because the show
    /// is crowded and one rogue button-masher tortures the whole audience.</summary>
    private static int PerSecondCap(string scope) =>
        string.Equals(scope, "pyaar-live", StringComparison.OrdinalIgnoreCase) ? 1 : 3;

    public async Task JoinScope(string scope, string scopeId)
    {
        var ct = Context.ConnectionAborted;
        if (!AllowedScopes.Contains(scope) || string.IsNullOrWhiteSpace(scopeId))
            throw new HubException("Invalid scope.");
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupKey(scope, scopeId), ct);
    }

    public async Task LeaveScope(string scope, string scopeId)
    {
        var ct = Context.ConnectionAborted;
        if (!AllowedScopes.Contains(scope) || string.IsNullOrWhiteSpace(scopeId))
            return; // silent no-op — leave should never throw
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupKey(scope, scopeId), ct);
    }

    public async Task PlaySound(string scope, string scopeId, string soundId)
    {
        var ct = Context.ConnectionAborted;

        if (!AllowedScopes.Contains(scope) || string.IsNullOrWhiteSpace(scopeId))
            throw new HubException("Invalid scope.");
        if (!AllowedSounds.Contains(soundId))
            throw new HubException("Unknown sound.");

        var userId = JwtService.GetUserId(Context.User!);
        var username = JwtService.GetUsername(Context.User!);

        // Fixed-window rate limit. Counter increments, expires 1s later.
        // If the counter is already past the cap, drop the request quietly
        // (no exception — UI doesn't need to know about spam taps).
        var db = _redis.GetDatabase();
        var rlKey = (RedisKey)RateLimitKey(userId, scope);
        var count = await db.StringIncrementAsync(rlKey, 1);
        if (count == 1)
        {
            // First hit in the window — set the TTL.
            await db.KeyExpireAsync(rlKey, TimeSpan.FromSeconds(1));
        }
        if (count > PerSecondCap(scope))
        {
            _logger.LogDebug("Soundboard rate-limited user {UserId} in scope {Scope}", userId, scope);
            return;
        }

        // Fan out. Fire-and-forget pattern not needed — Clients.Group is
        // already non-blocking from the caller's perspective. Pass the
        // connection's CT so a disconnect doesn't strand the send.
        await Clients.Group(GroupKey(scope, scopeId)).SendAsync(
            "SoundPlayed",
            new
            {
                soundId = soundId.ToLowerInvariant(),
                byUserId = userId,
                byUsername = username,
                atUtc = DateTime.UtcNow,
                scope,
                scopeId,
            },
            ct);
    }
}
