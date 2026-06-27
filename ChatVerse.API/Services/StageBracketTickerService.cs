using ChatVerse.API.Hubs;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Services;

// ============================================================
//  StageBracketTickerService — keeps every live round moving.
//
//  Runs at 1-second cadence (rounds are short, turn timers matter).
//  For each live round:
//    1. If a challenger is on-stage and ChallengerEndsAt elapsed →
//       end challenger turn + return mic to the original speaker.
//    2. If CurrentTurnEndsAt elapsed (and no challenger active) →
//       advance to the next speaker (alternate sides, cycle within
//       side). Insert turn audit row. Update the round.
//    3. If EndsAt elapsed → mark round ended + close turn log.
//
//  After every mutation we push RoundUpdated to the room group so
//  clients see live progress without polling.
//
//  Stale-resume safety: ticker reads ALL live rounds on every tick
//  via `GetAllLiveStageBracketRoundsAsync` — survives backend
//  restart cleanly.
// ============================================================

public sealed class StageBracketTickerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<StageBracketHub> _hub;
    private readonly ILogger<StageBracketTickerService> _logger;

    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    public StageBracketTickerService(
        IServiceScopeFactory scopeFactory,
        IHubContext<StageBracketHub> hub,
        ILogger<StageBracketTickerService> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StageBracketTickerService started (cadence: 1s)");
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StageBracketTickerService tick failed");
            }
            try { await Task.Delay(Cadence, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<MongoService>();

        var live = await mongo.GetAllLiveStageBracketRoundsAsync(ct);
        if (live.Count == 0) return;

        var nowUtc = DateTime.UtcNow;
        foreach (var round in live)
        {
            if (ct.IsCancellationRequested) return;
            try { await TickRoundAsync(mongo, round, nowUtc, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tick failed for round {RoundId}", round.Id);
            }
        }
    }

    private async Task TickRoundAsync(MongoService mongo, StageBracketRound round, DateTime nowUtc, CancellationToken ct)
    {
        // 1) Round expiry — overrides everything.
        if (round.EndsAt.HasValue && nowUtc >= round.EndsAt.Value)
        {
            await mongo.EndOpenStageBracketTurnsForRoundAsync(round.Id!, ct);
            await mongo.PatchStageBracketRoundAsync(round.Id!,
                status: "ended", endedAt: nowUtc, clearChallenger: true, ct: ct);
            await PushAsync(round.RoomId, "RoundEnded", await BuildState(mongo, round.RoomId, ct), ct);
            return;
        }

        // 2) Challenger slot expiry — revert to original speaker.
        if (round.ChallengerUserId is not null && round.ChallengerEndsAt is DateTime cEnd && nowUtc >= cEnd)
        {
            await mongo.EndOpenStageBracketTurnsForRoundAsync(round.Id!, ct);
            await mongo.PatchStageBracketRoundAsync(round.Id!, clearChallenger: true, ct: ct);

            // Re-open the original speaker's turn for what time remains.
            var seat = await mongo.GetStageBracketSeatAsync(round.Id!, round.ActiveSide, round.ActiveSeatPosition, ct);
            if (seat?.OccupantUserId is string occ)
            {
                await mongo.InsertStageBracketTurnAsync(new StageBracketTurn
                {
                    RoomId = round.RoomId, RoundId = round.Id!,
                    Side = round.ActiveSide, SeatPosition = round.ActiveSeatPosition,
                    SpeakerUserId = occ, SpeakerUsername = seat.OccupantUsername ?? "",
                    Kind = "normal", StartedAt = nowUtc,
                }, ct);
            }
            await PushAsync(round.RoomId, "ChallengeEnded", await BuildState(mongo, round.RoomId, ct), ct);
            return;
        }

        // 3) Current turn expiry (skip if challenger live — that has
        //    its own timer above).
        if (round.ChallengerUserId is null
            && round.CurrentTurnEndsAt.HasValue
            && nowUtc >= round.CurrentTurnEndsAt.Value)
        {
            await AdvanceTurnAsync(mongo, round, nowUtc, ct);
            await PushAsync(round.RoomId, "TurnAdvanced", await BuildState(mongo, round.RoomId, ct), ct);
            return;
        }
    }

    private async Task AdvanceTurnAsync(MongoService mongo, StageBracketRound round, DateTime nowUtc, CancellationToken ct)
    {
        // Tally seconds spoken for the seat that just finished.
        var cfg = await mongo.GetStageBracketConfigAsync(round.RoomId, ct);
        var turnLen = cfg?.SecondsPerTurn ?? 90;
        await mongo.IncrementStageBracketSeatSecondsAsync(round.Id!, round.ActiveSide, round.ActiveSeatPosition, turnLen, ct);

        // Close any open turn audit rows.
        await mongo.EndOpenStageBracketTurnsForRoundAsync(round.Id!, ct);

        // Pick next side + seat — alternate sides each turn.
        var nextSide = round.ActiveSide == "left" ? "right" : "left";
        int nextPos = nextSide == "left" ? round.NextLeftPosition : round.NextRightPosition;
        int nextLeft = round.NextLeftPosition;
        int nextRight = round.NextRightPosition;

        // Try to find a seated occupant by cycling within the side.
        StageBracketSeat? seat = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            seat = await mongo.GetStageBracketSeatAsync(round.Id!, nextSide, nextPos, ct);
            if (seat?.OccupantUserId is not null) break;
            nextPos = (nextPos + 1) % 5;
        }

        // No occupants on next side — end the round.
        if (seat?.OccupantUserId is null)
        {
            await mongo.PatchStageBracketRoundAsync(round.Id!,
                status: "ended", endedAt: nowUtc, ct: ct);
            return;
        }

        // Bump the cycle index for THIS side so the NEXT visit to this
        // side picks the seat after `nextPos`.
        if (nextSide == "left") nextLeft = (nextPos + 1) % 5;
        else nextRight = (nextPos + 1) % 5;

        var newTurnEnd = nowUtc.AddSeconds(turnLen);
        await mongo.PatchStageBracketRoundAsync(round.Id!,
            activeSide: nextSide,
            activeSeatPosition: nextPos,
            nextLeftPosition: nextLeft,
            nextRightPosition: nextRight,
            currentTurnEndsAt: newTurnEnd,
            ct: ct);

        await mongo.InsertStageBracketTurnAsync(new StageBracketTurn
        {
            RoomId = round.RoomId, RoundId = round.Id!,
            Side = nextSide, SeatPosition = nextPos,
            SpeakerUserId = seat.OccupantUserId,
            SpeakerUsername = seat.OccupantUsername ?? "",
            Kind = "normal", StartedAt = nowUtc,
        }, ct);
    }

    private async Task<object?> BuildState(MongoService mongo, string roomId, CancellationToken ct)
    {
        var room = await mongo.GetMehfilRoomByIdAsync(roomId, ct);
        if (room is null) return null;
        // Minimal projection that mirrors StageBracketHub.BuildRoomStateAsync
        // but without inflating the audit log — clients re-request full
        // state via GetRoomState if they want everything.
        var cfg = await mongo.GetStageBracketConfigAsync(roomId, ct);
        var round = await mongo.GetActiveStageBracketRoundAsync(roomId, ct);
        var seats = round is not null ? await mongo.GetStageBracketSeatsAsync(round.Id!, ct) : new();
        return new
        {
            roomId,
            config = cfg is null ? null : new
            {
                mode                 = cfg.Mode,
                privacy              = cfg.Privacy,
                secondsPerTurn       = cfg.SecondsPerTurn,
                roundDurationMinutes = cfg.RoundDurationMinutes,
                challengeSlotSeconds = cfg.ChallengeSlotSeconds,
            },
            round = round is null ? null : new
            {
                id                  = round.Id,
                mode                = round.Mode,
                topic               = round.Topic,
                status              = round.Status,
                activeSide          = round.ActiveSide,
                activeSeatPosition  = round.ActiveSeatPosition,
                startedAt           = round.StartedAt,
                endsAt              = round.EndsAt,
                currentTurnEndsAt   = round.CurrentTurnEndsAt,
                endedAt             = round.EndedAt,
                challengerUserId    = round.ChallengerUserId,
                challengerUsername  = round.ChallengerUsername,
                challengerEndsAt    = round.ChallengerEndsAt,
            },
            seats = seats.Select(s => new
            {
                id = s.Id, side = s.Side, position = s.Position,
                occupantUserId = s.OccupantUserId, occupantUsername = s.OccupantUsername,
                secondsSpoken = s.SecondsSpoken,
            }).ToList(),
        };
    }

    private Task PushAsync(string roomId, string evt, object? payload, CancellationToken ct)
    {
        if (payload is null) return Task.CompletedTask;
        return _hub.Clients.Group($"sb:{roomId}").SendAsync(evt, payload, ct);
    }
}
