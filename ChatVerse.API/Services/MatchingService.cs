using ChatVerse.API.Hubs;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.Redis;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Services;

/// <summary>
/// Background service that drives the VideoHub random-1-on-1 queue.
///
/// Every <see cref="PollIntervalMs"/> ms it tries to dequeue two users
/// from the Redis waiting list. When a pair is found it:
///   1. Generates a session id.
///   2. Persists a <see cref="VideoSession"/> in MongoDB with both
///      participants.
///   3. Tells Redis which pair the session belongs to (used by the
///      hub for partner-id lookups during signaling and ban routing).
///   4. Pushes "MatchFound" to both users via <see cref="IHubContext{VideoHub}"/>
///      so the frontend can start WebRTC signaling.
///
/// Designed to be safe to run on multiple backend instances: the Redis
/// LPOP is atomic, so two replicas won't double-match the same user.
/// </summary>
public class MatchingService : BackgroundService
{
    private const int PollIntervalMs = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<VideoHub> _hub;
    private readonly ILogger<MatchingService> _logger;

    public MatchingService(
        IServiceScopeFactory scopeFactory,
        IHubContext<VideoHub> hub,
        ILogger<MatchingService> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MatchingService starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TryMatchAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // Don't let a single bad iteration kill the loop.
                _logger.LogError(ex, "MatchingService iteration failed");
            }

            try
            {
                await Task.Delay(PollIntervalMs, stoppingToken);
            }
            catch (TaskCanceledException) { /* shutting down */ }
        }

        _logger.LogInformation("MatchingService stopped");
    }

    private async Task TryMatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var redis = scope.ServiceProvider.GetRequiredService<RedisService>();
        var mongo = scope.ServiceProvider.GetRequiredService<MongoService>();

        // Need at least two users to make a pair. Re-check after each pop
        // because the queue length is racy across replicas.
        var length = await redis.GetVideoQueueLengthAsync();
        if (length < 2) return;

        var userA = await redis.DequeueForVideoAsync();
        if (userA == null) return;

        var userB = await redis.DequeueForVideoAsync();
        if (userB == null)
        {
            // Lost the race — put A back at the head-ish and retry next tick.
            await redis.EnqueueForVideoAsync(userA);
            return;
        }

        // Edge case: same user enqueued twice. Put both back as one and bail.
        if (userA == userB)
        {
            await redis.EnqueueForVideoAsync(userA);
            return;
        }

        ct.ThrowIfCancellationRequested();

        var sessionId = $"vs_{Guid.NewGuid().ToString("N")[..12]}";

        // Persist the session so we have an audit trail + nsfw flag attachments.
        var session = new VideoSession
        {
            SessionId   = sessionId,
            StartedAt   = DateTime.UtcNow,
            Participants =
            {
                new Participant { UserId = userA, Username = "", TrustScore = 0, AgeVerified = false },
                new Participant { UserId = userB, Username = "", TrustScore = 0, AgeVerified = false },
            },
            Outcome     = "clean",
        };
        try
        {
            await mongo.CreateVideoSessionAsync(session);
        }
        catch (Exception ex)
        {
            // If we can't persist the session, abort and put the users back
            // so they can be matched again on the next tick.
            _logger.LogWarning(ex, "Could not create video session — re-enqueuing both users");
            await redis.EnqueueForVideoAsync(userA);
            await redis.EnqueueForVideoAsync(userB);
            return;
        }

        // Store the pair so VideoHub.GetPartnerConnectionIdAsync can resolve
        // each side's partner without another Mongo round-trip.
        await redis.SetStringAsync(
            $"video:session:{sessionId}",
            $"{userA},{userB}",
            TimeSpan.FromHours(4)
        );

        // "isInitiator" tells one side to mint the SDP offer. Convention:
        // the user dequeued first (userA) initiates.
        await _hub.Clients.User(userA).SendAsync(
            "MatchFound", sessionId, userB, /* isInitiator */ true, ct);
        await _hub.Clients.User(userB).SendAsync(
            "MatchFound", sessionId, userA, /* isInitiator */ false, ct);

        _logger.LogInformation(
            "Matched {UserA} ↔ {UserB} in session {SessionId}",
            userA, userB, sessionId);
    }
}
