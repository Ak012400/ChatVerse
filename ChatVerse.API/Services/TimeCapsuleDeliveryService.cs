using ChatVerse.API.Hubs;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;  // RandomUserPick lives here
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Services;

// ============================================================
//  TimeCapsuleDeliveryService — hourly delivery sweeper.
//
//  Two queues every tick:
//    A) NEW CAPSULES whose ScheduledFor has passed → pick a random
//       eligible recipient (not the author, not blocked, recent active
//       user), claim atomically via MarkCapsuleDeliveredAsync, push
//       a SignalR event to that user's connections.
//    B) REPLIES whose ScheduledReplyDeliveryAt has passed → deliver
//       back to the original author with a "ReplyDelivered" event.
//
//  Recipient pool query — we hit Postgres for a random sample of
//  active registered users. Guest accounts excluded (they 24h expire
//  via maintenance, so an undelivered capsule would orphan if a guest
//  was chosen and then deleted before reading). Random sample size of
//  50 gives statistical fairness; we shuffle in-memory and pick one
//  who isn't the author.
//
//  Cadence: every 30 minutes. The cron is gentle because each capsule
//  has a 24h jitter window — exact-to-the-second delivery isn't the
//  promise.
//
//  Failure isolation: per-capsule try/catch. One failure doesn't kill
//  the batch; we log and continue.
// ============================================================

public sealed class TimeCapsuleDeliveryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<TimeCapsuleHub> _hub;
    private readonly ILogger<TimeCapsuleDeliveryService> _logger;

    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private const int BatchSize = 100;
    private const int RecipientPoolSample = 50;

    public TimeCapsuleDeliveryService(
        IServiceScopeFactory scopeFactory,
        IHubContext<TimeCapsuleHub> hub,
        ILogger<TimeCapsuleDeliveryService> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "TimeCapsuleDeliveryService started (cadence: {Mins} min)",
            Cadence.TotalMinutes);

        try { await Task.Delay(StartupDelay, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TimeCapsule delivery tick failed; will retry");
            }

            try { await Task.Delay(Cadence, ct); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("TimeCapsuleDeliveryService stopping");
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<MongoService>();
        var postgres = scope.ServiceProvider.GetRequiredService<PostgresProcService>();

        // ── A) Deliver new capsules ────────────────────────────────
        var dueCapsules = await mongo.GetCapsulesDueForDeliveryAsync(BatchSize);
        int deliveredCount = 0;

        if (dueCapsules.Count > 0)
        {
            // One Postgres query for the candidate pool — reused across
            // all capsules in this tick. Cheap; refreshed each tick.
            var candidates = await postgres.GetRandomActiveUserSampleAsync(RecipientPoolSample);

            foreach (var capsule in dueCapsules)
            {
                try
                {
                    var recipient = PickRecipientFor(capsule, candidates);
                    if (recipient is null)
                    {
                        _logger.LogWarning(
                            "No eligible recipient for capsule {Id} (pool size {Pool}) — will retry next tick",
                            capsule.Id, candidates.Count);
                        continue;
                    }

                    var claimed = await mongo.MarkCapsuleDeliveredAsync(
                        capsule.Id!, recipient.UserId.ToString(), recipient.Username);
                    if (!claimed) continue;  // another worker grabbed it

                    await _hub.Clients
                        .User(recipient.UserId.ToString())
                        .SendAsync("TimeCapsuleDelivered", new
                        {
                            id           = capsule.Id,
                            content      = capsule.Content,
                            type         = capsule.Type,
                            mediaUrl     = capsule.MediaUrl,
                            deliveryDays = capsule.DeliveryWindowDays,
                            authorName   = capsule.AuthorRevealed ? capsule.AuthorUsername : null,
                            deliveredAt  = DateTime.UtcNow,
                        }, ct);

                    deliveredCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to deliver capsule {Id} — will retry next tick",
                        capsule.Id);
                }
            }
        }

        // ── B) Deliver replies (3 days after recipient replied) ────
        var dueReplies = await mongo.GetRepliesDueForDeliveryAsync(BatchSize);
        int replyDelivered = 0;

        foreach (var capsule in dueReplies)
        {
            try
            {
                if (capsule.AuthorUserId is null)
                {
                    // True anonymous capsule — nobody to deliver back to.
                    // Mark delivered so we don't keep polling it.
                    await mongo.MarkReplyDeliveredAsync(capsule.Id!);
                    continue;
                }

                var claimed = await mongo.MarkReplyDeliveredAsync(capsule.Id!);
                if (!claimed) continue;

                await _hub.Clients
                    .User(capsule.AuthorUserId)
                    .SendAsync("TimeCapsuleReplyDelivered", new
                    {
                        capsuleId        = capsule.Id,
                        originalContent  = capsule.Content,
                        replyContent     = capsule.ReplyContent,
                        replyDeliveredAt = DateTime.UtcNow,
                    }, ct);

                replyDelivered++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reply delivery failed for capsule {Id}", capsule.Id);
            }
        }

        if (deliveredCount > 0 || replyDelivered > 0)
        {
            _logger.LogInformation(
                "TimeCapsule tick complete: delivered={Delivered}, replies={Replies}",
                deliveredCount, replyDelivered);
        }
    }

    /// <summary>
    /// Pick the first candidate that ISN'T the author. Pool is already
    /// random-sampled from Postgres, so any non-author is fair. Returns
    /// null if pool only contains the author (degenerate case).
    /// </summary>
    private static RandomUserPick? PickRecipientFor(
        TimeCapsule capsule,
        List<RandomUserPick> candidates)
    {
        foreach (var c in candidates)
        {
            if (capsule.AuthorUserId is not null &&
                c.UserId.ToString().Equals(capsule.AuthorUserId, StringComparison.OrdinalIgnoreCase))
            {
                continue;  // can't deliver to self
            }
            return c;
        }
        return null;
    }
}

// NOTE: RandomUserPick record lives in ChatVerse.Infrastructure.Persistence.PostgreSQL
// (defined alongside PostgresProcService) so that Infrastructure doesn't have to
// reference API project. The using statement at the top of this file imports it.
