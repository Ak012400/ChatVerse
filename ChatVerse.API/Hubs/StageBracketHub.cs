using ChatVerse.API.Extensions;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Services.LiveKit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Hubs;

// ============================================================
//  StageBracketHub — shared backend for Debate v2 + Roast.
//
//  See Entities.cs §STAGE BRACKET for the architecture rationale.
//  Mode field on the config distinguishes the templates; mechanics
//  identical.
//
//  Group keys:
//    "sb:{roomId}"     — everyone in the room
//    "sb-host:{roomId}" — host's private channel (full nomination bios)
//    Per-user push:    UserKicked / UserBanned via Clients.User()
// ============================================================

[Authorize]
public class StageBracketHub : Hub
{
    private readonly MongoService _mongo;
    private readonly LiveKitService _liveKit;
    private readonly IHubContext<StageBracketHub> _hubCtx;
    private readonly ILogger<StageBracketHub> _logger;

    public StageBracketHub(
        MongoService mongo, LiveKitService liveKit,
        IHubContext<StageBracketHub> hubCtx, ILogger<StageBracketHub> logger)
    {
        _mongo = mongo;
        _liveKit = liveKit;
        _hubCtx = hubCtx;
        _logger = logger;
    }

    // ── Server-side allowlists ──────────────────────────────
    private static readonly HashSet<string> AllowedModes = new(StringComparer.OrdinalIgnoreCase) { "debate", "roast" };
    private static readonly HashSet<string> AllowedPrivacy = new(StringComparer.OrdinalIgnoreCase) { "public", "private" };
    private static readonly HashSet<string> AllowedSides = new(StringComparer.OrdinalIgnoreCase) { "left", "right" };
    private static readonly HashSet<string> AllowedPreferred = new(StringComparer.OrdinalIgnoreCase) { "left", "right", "either" };
    private static readonly HashSet<string> AllowedIntent = new(StringComparer.OrdinalIgnoreCase) { "seat", "challenge" };
    private static readonly int[] AllowedSecondsPerTurn = { 60, 90, 120 };
    private static readonly int[] AllowedRoundMinutes = { 3, 5, 10 };
    private const int MaxChatChars = 1000;
    private const int MaxNoteChars = 120;
    private const int MaxTopicChars = 200;

    // Topic bank for public Debate rooms — server picks one on round
    // start. Roast rooms always require a host-provided topic.
    private static readonly string[] PublicDebateTopics = new[]
    {
        "Should social media be regulated like tobacco?",
        "Remote work is better than office work — agree or disagree?",
        "AI will replace more jobs than it creates by 2030 — agree or disagree?",
        "Cryptocurrency is the future of money — agree or disagree?",
        "Cancel culture has gone too far — agree or disagree?",
        "School uniforms should be mandatory — agree or disagree?",
        "Tech billionaires should not exist — agree or disagree?",
        "Social media does more harm than good — agree or disagree?",
        "College degrees are overrated — agree or disagree?",
        "Privacy is dead in the digital age — accept or fight?",
    };

    private static string RoomGroup(string roomId) => $"sb:{roomId}";
    private static string HostGroup(string roomId) => $"sb-host:{roomId}";

    private async Task<(MehfilRoom room, bool isHost)> AuthorizeAsync(string roomId, CancellationToken ct, string? inviteCode = null)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var room = await _mongo.GetMehfilRoomByIdAsync(roomId, ct);
        if (room is null) throw new HubException("Room not found.");
        if (!AllowedModes.Contains(room.TemplateKind))
            throw new HubException("Not a stage-bracket room.");

        if (await _mongo.IsBannedFromStageBracketAsync(roomId, meId, ct))
            throw new HubException("You are banned from this room.");

        var cfg = await _mongo.GetStageBracketConfigAsync(roomId, ct);
        if (cfg is not null && string.Equals(cfg.Privacy, "private", StringComparison.OrdinalIgnoreCase))
        {
            var isHostCheck = string.Equals(room.HostUserId, meId, StringComparison.OrdinalIgnoreCase);
            if (!isHostCheck && (string.IsNullOrWhiteSpace(inviteCode) ||
                                 !string.Equals(inviteCode.Trim(), cfg.InviteCode, StringComparison.OrdinalIgnoreCase)))
                throw new HubException("Invite code required.");
        }

