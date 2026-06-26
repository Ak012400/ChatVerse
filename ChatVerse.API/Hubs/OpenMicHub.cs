using ChatVerse.API.Extensions;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Services.LiveKit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Hubs;

// ============================================================
//  OpenMicHub — third per-template Mehfil specialisation.
//
//  Standalone hub at /hubs/open-mic. References MehfilRoom only for
//  identity. All open-mic state in om_* collections.
//
//  Roles:
//   • MC (= MehfilRoom.HostUserId)
//   • Performers (anyone on the current slot)
//   • Audience (everyone else in the room)
//
//  Method roster:
//   MC:
//     • ConfigureRoom(privacy, slotDurationSeconds)
//     • StartSet()                            — opens nominations queue
//     • EndSet()                              — closes set + ends slot
//     • NextPerformer(queueEntryId)           — moves entry to stage,
//                                                ends previous slot,
//                                                provisions LiveKit room
//     • EndCurrentSlot()                      — end-without-advance
//     • HighlightPerformance(slotId)
//     • KickFromOpenMic(targetUserId, reason?)
//     • BanFromOpenMic(targetUserId, reason?)
//     • GetQueueForMc()                       — PRIVILEGED bios
//   Audience:
//     • JoinQueue(realName, age, gender, performanceTitle)
//     • LeaveQueue()
//     • SendReaction(emoji)                   — rate-limited (1/sec)
//   Performer:
//     • GetSlotLiveKitToken(slotId)           — only callable by
//                                                current slot occupant
//   View:
//     • JoinOpenMicRoom(inviteCode?)
//     • LeaveOpenMicRoom()
//     • GetRoomState()
//
//  Public push events (room group "om:{roomId}"):
//   • SetOpened / SetEnded / RoomStateChanged (full state)
//   • SlotChanged                             — new performer on stage
//   • QueueCountChanged                       — public count only
//   • ReactionFlashed                         — emoji + ts (no userId)
//   • PerformanceHighlighted                  — slotId + at
//
//  Private push (MC sub-group "om-mc:{roomId}"):
//   • QueueRaisedPrivate                      — full bio
//   • QueueWithdrawnPrivate                   — userId only
// ============================================================

[Authorize]
public class OpenMicHub : Hub
{
    private readonly MongoService _mongo;
    private readonly LiveKitService _liveKit;
    private readonly IHubContext<OpenMicHub> _hubCtx;
    private readonly ILogger<OpenMicHub> _logger;

    public OpenMicHub(
        MongoService mongo,
        LiveKitService liveKit,
        IHubContext<OpenMicHub> hubCtx,
        ILogger<OpenMicHub> logger)
    {
        _mongo = mongo;
        _liveKit = liveKit;
        _hubCtx = hubCtx;
        _logger = logger;
    }

    // Allowed emojis (allowlisted server-side so audience can't push
    // arbitrary strings down to all peers).
    private static readonly HashSet<string> AllowedReactionEmojis = new()
    {
        "👏", "🔥", "😭", "😂", "🎤", "💯", "🫡", "💀",
    };

    private const int MaxNameChars = 80;
    private const int MaxTitleChars = 80;

    private static readonly int[] AllowedSlotDurations = { 60, 180, 300 };
    private static readonly HashSet<string> AllowedPrivacy = new() { "public", "private" };

    private static string RoomGroup(string roomId) => $"om:{roomId}";
    private static string McGroup(string roomId) => $"om-mc:{roomId}";

    private async Task<(MehfilRoom room, bool isMc)> AuthorizeAsync(string roomId, CancellationToken ct, string? inviteCode = null)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var room = await _mongo.GetMehfilRoomByIdAsync(roomId, ct);
        if (room is null) throw new HubException("Room not found.");
        if (!string.Equals(room.TemplateKind, "open_mic", StringComparison.OrdinalIgnoreCase))
            throw new HubException("Not an open-mic room.");

        if (await _mongo.IsBannedFromOpenMicAsync(roomId, meId, ct))
            throw new HubException("You are banned from this room.");

