using ChatVerse.API.Extensions;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Hubs;

// ============================================================
//  DebateHub — first per-template Mehfil specialisation.
//
//  Architecture (per the per-feature isolation policy):
//   • STANDALONE Hub at /hubs/debate. Doesn't extend MehfilHub.
//   • Uses MehfilRoom only for identity (room id + host id + audience
//     cap). All debate gameplay state lives in debate_* collections.
//   • Group key: "debate:{roomId}". Audience joins on entry.
//   • Monitor (= MehfilRoom.HostUserId) gets a privileged sub-channel:
//     "debate-monitor:{roomId}" — nomination bios fan to that only.
//
//  Privacy contract:
//   • Public events serialise USERNAMES only on seats + chat.
//   • Nomination bios (real name + age + gender) are PRIVATE — they
//     surface ONLY via GetNominationsForMonitor() return value OR
//     NominationRaisedPrivate push to the monitor's private group.
//   • Audience NEVER sees bios.
//
//  Method roster:
//   Monitor:
//     • OpenRound(format)                     — 1v1..5v5
//     • EndRound()                            — closes early
//     • AssignSeat(nominationId, side, pos)   — seat from nominee
//     • UnseatUser(targetUserId, reason?)
//     • KickFromDebate(targetUserId, reason?) — removes from group + state
//     • BanFromDebate(targetUserId, reason?)  — kick + persist ban
//     • HighlightGoodQuestion(targetUserId, messageId?)
//     • GetNominationsForMonitor()            — PRIVILEGED bios
//   Audience:
//     • NominateForSeat(realName, age, gender, preferredSide)
//     • WithdrawNomination()
//     • SendChatMessage(content)
//     • SendQuestion(content)
//   View:
//     • GetRoomState()                        — full snapshot (no bios)
//
//  Push events (public, fanned to debate:{roomId}):
//   • RoundOpened, RoundEnded, RoundUpdated
//   • SeatChanged                             — seat occupied/freed
//   • NominationCountChanged                  — public count only
//   • ChatMessage                             — including IsQuestion flag
//   • UserHighlighted                         — flagged for "good question"
//   • UserKicked / UserBanned                 — target id + reason
//
//  Push events (PRIVATE, fanned to debate-monitor:{roomId}):
//   • NominationRaisedPrivate                 — full bio
//   • NominationWithdrawnPrivate              — id only
// ============================================================

[Authorize]
public class DebateHub : Hub
{
    private readonly MongoService _mongo;
    private readonly IHubContext<DebateHub> _hubCtx;
    private readonly ILogger<DebateHub> _logger;