        return (room, string.Equals(room.HostUserId, meId, StringComparison.OrdinalIgnoreCase));
    }

    private void RequireHost(bool isHost) { if (!isHost) throw new HubException("Host only."); }

    // ─── Lifecycle ────────────────────────────────────────────

    public async Task<object> JoinStageRoom(string roomId, string? inviteCode)
    {
        var ct = Context.ConnectionAborted;
        var (room, isHost) = await AuthorizeAsync(roomId, ct, inviteCode);
        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(roomId), ct);
        if (isHost) await Groups.AddToGroupAsync(Context.ConnectionId, HostGroup(roomId), ct);
        return await BuildRoomStateAsync(room, isHost, ct);
    }

    public async Task LeaveStageRoom(string roomId)
    {
        var ct = Context.ConnectionAborted;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(roomId), ct);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, HostGroup(roomId), ct);
    }

    public Task<object> GetRoomState(string roomId) => JoinStageRoom(roomId, null);

    // ─── Config ───────────────────────────────────────────────

    public async Task ConfigureRoom(
        string roomId, string mode, string privacy,
        int secondsPerTurn, int roundDurationMinutes, int challengeSlotSeconds,
        string? hostTopic)
    {
        var ct = Context.ConnectionAborted;
        var (room, isHost) = await AuthorizeAsync(roomId, ct);
        RequireHost(isHost);

        if (!AllowedModes.Contains(mode)) throw new HubException("Mode must be debate or roast.");
        if (!AllowedPrivacy.Contains(privacy)) throw new HubException("Privacy public or private.");
        if (!AllowedSecondsPerTurn.Contains(secondsPerTurn))
            throw new HubException("Seconds per turn must be 60, 90 or 120.");
        if (!AllowedRoundMinutes.Contains(roundDurationMinutes))
            throw new HubException("Round duration 3, 5 or 10 min.");
        if (challengeSlotSeconds is < 15 or > 120)
            throw new HubException("Challenge slot 15-120 seconds.");
        var topic = (hostTopic ?? "").Trim();
        if (topic.Length > MaxTopicChars) topic = topic.Substring(0, MaxTopicChars);

        var existing = await _mongo.GetStageBracketConfigAsync(roomId, ct);
        var cfg = existing ?? new StageBracketConfig
        {
            RoomId     = roomId,
            HostUserId = room.HostUserId,
            CreatedAt  = DateTime.UtcNow,
        };
        cfg.Mode = mode.ToLowerInvariant();
        cfg.Privacy = privacy.ToLowerInvariant();
        cfg.SecondsPerTurn = secondsPerTurn;
        cfg.RoundDurationMinutes = roundDurationMinutes;
        cfg.ChallengeSlotSeconds = challengeSlotSeconds;
        cfg.HostTopic = string.IsNullOrEmpty(topic) ? null : topic;
        if (cfg.Privacy == "private" && string.IsNullOrEmpty(cfg.InviteCode))
            cfg.InviteCode = GenerateInviteCode();
        if (cfg.Privacy == "public") cfg.InviteCode = null;
        await _mongo.UpsertStageBracketConfigAsync(cfg, ct);

        await _mongo.LogStageBracketHostActionAsync(new StageBracketHostAction
        {
            RoomId = roomId, HostUserId = room.HostUserId, TargetUserId = room.HostUserId,
            ActionType = "configure", At = DateTime.UtcNow,
        }, ct);

        var state = await BuildRoomStateAsync(room, isHost: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoomStateChanged", state, ct);
    }

    // ─── Round lifecycle ──────────────────────────────────────

    public async Task<string?> StartRound(string roomId)
    {
        var ct = Context.ConnectionAborted;
        var (room, isHost) = await AuthorizeAsync(roomId, ct);
        RequireHost(isHost);

        var existing = await _mongo.GetActiveStageBracketRoundAsync(roomId, ct);
        if (existing is not null) throw new HubException("A round is already in progress.");

        var cfg = await _mongo.GetStageBracketConfigAsync(roomId, ct);
        if (cfg is null) throw new HubException("Configure the room first.");

        // Pick topic — host-provided if present, else random for public
        // Debate rooms. Roast rooms ALWAYS need a host topic.
        var topic = cfg.HostTopic;
        if (string.IsNullOrWhiteSpace(topic))
        {
            if (string.Equals(cfg.Mode, "roast", StringComparison.OrdinalIgnoreCase))
                throw new HubException("Roast rooms need a host-provided topic.");
            topic = PublicDebateTopics[Random.Shared.Next(PublicDebateTopics.Length)];
        }

        var round = new StageBracketRound
        {
            RoomId    = roomId,
            Mode      = cfg.Mode,
            Topic     = topic,
            Status    = "open_seats",
            CreatedAt = DateTime.UtcNow,
        };
        await _mongo.InsertStageBracketRoundAsync(round, ct);
        await _mongo.SeedStageBracketSeatsAsync(roomId, round.Id!, ct);

        await _mongo.LogStageBracketHostActionAsync(new StageBracketHostAction
        {
            RoomId = roomId, HostUserId = room.HostUserId, TargetUserId = room.HostUserId,
            ActionType = "start_round", Reason = $"topic={topic}", At = DateTime.UtcNow,
        }, ct);

        var state = await BuildRoomStateAsync(room, isHost: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoundOpened", state, ct);
        return round.Id;
    }

    public async Task GoLive(string roomId)
    {
        var ct = Context.ConnectionAborted;
        var (room, isHost) = await AuthorizeAsync(roomId, ct);
        RequireHost(isHost);

        var round = await _mongo.GetActiveStageBracketRoundAsync(roomId, ct);
        if (round is null) throw new HubException("No round open.");
        if (round.Status == "live") return;

        var cfg = (await _mongo.GetStageBracketConfigAsync(roomId, ct))!;
        var now = DateTime.UtcNow;
        var endsAt = now.AddMinutes(cfg.RoundDurationMinutes);
        var turnEndsAt = now.AddSeconds(cfg.SecondsPerTurn);

        await _mongo.PatchStageBracketRoundAsync(round.Id!,
            status: "live",
            activeSide: "left", activeSeatPosition: 0,
            nextLeftPosition: 1, nextRightPosition: 0,
            startedAt: now, endsAt: endsAt, currentTurnEndsAt: turnEndsAt,
            ct: ct);

        // Open a turn log entry for whoever is on Left Seat 0 (if seated).
        var seat = await _mongo.GetStageBracketSeatAsync(round.Id!, "left", 0, ct);
        if (seat?.OccupantUserId is string occUid)
        {
            await _mongo.InsertStageBracketTurnAsync(new StageBracketTurn
            {
                RoomId = roomId, RoundId = round.Id!,
                Side = "left", SeatPosition = 0,
                SpeakerUserId = occUid, SpeakerUsername = seat.OccupantUsername ?? "",
                Kind = "normal", StartedAt = now,
            }, ct);
        }

        var state = await BuildRoomStateAsync(room, isHost: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoundLive", state, ct);
    }

    public async Task EndRound(string roomId)
    {
        var ct = Context.ConnectionAborted;
        var (room, isHost) = await AuthorizeAsync(roomId, ct);
        RequireHost(isHost);

        var round = await _mongo.GetActiveStageBracketRoundAsync(roomId, ct);
        if (round is null) return;

        await _mongo.EndOpenStageBracketTurnsForRoundAsync(round.Id!, ct);
        await _mongo.PatchStageBracketRoundAsync(round.Id!,
            status: "ended", endedAt: DateTime.UtcNow,
            clearChallenger: true, ct: ct);

        await _mongo.LogStageBracketHostActionAsync(new StageBracketHostAction
        {
            RoomId = roomId, HostUserId = room.HostUserId, TargetUserId = room.HostUserId,
            ActionType = "end_round", At = DateTime.UtcNow,
        }, ct);

        var state = await BuildRoomStateAsync(room, isHost: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoundEnded", state, ct);
    }

    // ─── Nominations + seat assignment ─────────────────────────

    public async Task<string?> NominateForSeat(string roomId, string preferredSide, string note)
    {
        var ct = Context.ConnectionAborted;
        await AuthorizeAsync(roomId, ct);
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);

        var round = await _mongo.GetActiveStageBracketRoundAsync(roomId, ct);
        if (round is null) throw new HubException("No round to join.");
        if (round.Status == "ended") throw new HubException("Round has ended.");

        if (await _mongo.IsUserSeatedStageBracketAsync(round.Id!, meId, ct))
            throw new HubException("You're already on a seat.");

        if (!AllowedPreferred.Contains(preferredSide))
            throw new HubException("Side must be left, right or either.");
        var clean = (note ?? "").Trim();
        if (clean.Length > MaxNoteChars) clean = clean.Substring(0, MaxNoteChars);

        var nom = new StageBracketNomination
        {
            RoomId = roomId, RoundId = round.Id!,
            UserId = meId, Username = username,
            Intent = "seat",
            PreferredSide = preferredSide.ToLowerInvariant(),
            Note = clean,
            Status = "pending",
            RaisedAt = DateTime.UtcNow,
        };
        var inserted = await _mongo.InsertStageBracketNominationAsync(nom, ct);
        if (inserted is null) return null;

        var cfg = await _mongo.GetStageBracketConfigAsync(roomId, ct);
        var count = await _mongo.CountPendingStageBracketNominationsAsync(round.Id!, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("NominationCountChanged", new { count }, ct);
        await _hubCtx.Clients.Group(HostGroup(roomId)).SendAsync("NominationRaisedPrivate", ShapeNomination(inserted), ct);

        // PUBLIC mode: server auto-seats the nominee if a seat on their
        // preferred side is open. First-come-first-seated.
        if (cfg is not null && string.Equals(cfg.Privacy, "public", StringComparison.OrdinalIgnoreCase))
        {
            await TryAutoSeatAsync(roomId, round, inserted, ct);
        }

        return inserted.Id;
    }

    public async Task WithdrawNomination(string roomId)
    {
        var ct = Context.ConnectionAborted;
        await AuthorizeAsync(roomId, ct);
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var round = await _mongo.GetActiveStageBracketRoundAsync(roomId, ct);
        if (round is null) return;
        await _mongo.WithdrawStageBracketNominationAsync(round.Id!, meId, "seat", ct);

        var count = await _mongo.CountPendingStageBracketNominationsAsync(round.Id!, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("NominationCountChanged", new { count }, ct);
        await _hubCtx.Clients.Group(HostGroup(roomId)).SendAsync("NominationWithdrawnPrivate", new { userId = meId }, ct);
    }

    public async Task<string?> RaiseHandToChallenge(string roomId, string note)
    {
        var ct = Context.ConnectionAborted;
        await AuthorizeAsync(roomId, ct);
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);

        var round = await _mongo.GetActiveStageBracketRoundAsync(roomId, ct);
        if (round is null || round.Status != "live")
            throw new HubException("Challenges only during a live round.");
        if (await _mongo.IsUserSeatedStageBracketAsync(round.Id!, meId, ct))
            throw new HubException("Seated speakers can't challenge.");

        var clean = (note ?? "").Trim();
        if (clean.Length > MaxNoteChars) clean = clean.Substring(0, MaxNoteChars);

        var nom = new StageBracketNomination
        {
            RoomId = roomId, RoundId = round.Id!,
            UserId = meId, Username = username,
            Intent = "challenge",
            Note = clean,
            Status = "pending",
            RaisedAt = DateTime.UtcNow,
        };
        var inserted = await _mongo.InsertStageBracketNominationAsync(nom, ct);
        if (inserted is null) return null;

        await _hubCtx.Clients.Group(HostGroup(roomId))
            .SendAsync("ChallengeRaisedPrivate", ShapeNomination(inserted), ct);
        return inserted.Id;
    }

    public async Task ApproveChallenge(string roomId, string nominationId)
    {
        var ct = Context.ConnectionAborted;
        var (room, isHost) = await AuthorizeAsync(roomId, ct);
        RequireHost(isHost);

        var round = await _mongo.GetActiveStageBracketRoundAsync(roomId, ct);
        if (round is null || round.Status != "live") throw new HubException("No live round.");
        if (round.ChallengerUserId is not null) throw new HubException("A challenge slot is already live.");

        var nom = await _mongo.GetStageBracketNominationAsync(nominationId, ct);
        if (nom is null || nom.Intent != "challenge" || nom.Status != "pending" || nom.RoundId != round.Id)
            throw new HubException("Challenge not available.");

        var cfg = (await _mongo.GetStageBracketConfigAsync(roomId, ct))!;
        var endsAt = DateTime.UtcNow.AddSeconds(cfg.ChallengeSlotSeconds);

        // Close the original speaker's open turn (challenger takes mic).
        await _mongo.EndOpenStageBracketTurnsForRoundAsync(round.Id!, ct);
        await _mongo.PatchStageBracketRoundAsync(round.Id!,
            challengerUserId: nom.UserId,
            challengerUsername: nom.Username,
            challengerEndsAt: endsAt, ct: ct);
        await _mongo.SetStageBracketNominationStatusAsync(nominationId, "accepted", ct);

        await _mongo.InsertStageBracketTurnAsync(new StageBracketTurn
        {
            RoomId = roomId, RoundId = round.Id!,
            Side = round.ActiveSide, SeatPosition = round.ActiveSeatPosition,
            SpeakerUserId = nom.UserId, SpeakerUsername = nom.Username,
            Kind = "challenge", StartedAt = DateTime.UtcNow,
        }, ct);

        await _mongo.LogStageBracketHostActionAsync(new StageBracketHostAction
        {
            RoomId = roomId, HostUserId = room.HostUserId, TargetUserId = nom.UserId,
            ActionType = "approve_challenge", At = DateTime.UtcNow,
        }, ct);

        var state = await BuildRoomStateAsync(room, isHost: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("ChallengeStarted", state, ct);
    }

    public async Task RejectChallenge(string roomId, string nominationId)
    {
        var ct = Context.ConnectionAborted;
        var (_, isHost) = await AuthorizeAsync(roomId, ct);
        RequireHost(isHost);
        await _mongo.SetStageBracketNominationStatusAsync(nominationId, "rejected", ct);
        await _hubCtx.Clients.Group(HostGroup(roomId))
            .SendAsync("ChallengeWithdrawnPrivate", new { nominationId }, ct);
    }

    public async Task AssignSeat(string roomId, string nominationId, string side, int position)
    {
        var ct = Context.ConnectionAborted;
        var (room, isHost) = await AuthorizeAsync(roomId, ct);
        RequireHost(isHost);

        if (!AllowedSides.Contains(side)) throw new HubException("Side must be left or right.");
        if (position is < 0 or > 4) throw new HubException("Position 0..4.");

        var round = await _mongo.GetActiveStageBracketRoundAsync(roomId, ct);
        if (round is null) throw new HubException("No round.");

        var nom = await _mongo.GetStageBracketNominationAsync(nominationId, ct);
        if (nom is null || nom.Intent != "seat" || nom.Status != "pending" || nom.RoundId != round.Id)
            throw new HubException("Nomination not available.");

        var ok = await _mongo.TryAssignStageBracketSeatAsync(round.Id!, side.ToLowerInvariant(), position,
            nom.UserId, nom.Username, ct);
        if (!ok) throw new HubException("Seat already taken.");

        await _mongo.SetStageBracketNominationStatusAsync(nominationId, "accepted", ct);
        await _mongo.LogStageBracketHostActionAsync(new StageBracketHostAction
        {
            RoomId = roomId, HostUserId = room.HostUserId, TargetUserId = nom.UserId,
            ActionType = "assign_seat", Reason = $"{side}/{position}", At = DateTime.UtcNow,
        }, ct);

        var state = await BuildRoomStateAsync(room, isHost: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("SeatChanged", state, ct);
    }

    public async Task UnseatUser(string roomId, string targetUserId)
    {
        var ct = Context.ConnectionAborted;
        var (room, isHost) = await AuthorizeAsync(roomId, ct);
        RequireHost(isHost);
        var round = await _mongo.GetActiveStageBracketRoundAsync(roomId, ct);
        if (round is null) return;
        await _mongo.UnassignStageBracketSeatAsync(round.Id!, targetUserId, ct);
        await _mongo.LogStageBracketHostActionAsync(new StageBracketHostAction
        {
            RoomId = roomId, HostUserId = room.HostUserId, TargetUserId = targetUserId,
            ActionType = "unseat", At = DateTime.UtcNow,
        }, ct);
        var state = await BuildRoomStateAsync(room, isHost: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("SeatChanged", state, ct);
    }

    public async Task<List<object>> GetNominationsForHost(string roomId, string? intent)
    {
        var ct = Context.ConnectionAborted;
        var (_, isHost) = await AuthorizeAsync(roomId, ct);
        RequireHost(isHost);
        var round = await _mongo.GetActiveStageBracketRoundAsync(roomId, ct);
        if (round is null) return new List<object>();
        var rows = await _mongo.GetPendingStageBracketNominationsAsync(round.Id!, intent, ct);
        return rows.Select(ShapeNomination).ToList();
    }

    // ─── Moderation ───────────────────────────────────────────

    public async Task KickFromStage(string roomId, string targetUserId, string? reason)
    {
        var ct = Context.ConnectionAborted;
        var (room, isHost) = await AuthorizeAsync(roomId, ct);
        RequireHost(isHost);
        var round = await _mongo.GetActiveStageBracketRoundAsync(roomId, ct);
        if (round is not null) await _mongo.UnassignStageBracketSeatAsync(round.Id!, targetUserId, ct);
        await _mongo.LogStageBracketHostActionAsync(new StageBracketHostAction
        {
            RoomId = roomId, HostUserId = room.HostUserId, TargetUserId = targetUserId,
            ActionType = "kick", Reason = reason, At = DateTime.UtcNow,
        }, ct);
        await _hubCtx.Clients.User(targetUserId).SendAsync("UserKicked", new { roomId, reason }, ct);
        var state = await BuildRoomStateAsync(room, isHost: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoomStateChanged", state, ct);
    }

    public async Task BanFromStage(string roomId, string targetUserId, string? reason)
    {
        var ct = Context.ConnectionAborted;
        var (room, isHost) = await AuthorizeAsync(roomId, ct);
        RequireHost(isHost);
        await _mongo.BanFromStageBracketAsync(new StageBracketBan
        {
            RoomId = roomId, UserId = targetUserId, HostUserId = room.HostUserId,
            Reason = reason, CreatedAt = DateTime.UtcNow,
        }, ct);
        var round = await _mongo.GetActiveStageBracketRoundAsync(roomId, ct);
        if (round is not null) await _mongo.UnassignStageBracketSeatAsync(round.Id!, targetUserId, ct);
        await _mongo.LogStageBracketHostActionAsync(new StageBracketHostAction
        {
            RoomId = roomId, HostUserId = room.HostUserId, TargetUserId = targetUserId,
            ActionType = "ban", Reason = reason, At = DateTime.UtcNow,
        }, ct);
        await _hubCtx.Clients.User(targetUserId).SendAsync("UserBanned", new { roomId, reason }, ct);
        var state = await BuildRoomStateAsync(room, isHost: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoomStateChanged", state, ct);
    }

    // ─── Chat ─────────────────────────────────────────────────

    public async Task<string?> SendChatMessage(string roomId, string content)
    {
        var ct = Context.ConnectionAborted;
        var (_, isHost) = await AuthorizeAsync(roomId, ct);
        var trimmed = (content ?? "").Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Length > MaxChatChars) trimmed = trimmed.Substring(0, MaxChatChars);

        var meId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);
        var round = await _mongo.GetActiveStageBracketRoundAsync(roomId, ct);
        var seated = round is not null && await _mongo.IsUserSeatedStageBracketAsync(round.Id!, meId, ct);

        var msg = new StageBracketChatMessage
        {
            RoomId = roomId, SenderUserId = meId, SenderUsername = username,
            SenderIsHost = isHost, SenderIsSeated = seated,
            Content = trimmed, CreatedAt = DateTime.UtcNow,
        };
        var saved = await _mongo.InsertStageBracketChatAsync(msg, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("ChatMessage", ShapeChat(saved), ct);
        return saved.Id;
    }

    // ─── LiveKit token (current speaker = canPublish) ─────────

    public async Task<object?> GetActiveMicToken(string roomId)
    {
        var ct = Context.ConnectionAborted;
        await AuthorizeAsync(roomId, ct);
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);

        var round = await _mongo.GetActiveStageBracketRoundAsync(roomId, ct);
        if (round is null || round.Status != "live") return null;

        // Decide active speaker: challenger if set, else seat occupant.
        var canPublish = false;
        if (round.ChallengerUserId is string challengerId)
        {
            canPublish = string.Equals(challengerId, meId, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var seat = await _mongo.GetStageBracketSeatAsync(round.Id!, round.ActiveSide, round.ActiveSeatPosition, ct);
            canPublish = seat?.OccupantUserId is string occ
                         && string.Equals(occ, meId, StringComparison.OrdinalIgnoreCase);
        }

        // LiveKit room name keyed to the ROUND so a new round = fresh
        // audio room (no carry-over of stale connections).
        var lkRoom = $"sb-round-{round.Id}";
        await _liveKit.CreateRoomAsync(lkRoom, emptyTimeoutSeconds: 120, maxParticipants: 100);
        var token = _liveKit.GenerateToken(lkRoom, meId, username, canPublish, canSubscribe: true);
        return new
        {
            roomName  = lkRoom,
            serverUrl = _liveKit.GetServerUrl(),
            token,
            canPublish,
        };
    }

    // ─── Helpers ─────────────────────────────────────────────

    private async Task TryAutoSeatAsync(string roomId, StageBracketRound round, StageBracketNomination nom, CancellationToken ct)
    {
        // Try preferred side first, then either side.
        var sides = nom.PreferredSide switch
        {
            "left" => new[] { "left" },
            "right" => new[] { "right" },
            _ => new[] { "left", "right" },
        };
        foreach (var side in sides)
        {
            var pos = await _mongo.FindEmptyStageBracketSeatAsync(round.Id!, side, ct);
            if (pos.HasValue)
            {
                var ok = await _mongo.TryAssignStageBracketSeatAsync(round.Id!, side, pos.Value,
                    nom.UserId, nom.Username, ct);
                if (ok)
                {
                    await _mongo.SetStageBracketNominationStatusAsync(nom.Id!, "accepted", ct);
                    var room = await _mongo.GetMehfilRoomByIdAsync(roomId, ct);
                    if (room is not null)
                    {
                        var state = await BuildRoomStateAsync(room, isHost: false, ct);
                        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("SeatChanged", state, ct);
                    }
                    return;
                }
            }
        }
    }

    private async Task<object> BuildRoomStateAsync(MehfilRoom room, bool isHost, CancellationToken ct)
    {
        var cfg = await _mongo.GetStageBracketConfigAsync(room.Id!, ct);
        var round = await _mongo.GetActiveStageBracketRoundAsync(room.Id!, ct);
        List<StageBracketSeat> seats = new();
        int pendingCount = 0;
        if (round is not null)
        {
            seats = await _mongo.GetStageBracketSeatsAsync(round.Id!, ct);
            pendingCount = await _mongo.CountPendingStageBracketNominationsAsync(round.Id!, ct);
        }
        var chat = await _mongo.GetStageBracketChatAsync(room.Id!, 80, ct);

        return new
        {
            room = new
            {
                id           = room.Id,
                title        = room.Title,
                hostUserId   = room.HostUserId,
                hostUsername = room.HostUsername,
                status       = room.Status,
            },
            isHost,
            config = cfg is null ? null : new
            {
                mode                 = cfg.Mode,
                privacy              = cfg.Privacy,
                inviteCode           = isHost ? cfg.InviteCode : null,
                secondsPerTurn       = cfg.SecondsPerTurn,
                roundDurationMinutes = cfg.RoundDurationMinutes,
                challengeSlotSeconds = cfg.ChallengeSlotSeconds,
                hostTopic            = cfg.HostTopic,
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
                id               = s.Id,
                side             = s.Side,
                position         = s.Position,
                occupantUserId   = s.OccupantUserId,
                occupantUsername = s.OccupantUsername,
                secondsSpoken    = s.SecondsSpoken,
            }).ToList(),
            pendingNominationCount = pendingCount,
            messages = chat.Select(ShapeChat).ToList(),
        };
    }

    private static object ShapeChat(StageBracketChatMessage m) => new
    {
        id             = m.Id,
        senderUserId   = m.SenderUserId,
        senderUsername = m.SenderUsername,
        senderIsHost   = m.SenderIsHost,
        senderIsSeated = m.SenderIsSeated,
        content        = m.Content,
        createdAt      = m.CreatedAt,
    };

    /// <summary>Privileged — bios + note. Only host sub-channel ever sees this.</summary>
    private static object ShapeNomination(StageBracketNomination n) => new
    {
        id            = n.Id,
        userId        = n.UserId,
        username      = n.Username,
        intent        = n.Intent,
        preferredSide = n.PreferredSide,
        note          = n.Note,
        raisedAt      = n.RaisedAt,
    };

    private static string GenerateInviteCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = new byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var sb = new System.Text.StringBuilder(8);
        foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
        return sb.ToString();
    }
}