        var cfg = await _mongo.GetOpenMicConfigAsync(roomId, ct);
        if (cfg is not null && string.Equals(cfg.Privacy, "private", StringComparison.OrdinalIgnoreCase))
        {
            var isMc = string.Equals(room.HostUserId, meId, StringComparison.OrdinalIgnoreCase);
            if (!isMc && (string.IsNullOrWhiteSpace(inviteCode) ||
                          !string.Equals(inviteCode.Trim(), cfg.InviteCode, StringComparison.OrdinalIgnoreCase)))
                throw new HubException("Invite code required.");
        }

        var isMcRole = string.Equals(room.HostUserId, meId, StringComparison.OrdinalIgnoreCase);
        return (room, isMcRole);
    }

    private void RequireMc(bool isMc)
    {
        if (!isMc) throw new HubException("MC only.");
    }

    // ─── Connection / state ─────────────────────────────────────

    public async Task<object> JoinOpenMicRoom(string roomId, string? inviteCode)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMc) = await AuthorizeAsync(roomId, ct, inviteCode);

        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(roomId), ct);
        if (isMc)
            await Groups.AddToGroupAsync(Context.ConnectionId, McGroup(roomId), ct);

        return await BuildRoomStateAsync(room, isMc, ct);
    }

    public async Task LeaveOpenMicRoom(string roomId)
    {
        var ct = Context.ConnectionAborted;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(roomId), ct);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, McGroup(roomId), ct);
    }

    public Task<object> GetRoomState(string roomId) => JoinOpenMicRoom(roomId, null);

    // ─── Config ──────────────────────────────────────────────────

    public async Task ConfigureRoom(string roomId, string privacy, int slotDurationSeconds)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMc) = await AuthorizeAsync(roomId, ct);
        RequireMc(isMc);

        if (!AllowedPrivacy.Contains(privacy ?? ""))
            throw new HubException("Privacy must be public or private.");
        if (!AllowedSlotDurations.Contains(slotDurationSeconds))
            throw new HubException("Slot duration must be 60, 180 or 300 seconds.");

        var existing = await _mongo.GetOpenMicConfigAsync(roomId, ct);
        var cfg = existing ?? new OpenMicConfig
        {
            RoomId    = roomId,
            McUserId  = room.HostUserId,
            CreatedAt = DateTime.UtcNow,
        };
        cfg.Privacy = privacy.ToLowerInvariant();
        cfg.SlotDurationSeconds = slotDurationSeconds;
        if (cfg.Privacy == "private" && string.IsNullOrEmpty(cfg.InviteCode))
        {
            cfg.InviteCode = GenerateInviteCode();
        }
        if (cfg.Privacy == "public")
        {
            cfg.InviteCode = null;
        }
        await _mongo.UpsertOpenMicConfigAsync(cfg, ct);

        var state = await BuildRoomStateAsync(room, isMc: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoomStateChanged", state, ct);
    }

    // ─── Set lifecycle ──────────────────────────────────────────

    public async Task StartSet(string roomId)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMc) = await AuthorizeAsync(roomId, ct);
        RequireMc(isMc);

        var existing = await _mongo.GetActiveOpenMicSetAsync(roomId, ct);
        if (existing is not null) throw new HubException("A set is already active.");

        var set = new OpenMicSet
        {
            RoomId    = roomId,
            McUserId  = room.HostUserId,
            Status    = "waiting",
            CreatedAt = DateTime.UtcNow,
        };
        await _mongo.InsertOpenMicSetAsync(set, ct);

        await _mongo.LogOpenMicMcActionAsync(new OpenMicMcAction
        {
            RoomId       = roomId,
            McUserId     = room.HostUserId,
            TargetUserId = room.HostUserId,
            ActionType   = "start_set",
            At           = DateTime.UtcNow,
        }, ct);

        var state = await BuildRoomStateAsync(room, isMc: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("SetOpened", state, ct);
    }

    public async Task EndSet(string roomId)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMc) = await AuthorizeAsync(roomId, ct);
        RequireMc(isMc);

        var active = await _mongo.GetActiveOpenMicSetAsync(roomId, ct);
        if (active is null) return;

        // End current slot if live.
        var slot = await _mongo.GetActiveOpenMicSlotAsync(roomId, ct);
        if (slot is not null) await _mongo.EndOpenMicSlotAsync(slot.Id!, ct);

        await _mongo.SetOpenMicSetStatusAsync(active.Id!, "ended", ct);

        await _mongo.LogOpenMicMcActionAsync(new OpenMicMcAction
        {
            RoomId       = roomId,
            McUserId     = room.HostUserId,
            TargetUserId = room.HostUserId,
            ActionType   = "end_set",
            At           = DateTime.UtcNow,
        }, ct);

        var state = await BuildRoomStateAsync(room, isMc: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("SetEnded", state, ct);
    }

    // ─── Queue ─────────────────────────────────────────────────

    public async Task<string?> JoinQueue(string roomId, string realName, int age, string gender, string performanceTitle)
    {
        var ct = Context.ConnectionAborted;
        await AuthorizeAsync(roomId, ct);

        var meId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);

        var active = await _mongo.GetActiveOpenMicSetAsync(roomId, ct);
        if (active is null) throw new HubException("No active set — wait for the MC to start one.");
        if (active.Status == "ended") throw new HubException("Set has ended.");

        var name = (realName ?? "").Trim();
        if (name.Length == 0 || name.Length > MaxNameChars)
            throw new HubException("Real name 1-80 chars required.");
        if (age < 13 || age > 120) throw new HubException("Age 13-120.");
        var title = (performanceTitle ?? "").Trim();
        if (title.Length == 0 || title.Length > MaxTitleChars)
            throw new HubException("Performance title 1-80 chars.");

        var entry = new OpenMicQueueEntry
        {
            RoomId           = roomId,
            SetId            = active.Id!,
            UserId           = meId,
            Username         = username,
            RealName         = name,
            Age              = age,
            Gender           = (gender ?? "").Trim().ToLowerInvariant(),
            PerformanceTitle = title,
            Status           = "pending",
            RaisedAt         = DateTime.UtcNow,
        };
        var inserted = await _mongo.InsertOpenMicQueueEntryAsync(entry, ct);
        if (inserted is null) return null;

        var count = await _mongo.CountPendingOpenMicQueueAsync(active.Id!, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId))
            .SendAsync("QueueCountChanged", new { count }, ct);
        await _hubCtx.Clients.Group(McGroup(roomId))
            .SendAsync("QueueRaisedPrivate", ShapePrivilegedQueueEntry(inserted), ct);

        return inserted.Id;
    }

    public async Task LeaveQueue(string roomId)
    {
        var ct = Context.ConnectionAborted;
        await AuthorizeAsync(roomId, ct);

        var meId = JwtService.GetUserId(Context.User!).ToString();
        var active = await _mongo.GetActiveOpenMicSetAsync(roomId, ct);
        if (active is null) return;

        await _mongo.WithdrawOpenMicQueueEntryAsync(active.Id!, meId, ct);

        var count = await _mongo.CountPendingOpenMicQueueAsync(active.Id!, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId))
            .SendAsync("QueueCountChanged", new { count }, ct);
        await _hubCtx.Clients.Group(McGroup(roomId))
            .SendAsync("QueueWithdrawnPrivate", new { userId = meId }, ct);
    }

    public async Task<List<object>> GetQueueForMc(string roomId)
    {
        var ct = Context.ConnectionAborted;
        var (_, isMc) = await AuthorizeAsync(roomId, ct);
        RequireMc(isMc);

        var active = await _mongo.GetActiveOpenMicSetAsync(roomId, ct);
        if (active is null) return new List<object>();
        var rows = await _mongo.GetPendingOpenMicQueueForMcAsync(active.Id!, ct);
        return rows.Select(ShapePrivilegedQueueEntry).ToList();
    }

    // ─── Slots ─────────────────────────────────────────────────

    public async Task NextPerformer(string roomId, string queueEntryId)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMc) = await AuthorizeAsync(roomId, ct);
        RequireMc(isMc);

        var active = await _mongo.GetActiveOpenMicSetAsync(roomId, ct);
        if (active is null) throw new HubException("No active set.");

        var entry = await _mongo.GetOpenMicQueueEntryAsync(queueEntryId, ct);
        if (entry is null || entry.SetId != active.Id || entry.Status != "pending")
            throw new HubException("Queue entry not available.");

        // End any current slot.
        var currentSlot = await _mongo.GetActiveOpenMicSlotAsync(roomId, ct);
        if (currentSlot is not null)
        {
            await _mongo.EndOpenMicSlotAsync(currentSlot.Id!, ct);
            // Mark its queue entry as done if not already.
            await _mongo.SetOpenMicQueueEntryStatusAsync(currentSlot.QueueEntryId, "done", ct);
        }

        // Provision a LiveKit room for this performer.
        var slotId = ObjectIdGen();
        var livekitRoomName = $"om-slot-{slotId}";
        await _liveKit.CreateRoomAsync(livekitRoomName, emptyTimeoutSeconds: 60, maxParticipants: 200);

        var cfg = await _mongo.GetOpenMicConfigAsync(roomId, ct);
        var slotDuration = cfg?.SlotDurationSeconds ?? 180;

        var slot = new OpenMicSlot
        {
            Id                = slotId,
            RoomId            = roomId,
            SetId             = active.Id!,
            QueueEntryId      = entry.Id!,
            PerformerUserId   = entry.UserId,
            PerformerUsername = entry.Username,
            PerformanceTitle  = entry.PerformanceTitle,
            LivekitRoomName   = livekitRoomName,
            StartedAt         = DateTime.UtcNow,
            EndsAt            = DateTime.UtcNow.AddSeconds(slotDuration),
            ApplauseCount     = 0,
            IsHighlighted     = false,
        };
        await _mongo.InsertOpenMicSlotAsync(slot, ct);

        await _mongo.SetOpenMicQueueEntryStatusAsync(entry.Id!, "called", ct);
        await _mongo.SetOpenMicSetStatusAsync(active.Id!, "live", ct);

        await _mongo.LogOpenMicMcActionAsync(new OpenMicMcAction
        {
            RoomId       = roomId,
            McUserId     = room.HostUserId,
            TargetUserId = entry.UserId,
            ActionType   = "next_performer",
            At           = DateTime.UtcNow,
        }, ct);

        var state = await BuildRoomStateAsync(room, isMc: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("SlotChanged", state, ct);

        var count = await _mongo.CountPendingOpenMicQueueAsync(active.Id!, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId))
            .SendAsync("QueueCountChanged", new { count }, ct);
    }

    public async Task EndCurrentSlot(string roomId)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMc) = await AuthorizeAsync(roomId, ct);
        RequireMc(isMc);

        var slot = await _mongo.GetActiveOpenMicSlotAsync(roomId, ct);
        if (slot is null) return;

        await _mongo.EndOpenMicSlotAsync(slot.Id!, ct);
        await _mongo.SetOpenMicQueueEntryStatusAsync(slot.QueueEntryId, "done", ct);

        var active = await _mongo.GetActiveOpenMicSetAsync(roomId, ct);
        if (active is not null)
            await _mongo.SetOpenMicSetStatusAsync(active.Id!, "waiting", ct);

        await _mongo.LogOpenMicMcActionAsync(new OpenMicMcAction
        {
            RoomId       = roomId,
            McUserId     = room.HostUserId,
            TargetUserId = slot.PerformerUserId,
            ActionType   = "end_slot",
            At           = DateTime.UtcNow,
        }, ct);

        var state = await BuildRoomStateAsync(room, isMc: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("SlotChanged", state, ct);
    }

    public async Task<object?> GetSlotLiveKitToken(string roomId, string slotId)
    {
        var ct = Context.ConnectionAborted;
        var (_, _) = await AuthorizeAsync(roomId, ct);

        var slot = await _mongo.GetOpenMicSlotAsync(slotId, ct);
        if (slot is null || slot.RoomId != roomId) return null;
        if (slot.EndedAt.HasValue) return null;

        var meId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);

        // Performer publishes; audience subscribes only.
        var canPublish = string.Equals(slot.PerformerUserId, meId, StringComparison.OrdinalIgnoreCase);
        var token = _liveKit.GenerateToken(slot.LivekitRoomName, meId, username, canPublish, canSubscribe: true);

        return new
        {
            roomName  = slot.LivekitRoomName,
            serverUrl = _liveKit.GetServerUrl(),
            token,
            canPublish,
        };
    }

    // ─── Reactions ─────────────────────────────────────────────

    public async Task SendReaction(string roomId, string emoji)
    {
        var ct = Context.ConnectionAborted;
        await AuthorizeAsync(roomId, ct);

        if (!AllowedReactionEmojis.Contains(emoji)) throw new HubException("Emoji not allowed.");

        var slot = await _mongo.GetActiveOpenMicSlotAsync(roomId, ct);
        if (slot is null) return; // silent — no live slot to react to

        await _mongo.InsertOpenMicReactionAsync(new OpenMicReaction
        {
            RoomId = roomId,
            SlotId = slot.Id!,
            Emoji  = emoji,
            At     = DateTime.UtcNow,
        }, ct);
        await _mongo.IncrementOpenMicSlotApplauseAsync(slot.Id!, 1, ct);

        // Public fan-out — emoji + ts only. No userId so privacy intact.
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync(
            "ReactionFlashed",
            new { emoji, slotId = slot.Id, at = DateTime.UtcNow },
            ct);
    }

    // ─── Moderation ───────────────────────────────────────────

    public async Task HighlightPerformance(string roomId, string slotId)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMc) = await AuthorizeAsync(roomId, ct);
        RequireMc(isMc);

        var slot = await _mongo.GetOpenMicSlotAsync(slotId, ct);
        if (slot is null || slot.RoomId != roomId) return;

        await _mongo.HighlightOpenMicSlotAsync(slotId, ct);
        await _mongo.LogOpenMicMcActionAsync(new OpenMicMcAction
        {
            RoomId       = roomId,
            McUserId     = room.HostUserId,
            TargetUserId = slot.PerformerUserId,
            ActionType   = "highlight",
            At           = DateTime.UtcNow,
        }, ct);

        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync(
            "PerformanceHighlighted",
            new { slotId, performerUserId = slot.PerformerUserId, at = DateTime.UtcNow },
            ct);
    }

    public async Task KickFromOpenMic(string roomId, string targetUserId, string? reason)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMc) = await AuthorizeAsync(roomId, ct);
        RequireMc(isMc);

        // Withdraw any queue entry; end slot if on stage.
        var active = await _mongo.GetActiveOpenMicSetAsync(roomId, ct);
        if (active is not null) await _mongo.WithdrawOpenMicQueueEntryAsync(active.Id!, targetUserId, ct);
        var slot = await _mongo.GetActiveOpenMicSlotAsync(roomId, ct);
        if (slot is not null && string.Equals(slot.PerformerUserId, targetUserId, StringComparison.OrdinalIgnoreCase))
            await _mongo.EndOpenMicSlotAsync(slot.Id!, ct);

        await _mongo.LogOpenMicMcActionAsync(new OpenMicMcAction
        {
            RoomId       = roomId,
            McUserId     = room.HostUserId,
            TargetUserId = targetUserId,
            ActionType   = "kick",
            Reason       = reason,
            At           = DateTime.UtcNow,
        }, ct);

        await _hubCtx.Clients.User(targetUserId).SendAsync("UserKicked", new { roomId, reason }, ct);
        var state = await BuildRoomStateAsync(room, isMc: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoomStateChanged", state, ct);
    }

    public async Task BanFromOpenMic(string roomId, string targetUserId, string? reason)
    {
        var ct = Context.ConnectionAborted;
        var (room, isMc) = await AuthorizeAsync(roomId, ct);
        RequireMc(isMc);

        await _mongo.BanFromOpenMicAsync(new OpenMicBan
        {
            RoomId    = roomId,
            UserId    = targetUserId,
            McUserId  = room.HostUserId,
            Reason    = reason,
            CreatedAt = DateTime.UtcNow,
        }, ct);
        var active = await _mongo.GetActiveOpenMicSetAsync(roomId, ct);
        if (active is not null) await _mongo.WithdrawOpenMicQueueEntryAsync(active.Id!, targetUserId, ct);

        await _mongo.LogOpenMicMcActionAsync(new OpenMicMcAction
        {
            RoomId       = roomId,
            McUserId     = room.HostUserId,
            TargetUserId = targetUserId,
            ActionType   = "ban",
            Reason       = reason,
            At           = DateTime.UtcNow,
        }, ct);

        await _hubCtx.Clients.User(targetUserId).SendAsync("UserBanned", new { roomId, reason }, ct);
        var state = await BuildRoomStateAsync(room, isMc: true, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoomStateChanged", state, ct);
    }

    // ─── State builder ────────────────────────────────────────

    private async Task<object> BuildRoomStateAsync(MehfilRoom room, bool isMc, CancellationToken ct)
    {
        var cfg = await _mongo.GetOpenMicConfigAsync(room.Id!, ct);
        var activeSet = await _mongo.GetActiveOpenMicSetAsync(room.Id!, ct);
        OpenMicSlot? slot = null;
        int pendingCount = 0;
        if (activeSet is not null)
        {
            slot = await _mongo.GetActiveOpenMicSlotAsync(room.Id!, ct);
            pendingCount = await _mongo.CountPendingOpenMicQueueAsync(activeSet.Id!, ct);
        }
        var recentSlots = await _mongo.GetRecentOpenMicSlotsAsync(room.Id!, 12, ct);

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
            isMc,
            config = new
            {
                privacy             = cfg?.Privacy ?? "public",
                inviteCode          = isMc ? cfg?.InviteCode : null,
                slotDurationSeconds = cfg?.SlotDurationSeconds ?? 180,
            },
            set = activeSet is null ? null : new
            {
                id        = activeSet.Id,
                status    = activeSet.Status,
                createdAt = activeSet.CreatedAt,
                endedAt   = activeSet.EndedAt,
            },
            activeSlot = slot is null ? null : new
            {
                id                = slot.Id,
                performerUserId   = slot.PerformerUserId,
                performerUsername = slot.PerformerUsername,
                performanceTitle  = slot.PerformanceTitle,
                startedAt         = slot.StartedAt,
                endsAt            = slot.EndsAt,
                applauseCount     = slot.ApplauseCount,
                isHighlighted     = slot.IsHighlighted,
            },
            pendingQueueCount = pendingCount,
            recentSlots = recentSlots.Select(s => new
            {
                id                = s.Id,
                performerUsername = s.PerformerUsername,
                performanceTitle  = s.PerformanceTitle,
                startedAt         = s.StartedAt,
                endedAt           = s.EndedAt,
                applauseCount     = s.ApplauseCount,
                isHighlighted     = s.IsHighlighted,
            }).ToList(),
        };
    }

    private static object ShapePrivilegedQueueEntry(OpenMicQueueEntry e) => new
    {
        id               = e.Id,
        userId           = e.UserId,
        username         = e.Username,
        realName         = e.RealName,
        age              = e.Age,
        gender           = e.Gender,
        performanceTitle = e.PerformanceTitle,
        position         = e.Position,
        raisedAt         = e.RaisedAt,
    };

    // 8-char alphanumeric invite code (no ambiguous chars like 0/O/1/I).
    private static string GenerateInviteCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = new byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var sb = new System.Text.StringBuilder(8);
        foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
        return sb.ToString();
    }

    private static string ObjectIdGen() => MongoDB.Bson.ObjectId.GenerateNewId().ToString();
}