    // Format -> seats per side. The bracket is symmetric (Pro = Con count).
    private static readonly Dictionary<string, int> FormatSeatsPerSide = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1v1"] = 1,
        ["2v2"] = 2,
        ["3v3"] = 3,
        ["4v4"] = 4,
        ["5v5"] = 5,
    };

    private const int MaxChatChars = 1000;
    private const int MaxNameChars = 80;
    private const int MaxReasonChars = 200;

    private static readonly HashSet<string> AllowedSides = new(StringComparer.OrdinalIgnoreCase) { "pro", "con" };
    private static readonly HashSet<string> AllowedPreferred = new(StringComparer.OrdinalIgnoreCase) { "pro", "con", "either" };
    private static readonly HashSet<string> AllowedGenders = new(StringComparer.OrdinalIgnoreCase) { "male", "female", "other", "" };

    public DebateHub(
        MongoService mongo,
        IHubContext<DebateHub> hubCtx,
        ILogger<DebateHub> logger)
    {
        _mongo = mongo;
        _hubCtx = hubCtx;
        _logger = logger;
    }

    private static string RoomGroup(string roomId) => $"debate:{roomId}";
    private static string MonitorGroup(string roomId) => $"debate-monitor:{roomId}";

    /// <summary>Look up the parent MehfilRoom + verify caller can enter
    /// (not banned, room exists, room is live). Returns null if any
    /// gate fails — caller throws HubException.</summary>
    private async Task<(MehfilRoom room, bool isMonitor)> AuthorizeAsync(
        string roomId, CancellationToken ct)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();

        var room = await _mongo.GetMehfilRoomByIdAsync(roomId, ct);
        if (room is null) throw new HubException("Room not found.");
        if (!string.Equals(room.TemplateKind, "debate", StringComparison.OrdinalIgnoreCase))
            throw new HubException("Not a debate room.");

        var banned = await _mongo.IsBannedFromDebateAsync(roomId, meId, ct);
        if (banned) throw new HubException("You are banned from this room.");

        var isMonitor = string.Equals(room.HostUserId, meId, StringComparison.OrdinalIgnoreCase);
        return (room, isMonitor);
    }

    private void RequireMonitor(bool isMonitor)
    {
        if (!isMonitor) throw new HubException("Monitor only.");
    }

    // ─── Connection / group lifecycle ──────────────────────────────

    /// <summary>Audience or monitor joining the room. Adds to the
    /// public group + monitor sub-group (if monitor) and returns the
    /// initial state snapshot.</summary>
    public async Task<object> JoinDebateRoom(string roomId)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMonitor) = await AuthorizeAsync(roomId, ct);

        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(roomId), ct);
        if (isMonitor)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, MonitorGroup(roomId), ct);
        }

        return await BuildRoomStateAsync(room, isMonitor, ct);
    }

    public async Task LeaveDebateRoom(string roomId)
    {
        var ct = Context.ConnectionAborted;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(roomId), ct);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, MonitorGroup(roomId), ct);
    }

    public Task<object> GetRoomState(string roomId) => JoinDebateRoom(roomId);

    // ─── Round lifecycle ──────────────────────────────────────────

    public async Task<object> OpenRound(string roomId, string format, int? durationMinutes)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMonitor) = await AuthorizeAsync(roomId, ct);
        RequireMonitor(isMonitor);

        if (!FormatSeatsPerSide.TryGetValue(format, out var perSide))
            throw new HubException("Format must be 1v1, 2v2, 3v3, 4v4 or 5v5.");

        // If a round is already open / live, refuse — monitor must end
        // it explicitly to avoid stranded state.
        var existing = await _mongo.GetActiveDebateRoundAsync(roomId, ct);
        if (existing is not null) throw new HubException("A round is already in progress. End it first.");

        var meId = JwtService.GetUserId(Context.User!).ToString();
        var now = DateTime.UtcNow;

        var round = new DebateRound
        {
            RoomId        = roomId,
            MonitorUserId = meId,
            Format        = format.ToLowerInvariant(),
            Status        = "open_nominations",
            CreatedAt     = now,
        };
        await _mongo.InsertDebateRoundAsync(round, ct);
        await _mongo.SeedDebateSeatsAsync(roomId, round.Id!, perSide, ct);

        // If a duration was supplied, treat as "go live immediately +
        // round timer". Otherwise stays in open_nominations until the
        // monitor manually flips status when seats are filled.
        DateTime? endsAt = null;
        if (durationMinutes is int mins and > 0)
        {
            endsAt = now.AddMinutes(Math.Clamp(mins, 1, 180));
            round = (await _mongo.UpdateDebateRoundStatusAsync(round.Id!, "live", endsAt, ct))!;
        }

        var state = await BuildRoomStateAsync(room, isMonitor: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoundOpened", state, ct);
        return state;
    }

    public async Task EndRound(string roomId)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMonitor) = await AuthorizeAsync(roomId, ct);
        RequireMonitor(isMonitor);

        var active = await _mongo.GetActiveDebateRoundAsync(roomId, ct);
        if (active is null) return;

        await _mongo.UpdateDebateRoundStatusAsync(active.Id!, "ended", endsAt: null, ct);
        var state = await BuildRoomStateAsync(room, isMonitor: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoundEnded", state, ct);
    }

    public async Task StartLive(string roomId, int durationMinutes)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMonitor) = await AuthorizeAsync(roomId, ct);
        RequireMonitor(isMonitor);

        var active = await _mongo.GetActiveDebateRoundAsync(roomId, ct);
        if (active is null) throw new HubException("No open round.");
        if (active.Status == "live") return;

        var endsAt = DateTime.UtcNow.AddMinutes(Math.Clamp(durationMinutes, 1, 180));
        await _mongo.UpdateDebateRoundStatusAsync(active.Id!, "live", endsAt, ct);
        var state = await BuildRoomStateAsync(room, isMonitor: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoundUpdated", state, ct);
    }

    // ─── Nominations ──────────────────────────────────────────────

    public async Task<string?> NominateForSeat(
        string roomId, string realName, int age, string gender, string preferredSide)
    {
        var ct = Context.ConnectionAborted;
        var (_, _) = await AuthorizeAsync(roomId, ct);

        var meId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);

        var active = await _mongo.GetActiveDebateRoundAsync(roomId, ct);
        if (active is null) throw new HubException("No round to join.");
        if (active.Status == "ended") throw new HubException("Round has ended.");

        if (await _mongo.IsUserSeatedAsync(active.Id!, meId, ct))
            throw new HubException("You're already on a seat.");

        var trimmedName = (realName ?? "").Trim();
        if (trimmedName.Length == 0 || trimmedName.Length > MaxNameChars)
            throw new HubException("Real name 1-80 chars required.");
        if (age < 13 || age > 120) throw new HubException("Age 13-120 required.");
        var g = (gender ?? "").Trim().ToLowerInvariant();
        if (!AllowedGenders.Contains(g)) throw new HubException("Gender male/female/other.");
        var pref = (preferredSide ?? "either").Trim().ToLowerInvariant();
        if (!AllowedPreferred.Contains(pref)) throw new HubException("Preferred side pro/con/either.");

        var nomination = new DebateNomination
        {
            RoomId        = roomId,
            RoundId       = active.Id!,
            UserId        = meId,
            Username      = username,
            RealName      = trimmedName,
            Age           = age,
            Gender        = g,
            PreferredSide = pref,
            Status        = "pending",
            RaisedAt      = DateTime.UtcNow,
        };
        var inserted = await _mongo.InsertDebateNominationAsync(nomination, ct);
        if (inserted is null) return null;

        // Public push: count only (no bios).
        var count = await _mongo.CountPendingNominationsAsync(active.Id!, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId))
            .SendAsync("NominationCountChanged", new { count }, ct);

        // Monitor private push: full bio.
        await _hubCtx.Clients.Group(MonitorGroup(roomId))
            .SendAsync("NominationRaisedPrivate", ShapePrivilegedNomination(inserted), ct);

        return inserted.Id;
    }

    public async Task WithdrawNomination(string roomId)
    {
        var ct = Context.ConnectionAborted;
        var (_, _) = await AuthorizeAsync(roomId, ct);

        var meId = JwtService.GetUserId(Context.User!).ToString();
        var active = await _mongo.GetActiveDebateRoundAsync(roomId, ct);
        if (active is null) return;

        await _mongo.WithdrawDebateNominationAsync(active.Id!, meId, ct);

        var count = await _mongo.CountPendingNominationsAsync(active.Id!, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId))
            .SendAsync("NominationCountChanged", new { count }, ct);
        await _hubCtx.Clients.Group(MonitorGroup(roomId))
            .SendAsync("NominationWithdrawnPrivate", new { userId = meId }, ct);
    }

    public async Task<List<object>> GetNominationsForMonitor(string roomId)
    {
        var ct = Context.ConnectionAborted;
        var (_, isMonitor) = await AuthorizeAsync(roomId, ct);
        RequireMonitor(isMonitor);

        var active = await _mongo.GetActiveDebateRoundAsync(roomId, ct);
        if (active is null) return new List<object>();

        var rows = await _mongo.GetPendingNominationsForMonitorAsync(active.Id!, ct);
        return rows.Select(ShapePrivilegedNomination).ToList();
    }

    // ─── Seat assignment ─────────────────────────────────────────

    public async Task AssignSeat(string roomId, string nominationId, string side, int position)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMonitor) = await AuthorizeAsync(roomId, ct);
        RequireMonitor(isMonitor);

        var meId = room.HostUserId;
        var active = await _mongo.GetActiveDebateRoundAsync(roomId, ct);
        if (active is null) throw new HubException("No round to seat into.");

        if (!AllowedSides.Contains(side)) throw new HubException("Side must be pro/con.");

        var perSide = FormatSeatsPerSide[active.Format];
        if (position < 0 || position >= perSide)
            throw new HubException($"Position 0..{perSide - 1}.");

        var nom = await _mongo.GetDebateNominationAsync(nominationId, ct);
        if (nom is null || nom.RoundId != active.Id || nom.Status != "pending")
            throw new HubException("Nomination not available.");

        var ok = await _mongo.TryAssignDebateSeatAsync(
            active.Id!, side.ToLowerInvariant(), position,
            nom.UserId, nom.Username, ct);
        if (!ok) throw new HubException("Seat already taken.");

        await _mongo.SetDebateNominationStatusAsync(nominationId, "accepted", ct);
        await _mongo.LogModeratorActionAsync(new DebateModeratorAction
        {
            RoomId        = roomId,
            MonitorUserId = meId,
            TargetUserId  = nom.UserId,
            ActionType    = "assign_seat",
            Reason        = $"side={side},pos={position}",
            At            = DateTime.UtcNow,
        }, ct);

        var state = await BuildRoomStateAsync(room, isMonitor: false, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("SeatChanged", state, ct);

        var count = await _mongo.CountPendingNominationsAsync(active.Id!, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId))
            .SendAsync("NominationCountChanged", new { count }, ct);
    }

    public async Task UnseatUser(string roomId, string targetUserId, string? reason)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMonitor) = await AuthorizeAsync(roomId, ct);
        RequireMonitor(isMonitor);

        var active = await _mongo.GetActiveDebateRoundAsync(roomId, ct);
        if (active is null) return;

        await _mongo.UnassignDebateSeatAsync(active.Id!, targetUserId, ct);
        await _mongo.LogModeratorActionAsync(new DebateModeratorAction
        {
            RoomId        = roomId,
            MonitorUserId = room.HostUserId,
            TargetUserId  = targetUserId,
            ActionType    = "unseat",
            Reason        = ClampReason(reason),
            At            = DateTime.UtcNow,
        }, ct);

        var state = await BuildRoomStateAsync(room, isMonitor: false, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("SeatChanged", state, ct);
    }

    // ─── Moderation ───────────────────────────────────────────────

    public async Task KickFromDebate(string roomId, string targetUserId, string? reason)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMonitor) = await AuthorizeAsync(roomId, ct);
        RequireMonitor(isMonitor);

        // If kicked user is seated, free the seat too.
        var active = await _mongo.GetActiveDebateRoundAsync(roomId, ct);
        if (active is not null)
            await _mongo.UnassignDebateSeatAsync(active.Id!, targetUserId, ct);

        await _mongo.LogModeratorActionAsync(new DebateModeratorAction
        {
            RoomId        = roomId,
            MonitorUserId = room.HostUserId,
            TargetUserId  = targetUserId,
            ActionType    = "kick",
            Reason        = ClampReason(reason),
            At            = DateTime.UtcNow,
        }, ct);

        // Push to the kicked user (and only them) — their client routes
        // them out. Also fan a public notice so seat/UI updates.
        await _hubCtx.Clients.User(targetUserId).SendAsync(
            "UserKicked",
            new { roomId, reason = ClampReason(reason) },
            ct);
        var state = await BuildRoomStateAsync(room, isMonitor: false, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("SeatChanged", state, ct);
    }

    public async Task BanFromDebate(string roomId, string targetUserId, string? reason)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMonitor) = await AuthorizeAsync(roomId, ct);
        RequireMonitor(isMonitor);

        var active = await _mongo.GetActiveDebateRoundAsync(roomId, ct);
        if (active is not null)
            await _mongo.UnassignDebateSeatAsync(active.Id!, targetUserId, ct);

        await _mongo.BanFromDebateAsync(new DebateBan
        {
            RoomId        = roomId,
            UserId        = targetUserId,
            MonitorUserId = room.HostUserId,
            Reason        = ClampReason(reason),
            CreatedAt     = DateTime.UtcNow,
        }, ct);
        await _mongo.LogModeratorActionAsync(new DebateModeratorAction
        {
            RoomId        = roomId,
            MonitorUserId = room.HostUserId,
            TargetUserId  = targetUserId,
            ActionType    = "ban",
            Reason        = ClampReason(reason),
            At            = DateTime.UtcNow,
        }, ct);

        await _hubCtx.Clients.User(targetUserId).SendAsync(
            "UserBanned",
            new { roomId, reason = ClampReason(reason) },
            ct);
        var state = await BuildRoomStateAsync(room, isMonitor: false, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("SeatChanged", state, ct);
    }

    public async Task HighlightGoodQuestion(string roomId, string targetUserId, string? messageId)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMonitor) = await AuthorizeAsync(roomId, ct);
        RequireMonitor(isMonitor);

        var targetUsername = "";
        if (!string.IsNullOrEmpty(messageId))
        {
            var msg = await _mongo.GetDebateMessageAsync(messageId, ct);
            if (msg is not null && msg.RoomId == roomId)
            {
                await _mongo.HighlightDebateMessageAsync(messageId, ct);
                targetUsername = msg.SenderUsername;
            }
        }

        await _mongo.InsertDebateHighlightAsync(new DebateHighlight
        {
            RoomId          = roomId,
            TargetUserId    = targetUserId,
            TargetUsername  = targetUsername,
            MessageId       = messageId,
            MonitorUserId   = room.HostUserId,
            At              = DateTime.UtcNow,
        }, ct);
        await _mongo.LogModeratorActionAsync(new DebateModeratorAction
        {
            RoomId        = roomId,
            MonitorUserId = room.HostUserId,
            TargetUserId  = targetUserId,
            ActionType    = "highlight",
            Reason        = messageId,
            At            = DateTime.UtcNow,
        }, ct);

        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync(
            "UserHighlighted",
            new
            {
                targetUserId,
                targetUsername,
                messageId,
                at = DateTime.UtcNow,
            },
            ct);
    }

    // ─── Chat ─────────────────────────────────────────────────────

    public async Task<string?> SendChatMessage(string roomId, string content)
        => await SendChatInternal(roomId, content, isQuestion: false);

    public async Task<string?> SendQuestion(string roomId, string content)
        => await SendChatInternal(roomId, content, isQuestion: true);

    private async Task<string?> SendChatInternal(string roomId, string content, bool isQuestion)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMonitor) = await AuthorizeAsync(roomId, ct);

        var trimmed = (content ?? "").Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Length > MaxChatChars) trimmed = trimmed.Substring(0, MaxChatChars);

        var meId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);

        var active = await _mongo.GetActiveDebateRoundAsync(roomId, ct);
        var seated = active is not null && await _mongo.IsUserSeatedAsync(active.Id!, meId, ct);

        var msg = new DebateMessage
        {
            RoomId          = roomId,
            SenderUserId    = meId,
            SenderUsername  = username,
            SenderIsMonitor = isMonitor,
            SenderIsSeated  = seated,
            Content         = trimmed,
            IsQuestion      = isQuestion,
            IsHighlighted   = false,
            CreatedAt       = DateTime.UtcNow,
        };
        var saved = await _mongo.InsertDebateMessageAsync(msg, ct);

        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync(
            "ChatMessage",
            ShapeChat(saved),
            ct);
        return saved.Id;
    }

    // ─── State builder ────────────────────────────────────────────

    private async Task<object> BuildRoomStateAsync(MehfilRoom room, bool isMonitor, CancellationToken ct)
    {
        var round = await _mongo.GetActiveDebateRoundAsync(room.Id!, ct);
        List<DebateSeat> seats = new();
        int pendingCount = 0;
        if (round is not null)
        {
            seats = await _mongo.GetDebateSeatsAsync(round.Id!, ct);
            pendingCount = await _mongo.CountPendingNominationsAsync(round.Id!, ct);
        }
        var msgs = await _mongo.GetDebateMessagesAsync(room.Id!, limit: 80, ct);

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
            isMonitor,
            round = round is null ? null : new
            {
                id            = round.Id,
                format        = round.Format,
                status        = round.Status,
                startedAt     = round.StartedAt,
                endsAt        = round.EndsAt,
                createdAt     = round.CreatedAt,
                endedAt       = round.EndedAt,
                seatsPerSide  = FormatSeatsPerSide[round.Format],
            },
            seats = seats.Select(s => new
            {
                id               = s.Id,
                side             = s.Side,
                position         = s.Position,
                occupantUserId   = s.OccupantUserId,
                occupantUsername = s.OccupantUsername,
            }).ToList(),
            pendingNominationCount = pendingCount,
            messages = msgs.Select(ShapeChat).ToList(),
        };
    }

    private static object ShapeChat(DebateMessage m) => new
    {
        id              = m.Id,
        senderUserId    = m.SenderUserId,
        senderUsername  = m.SenderUsername,
        senderIsMonitor = m.SenderIsMonitor,
        senderIsSeated  = m.SenderIsSeated,
        content         = m.Content,
        isQuestion      = m.IsQuestion,
        isHighlighted   = m.IsHighlighted,
        createdAt       = m.CreatedAt,
    };

    /// <summary>Privileged nomination shape — includes bios. Only the
    /// monitor ever receives this. Anyone reusing the projection must
    /// gate on monitor role at the caller site.</summary>
    private static object ShapePrivilegedNomination(DebateNomination n) => new
    {
        id            = n.Id,
        userId        = n.UserId,
        username      = n.Username,
        realName      = n.RealName,
        age           = n.Age,
        gender        = n.Gender,
        preferredSide = n.PreferredSide,
        raisedAt      = n.RaisedAt,
    };

    private static string? ClampReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return null;
        return reason.Length > MaxReasonChars ? reason.Substring(0, MaxReasonChars) : reason;
    }
}
