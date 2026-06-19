using ChatVerse.API.Hubs;
using ChatVerse.API.Services.Personas;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Services;

// ============================================================
//  PersonaResetService — daily 00:00 UTC roll-over.
//
//  Each tick (once per day):
//    1. Compute "today" (UTC). If we've already generated personas
//       for today (idempotency guard), skip.
//    2. Read yesterday's persona_conversations rollup — every (A,B)
//       pair that DM'd at least once counts toward the streak.
//       Call MongoService.BumpStreakForDayAsync for each pair.
//    3. Pull a sample of recent active users from Postgres (we
//       lean on the existing GetRandomActiveUserSampleAsync helper
//       — for the scaffold we generate personas for the SAMPLE,
//       not the whole user base; the full version will paginate
//       across all active users).
//    4. UpsertPersonaAsync for each user in the sample.
//    5. Delete any persona row whose Date is before today.
//
//  Why a daily cadence vs the chat tick:
//    Personas have a deterministic per-day identity. Generating
//    them ahead of time means the first user to ask GetMyPersona()
//    after midnight doesn't pay a generation cost — and the day's
//    persona is the same whether the user logs in at 00:01 or 23:59.
//
//  Scope note: the scaffold ships the loop + idempotency + streak
//  rollover. The full feature (persona DM hub, mutual-unmask
//  handshake, server-push of milestones) will land alongside the
//  frontend page.
// ============================================================

public sealed class PersonaResetService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<PersonaHub> _hub;
    private readonly ILogger<PersonaResetService> _logger;

    // Once per hour we wake up and check whether the UTC date has
    // changed since the last run. If yes, run the rollover. Avoids
    // baking-in a specific tick alignment (the host could be sleeping
    // exactly at 00:00 UTC).
    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
    private const int ActiveUserSample = 500;  // bigger than TimeCapsule because we generate personas for the WHOLE sample, not pick one

    private string _lastProcessedDate = "";

    public PersonaResetService(
        IServiceScopeFactory scopeFactory,
        IHubContext<PersonaHub> hub,
        ILogger<PersonaResetService> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "PersonaResetService started (check cadence: {Mins} min)",
            Cadence.TotalMinutes);

        try { await Task.Delay(StartupDelay, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                if (_lastProcessedDate != today)
                {
                    await RolloverAsync(today, ct);
                    _lastProcessedDate = today;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Persona reset tick failed; will retry next hour");
            }

            try { await Task.Delay(Cadence, ct); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("PersonaResetService stopping");
    }

    private async Task RolloverAsync(string today, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var mongo    = scope.ServiceProvider.GetRequiredService<MongoService>();
        var postgres = scope.ServiceProvider.GetRequiredService<PostgresProcService>();

        // ── 1) Streak rollover from yesterday's conversation rollup.
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        var yesterdayConvos = await mongo.GetConversationsForDayAsync(yesterday);
        int streaksBumped = 0;
        foreach (var c in yesterdayConvos)
        {
            try
            {
                await mongo.BumpStreakForDayAsync(c.RealUserA, c.RealUserB, yesterday);
                streaksBumped++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to bump streak for pair ({A},{B}) on {Day}",
                    c.RealUserA, c.RealUserB, yesterday);
            }
        }

        // ── 2) Generate today's personas for active users.
        var users = await postgres.GetRandomActiveUserSampleAsync(ActiveUserSample);
        int personasMinted = 0;
        foreach (var u in users)
        {
            try
            {
                var p = PersonaGenerator.Generate(u.UserId.ToString(), DateTime.UtcNow);
                await mongo.UpsertPersonaAsync(p);
                personasMinted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to generate persona for user {User}",
                    u.UserId);
            }
        }

        // ── 3) Sweep yesterday-and-older personas. Cheap; the index
        //   makes this an O(log n) range scan.
        var deleted = await mongo.DeletePersonasBeforeAsync(today);

        _logger.LogInformation(
            "Persona rollover for {Day}: personas={Minted}, streaks={Streaks}, sweptOld={Swept}",
            today, personasMinted, streaksBumped, deleted);

        // Server-push of "PersonaRolled" deferred until frontend page
        // lands — the client will fetch fresh via GetMyPersona().
        await Task.CompletedTask;
    }
}
