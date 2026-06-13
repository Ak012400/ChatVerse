using ChatVerse.API.Extensions;
using ChatVerse.Domain.Constants;
using ChatVerse.Domain.Entities;
using ChatVerse.Domain.Enums;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using ChatVerse.Infrastructure.Persistence.Redis;
using ChatVerse.Infrastructure.Services.UserState;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Hubs;

/// <summary>
/// VideoHub — long-term home for random-1-on-1 WebRTC.
///
/// Backend is now COMPLETE — <see cref="ChatVerse.API.Services.MatchingService"/>
/// runs as a hosted background service, polls the Redis queue every
/// 500ms, dequeues pairs, persists a <see cref="Domain.Entities.VideoSession"/>
/// in MongoDB, and emits "MatchFound" to both clients here.
///
/// Frontend migration is still pending: <c>VideoPage.tsx</c> currently
/// drives video through <see cref="ChatHub"/>'s legacy in-memory queue
/// (StartAutoMatch / SendWebRTCOffer/Answer/IceCandidate). When ready:
///   1. Open a second SignalR connection at <c>/hubs/video</c>.
///   2. Replace <c>StartAutoMatch</c> with <c>JoinQueue</c> here.
///   3. Replace SendWebRTC* relays with the unified <c>SendSignal</c>.
///   4. Remove the legacy methods from ChatHub.
/// </summary>
[Authorize]
public class VideoHub : Hub
{
    private readonly RedisService _redis;
    private readonly MongoService _mongo;
    private readonly PostgresProcService _postgres;
    private readonly UserStateService _userState;
    private readonly ILogger<VideoHub> _logger;

    // Redis key — connectionId → sessionId mapping
    private static string ConnSessionKey(string connId) => $"video:conn:session:{connId}";
    // Redis key — userId → connectionId mapping
    private static string UserConnKey(string userId) => $"video:user:conn:{userId}";

    public VideoHub(
        RedisService redis,
        MongoService mongo,
        PostgresProcService postgres,
        UserStateService userState,
        ILogger<VideoHub> logger)
    {
        _redis = redis;
        _mongo = mongo;
        _postgres = postgres;
        _userState = userState;
        _logger = logger;
    }

    // ============================================================
    //  OnConnectedAsync
    // ============================================================
    public override async Task OnConnectedAsync()
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();

        // Store userId → connectionId mapping in Redis
        await _redis.SetStringAsync(
            UserConnKey(userId),
            Context.ConnectionId,
            TimeSpan.FromHours(4)
        );

        _logger.LogInformation("VideoHub: User {UserId} connected [{ConnId}]",
            userId, Context.ConnectionId);

