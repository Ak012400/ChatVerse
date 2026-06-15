using ChatVerse.Infrastructure.Persistence;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;

namespace ChatVerse.API.Services;

// ============================================================
//  MaintenanceService — background webjob that prunes stale data
//  so we don't blow the free-tier storage caps.
//
//  Runs every 6 hours. Picked over "once at midnight" because:
//    • Free Render plans cold-start daily; a 24h cycle could miss
//      an entire day on bad timing.
//    • 6h cadence keeps deletion batches small, so each run finishes
//      well under a single connection's idle timeout.
//
//  Tasks per tick:
//    1. Postgres: call `user_auth.usp_cleanup_old_guests` →
//       deletes guest users older than 24h + cascades dependent
//       rows (iam.age_declarations, iam.ai_maturity_sessions, etc).
//       Returns the user ids that got wiped.
//    2. Mongo: delete messages authored by those wiped users, so
//       no orphan content sits in chat rooms.
//    3. Mongo: bulk-delete chat messages older than 30 days from
//       every room. Anonymous chat — nobody's coming back for
//       a month-old line.
//    4. Mongo: delete DMs older than 90 days. DMs are slightly more
//       personal, so they get a longer grace window than rooms.
//    5. Mongo: delete ENDED video sessions older than 7 days. The
//       moderation audit log keeps any flagged ones separately, so
//       this only sweeps clean / closed sessions.
//
//  All failures are isolated — if Mongo cleanup throws, Postgres
//  cleanup still runs next tick, and vice versa.
// ============================================================

public sealed class MaintenanceService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MaintenanceService> _logger;

    // 6-hour cadence; first tick after a 10-minute warmup so we don't
    // race the cold-start surge of new connections.
    private static readonly TimeSpan Cadence = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(10);

    // Retention windows. Tuned for free-tier survival — bump up later
    // once we're on paid Mongo Atlas tiers and storage is cheap.
    private static readonly TimeSpan GuestUserAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan MessageRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan DmRetention = TimeSpan.FromDays(90);
    private static readonly TimeSpan EndedVideoSessionRetention = TimeSpan.FromDays(7);

    public MaintenanceService(
        IServiceScopeFactory scopeFactory,
        ILogger<MaintenanceService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MaintenanceService started (cadence: {Hours}h)", Cadence.TotalHours);

        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Maintenance tick failed; will retry next cadence");
            }

            try { await Task.Delay(Cadence, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("MaintenanceService stopping");
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        // Each tick gets its own DI scope so the DbContext / Mongo
        // service don't carry state across runs.
        using var scope = _scopeFactory.CreateScope();
        var postgres = scope.ServiceProvider.GetRequiredService<PostgresProcService>();
        var mongo = scope.ServiceProvider.GetRequiredService<MongoService>();

        // ── 1. Guest cleanup (Postgres). Returns the IDs so we can
        //    follow up with Mongo cleanup for their messages.
        var guestIds = new List<Guid>();
        try
        {
            guestIds = await postgres.CleanupOldGuestUsersAsync(GuestUserAge);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Maintenance: guest cleanup proc failed");
        }

        // ── 2. Orphan message cleanup (Mongo).
        long orphanMsgs = 0;
        if (guestIds.Count > 0)
        {
            try
            {
                orphanMsgs = await mongo.DeleteMessagesByUserIdsAsync(
                    guestIds.Select(g => g.ToString()));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Maintenance: orphan message cleanup failed");
            }
        }

        // ── 3. Old room messages (Mongo).
        long oldMsgs = 0;
        try { oldMsgs = await mongo.DeleteMessagesOlderThanAsync(MessageRetention); }
        catch (Exception ex) { _logger.LogWarning(ex, "Maintenance: old message cleanup failed"); }

        // ── 4. Old DMs (Mongo).
        long oldDms = 0;
        try { oldDms = await mongo.DeleteDmsOlderThanAsync(DmRetention); }
        catch (Exception ex) { _logger.LogWarning(ex, "Maintenance: old DM cleanup failed"); }

        // ── 5. Old ENDED video sessions (Mongo).
        long oldSessions = 0;
        try { oldSessions = await mongo.DeleteEndedVideoSessionsOlderThanAsync(EndedVideoSessionRetention); }
        catch (Exception ex) { _logger.LogWarning(ex, "Maintenance: old video session cleanup failed"); }

        _logger.LogInformation(
            "Maintenance tick complete: guests={Guests}, orphanMsgs={OrphanMsgs}, oldMsgs={OldMsgs}, oldDms={OldDms}, oldSessions={OldSessions}",
            guestIds.Count, orphanMsgs, oldMsgs, oldDms, oldSessions);
    }
}