        await base.OnConnectedAsync();
    }

    // ============================================================
    //  OnDisconnectedAsync
    //  Auto-cleanup — remove from queue + end active session
    // ============================================================
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();

        // Remove from waiting queue if still there
        await _redis.RemoveFromVideoQueueAsync(userId);

        // Check if user was in an active session
        var sessionId = await _redis.GetStringAsync(ConnSessionKey(Context.ConnectionId));
        if (sessionId != null)
        {
            // End session in MongoDB
            await _mongo.EndVideoSessionAsync(sessionId, "disconnect", "incomplete");

            // Notify partner
            var partnerConnId = await GetPartnerConnectionIdAsync(sessionId, userId);
            if (partnerConnId != null)
            {
                await Clients.Client(partnerConnId).SendAsync("PartnerDisconnected", new
                {
                    reason = "disconnect",
                    message = "Your partner disconnected"
                });
            }

            // Cleanup Redis
            await _redis.DeleteKeyAsync(ConnSessionKey(Context.ConnectionId));
            await _redis.DeleteKeyAsync($"video:session:{sessionId}");
        }

        await _redis.DeleteKeyAsync(UserConnKey(userId));

        _logger.LogInformation("VideoHub: User {UserId} disconnected", userId);
        await base.OnDisconnectedAsync(exception);
    }

    // ============================================================
    //  JoinQueue
    //  User wants random video match — pushed to Redis queue
    //  MatchingService polls this queue every 500ms
    // ============================================================
    public async Task JoinQueue()
    {
        var userGuid = JwtService.GetUserId(Context.User!);
        var userId = userGuid.ToString();

        // Fresh trust read — bans/penalties take effect within the
        // cache TTL, not at JWT expiry.
        var trustScore = await _userState.GetTrustScoreAsync(userGuid);

        // Trust gate — minimum Normal band required
        if (trustScore < TrustBands.RestrictedMax)
        {
            await Clients.Caller.SendAsync("QueueError", new
            {
                code = "TRUST_TOO_LOW",
                message = "Trust score too low for video chat. Minimum required: 41"
            });
            return;
        }

        // Already in queue?
        var queueLen = await _redis.GetVideoQueueLengthAsync();
        // Simple check — remove first to prevent duplicates
        await _redis.RemoveFromVideoQueueAsync(userId);

        // Store connectionId with userId for MatchingService to use
        await _redis.SetStringAsync(
            UserConnKey(userId),
            Context.ConnectionId,
            TimeSpan.FromHours(1)
        );

        await _redis.EnqueueForVideoAsync(userId);

        await Clients.Caller.SendAsync("QueueJoined", new
        {
            message = "Searching for a match...",
            queuePosition = await _redis.GetVideoQueueLengthAsync()
        });

        _logger.LogInformation("VideoHub: User {UserId} joined queue", userId);
    }

    // ============================================================
    //  LeaveQueue
    //  User cancels search before match found
    // ============================================================
    public async Task LeaveQueue()
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();
        await _redis.RemoveFromVideoQueueAsync(userId);

        await Clients.Caller.SendAsync("QueueLeft", new
        {
            message = "Left the queue"
        });
    }

    // ============================================================
    //  SendSignal
    //  WebRTC SDP offer/answer + ICE candidates relay
    //  Server just forwards — never inspects media
    // ============================================================
    public async Task SendSignal(string targetUserId, string signalType, object payload)
    {
        // signalType: "offer" | "answer" | "ice-candidate"
        var fromUserId = JwtService.GetUserId(Context.User!).ToString();
        var fromUsername = JwtService.GetUsername(Context.User!);

        // Get target's connectionId
        var targetConnId = await _redis.GetStringAsync(UserConnKey(targetUserId));
        if (targetConnId == null)
        {
            await Clients.Caller.SendAsync("SignalError", new
            {
                code = "USER_NOT_FOUND",
                message = "Target user is not connected"
            });
            return;
        }

        // Forward signal to target
        await Clients.Client(targetConnId).SendAsync("ReceiveSignal", new
        {
            fromUserId,
            fromUsername,
            signalType,
            payload
        });
    }

    // ============================================================
    //  SessionReady
    //  Called by both users after WebRTC connection established
    //  Saves +1 trust for completed setup
    // ============================================================
    public async Task SessionReady(string sessionId)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();

        // Store conn → session mapping
        await _redis.SetStringAsync(
            ConnSessionKey(Context.ConnectionId),
            sessionId,
            TimeSpan.FromHours(4)
        );

        _logger.LogInformation("VideoHub: User {UserId} ready in session {SessionId}",
            userId, sessionId);
    }

    // ============================================================
    //  EndSession
    //  User clicks "End" button — clean termination
    // ============================================================
    public async Task EndSession(string sessionId)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();

        // End in MongoDB
        await _mongo.EndVideoSessionAsync(sessionId, "ended_by_user", "clean");

        // Apply trust: +1 for completing session
        _ = Task.Run(async () =>
        {
            try
            {
                await _postgres.ApplyTrustEventAsync(
                    Guid.Parse(userId),
                    TrustEventType.SessionCompleted,
                    TrustDeltas.SessionCompleted,
                    "Video session completed",
                    null
                );
                // Invalidate cached trust so the +1 propagates immediately.
                await _userState.InvalidateAsync(Guid.Parse(userId));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trust event failed for session {SessionId}", sessionId);
            }
        });

        // Notify partner
        var partnerConnId = await GetPartnerConnectionIdAsync(sessionId, userId);
        if (partnerConnId != null)
        {
            await Clients.Client(partnerConnId).SendAsync("PartnerDisconnected", new
            {
                reason = "ended",
                message = "Partner ended the session"
            });
        }

        // Cleanup
        await _redis.DeleteKeyAsync(ConnSessionKey(Context.ConnectionId));
        await _redis.DeleteKeyAsync($"video:session:{sessionId}");

        await Clients.Caller.SendAsync("SessionEnded", new { sessionId });
    }

    // ============================================================
    //  ReportNsfw
    //  nsfwjs browser detection — client reports violation
    //  Trust -25 applied to violator
    // ============================================================
    public async Task ReportNsfw(string sessionId, string violatorUserId,
        string label, double confidence)
    {
        var reporterId = JwtService.GetUserId(Context.User!).ToString();

        // ── Sanity guards ────────────────────────────────────────
        // 1) Self-report is always meaningless.
        if (reporterId == violatorUserId)
        {
            await Clients.Caller.SendAsync("SignalError", new
            {
                code = "SELF_REPORT_BLOCKED",
                message = "You cannot report yourself"
            });
            return;
        }

        // 2) Verify the reporter is actually a session participant. Without
        //    this, any user can fire a trust penalty at any other user just
        //    by guessing a sessionId — one of the easier abuse vectors.
        var (u1, u2) = await _redis.GetVideoSessionParticipantsAsync(sessionId);
        if (u1 == null || u2 == null
            || (reporterId != u1 && reporterId != u2)
            || (violatorUserId != u1 && violatorUserId != u2))
        {
            await Clients.Caller.SendAsync("SignalError", new
            {
                code = "REPORT_INVALID",
                message = "You are not a participant in this session"
            });
            return;
        }

        // Always record the flag — it's evidence even before any
        // threshold trips, so we have an audit trail for review.
        var flag = new NsfwFlag
        {
            DetectedAt = DateTime.UtcNow,
            UserId = violatorUserId,
            Label = label,
            Confidence = confidence
        };
        await _mongo.AppendNsfwFlagAsync(sessionId, flag);

        // Aggregate distinct reporters within the time window. Penalty only
        // fires when ≥ ReportThreshold OR a single very-high-confidence
        // report. Stops the one-click griefing where a user can dock another
        // 25 trust points unilaterally.
        var reporterCount = await _redis.AddNsfwReporterAsync(
            sessionId, violatorUserId, reporterId);

        var highConfidenceSingleReport = confidence >= NsfwModeration.HighConfidenceAutoTrip;
        var thresholdReached =
            reporterCount >= NsfwModeration.ReportThreshold || highConfidenceSingleReport;

        if (!thresholdReached)
        {
            await Clients.Caller.SendAsync("ReportAcknowledged", new
            {
                sessionId,
                violatorUserId,
                reporterCount,
                threshold = NsfwModeration.ReportThreshold,
                message = "Report recorded. Waiting on threshold before action is taken."
            });

            _logger.LogInformation(
                "NSFW report below threshold ({Count}/{Threshold}) — session {SessionId}, violator {ViolatorId}",
                reporterCount, NsfwModeration.ReportThreshold, sessionId, violatorUserId);
            return;
        }

        // ── Threshold met: apply consequences ───────────────────
        await _mongo.EndVideoSessionAsync(sessionId, "nsfw_violation", "banned");

        _ = Task.Run(async () =>
        {
            try
            {
                await _postgres.ApplyTrustEventAsync(
                    Guid.Parse(violatorUserId),
                    TrustEventType.VideoNsfw,
                    TrustDeltas.VideoNsfw,
                    $"NSFW content detected in video: {label} ({confidence:P0}) — reporters: {reporterCount}",
                    null
                );
                // Invalidate cached trust so the next gate check sees the penalty.
                await _userState.InvalidateAsync(Guid.Parse(violatorUserId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trust penalty failed for NSFW violation");
            }
        });

        // Disconnect violator
        var violatorConnId = await _redis.GetStringAsync(UserConnKey(violatorUserId));
        if (violatorConnId != null)
        {
            await Clients.Client(violatorConnId).SendAsync("ForcedDisconnect", new
            {
                reason = "nsfw_violation",
                message = "You have been removed for violating community guidelines"
            });
        }

        // Notify the reporter
        await Clients.Caller.SendAsync("PartnerDisconnected", new
        {
            reason = "nsfw_violation",
            message = "Partner removed: community guidelines violation"
        });

        // Cleanup session + report aggregation
        await _redis.DeleteKeyAsync($"video:session:{sessionId}");
        await _redis.ClearNsfwReportsAsync(sessionId, violatorUserId);

        _logger.LogWarning(
            "NSFW threshold met in session {SessionId} — violator {ViolatorId}, reporters {Count}",
            sessionId, violatorUserId, reporterCount);
    }

    // ============================================================
    //  Private helper — find partner's connectionId from session
    // ============================================================
    private async Task<string?> GetPartnerConnectionIdAsync(string sessionId, string myUserId)
    {
        var sessionData = await _redis.GetStringAsync($"video:session:{sessionId}");
        if (sessionData == null) return null;

        try
        {
            var parts = sessionData.Split(',');
            // Format: "user1Id,user2Id"
            var partnerId = parts.FirstOrDefault(p => p != myUserId);
            if (partnerId == null) return null;

            return await _redis.GetStringAsync(UserConnKey(partnerId));
        }
        catch
        {
            return null;
        }
    }
}