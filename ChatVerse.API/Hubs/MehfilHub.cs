using ChatVerse.API.Extensions;
using ChatVerse.API.Services;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Hubs;

// ============================================================
//  MehfilHub — creator-room platform.
//
//  Client methods:
//    • Discover(filter, template?)  → live + upcoming room list
//    • GetRoom(roomId)               → snapshot + attendee count + tips
//    • CreateRoom(...)               → host action; locks template
//    • CancelRoom(roomId)            → host-only, scheduled rooms only
//    • StartRoom(roomId)             → host triggers transition
//    • EndRoom(roomId)               → host triggers close
//    • JoinRoom / LeaveRoom          → audience attendance
//    • SendMessage(roomId, content)  → in-room chat
//    • Tip(roomId, giftType)         → audience tip (MVP intent-only)
//    • MyRooms()                     → my hosted rooms
//
//  Server-push events (per-room SignalR group):
//    • RoomStarted     — host just transitioned to live
//    • RoomEnded       — host closed the room
//    • RoomAudience    — currentAudienceCount changed
//    • RoomMessage     — new in-room chat line
//    • RoomTip         — new tip (so audience sees a confetti banner)
//
//  Templates: 11 locked keys (see MongoService.MehfilTemplates).
//  Host verification + payment settlement deferred to Phase 5.
// ============================================================

[Authorize]
public class MehfilHub : Hub
{
    private readonly MongoService _mongo;
    private readonly IHubContext<MehfilHub> _hubCtx;
    private readonly ILogger<MehfilHub> _logger;

    public MehfilHub(MongoService mongo, IHubContext<MehfilHub> hubCtx, ILogger<MehfilHub> logger)
    {
        _mongo = mongo;
        _hubCtx = hubCtx;
        _logger = logger;
    }

    private static string RoomGroup(string roomId) => $"mehfil:{roomId}";

    // ─── Discovery + reads ─────────────────────────────────────

    public async Task<object> Discover(string filter, string? template = null)
    {
        // filter: "live" | "upcoming" | "template" | "all"
        IReadOnlyList<MehfilRoom> rooms = filter switch
        {
            "live"     => await _mongo.GetLiveMehfilRoomsAsync(),
            "upcoming" => await _mongo.GetUpcomingMehfilRoomsAsync(),
            "template" => template is null
                ? Array.Empty<MehfilRoom>()
                : await _mongo.GetMehfilRoomsByTemplateAsync(template),
            _ => (await _mongo.GetLiveMehfilRoomsAsync())
                .Concat(await _mongo.GetUpcomingMehfilRoomsAsync())
                .ToList(),
        };
        return new
        {
            count = rooms.Count,
            templates = MongoService.MehfilTemplates,
            gifts = MongoService.MehfilGifts,
            rooms = rooms.Select(ToCardDto).ToList(),
        };
    }

    public async Task<object> GetRoom(string roomId)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var room = await _mongo.GetMehfilRoomByIdAsync(roomId)
            ?? throw new HubException("Mehfil not found.");
        var messages = room.Status != "scheduled"
            ? await _mongo.GetMehfilRoomMessagesAsync(room.Id!)
            : new List<MehfilMessage>();
        var attendees = room.Status == "live"
            ? await _mongo.GetMehfilLiveAttendeesAsync(room.Id!)
            : new List<MehfilAttendance>();
        var tips = await _mongo.GetMehfilRoomTipsAsync(room.Id!);
        return new
        {
            room      = ToCardDto(room),
            iAmHost   = room.HostUserId == meId,
            iAmInside = attendees.Any(a => a.UserId == meId),
            attendees = attendees.Select(a => new { username = a.Username }).ToList(),
            messages  = messages.Select(m => ToMessageDto(m, meId)).ToList(),
            tips      = tips.Select(t => new
            {
                senderUsername = t.SenderUsername,
                giftType       = t.GiftType,
                tokenAmount    = t.TokenAmount,
                createdAt      = t.CreatedAt,
            }).ToList(),
        };
    }

    public async Task<object> MyRooms()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var rooms = await _mongo.GetMyHostedMehfilRoomsAsync(meId);
        return new { count = rooms.Count, rooms = rooms.Select(ToCardDto).ToList() };
    }

    // ─── Host actions ──────────────────────────────────────────

    public async Task<object> CreateRoom(
        string templateKind, string title, string description,
        DateTime scheduledFor, int maxAudience)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var meName = JwtService.GetUsername(Context.User!);

        if (!MongoService.MehfilTemplates.Contains(templateKind))
            throw new HubException("Unknown template.");
        if (string.IsNullOrWhiteSpace(title))
            throw new HubException("Title is required.");
        if (title.Length > MongoService.MehfilTitleMaxChars)
            throw new HubException($"Title too long (max {MongoService.MehfilTitleMaxChars}).");
        if (maxAudience is < 2 or > 5000)
            throw new HubException("Audience cap must be between 2 and 5000.");
        if (scheduledFor < DateTime.UtcNow.AddMinutes(-10))
            throw new HubException("Scheduled time can't be in the past.");

        var room = new MehfilRoom
        {
            HostUserId    = meId,
            HostUsername  = meName,
            TemplateKind  = templateKind,
            Title         = title.Trim(),
            Description   = (description ?? "").Trim(),
            ScheduledFor  = scheduledFor,
            MaxAudience   = maxAudience,
            Status        = "scheduled",
        };
        var saved = await _mongo.InsertMehfilRoomAsync(room);
        return ToCardDto(saved);
    }

    public async Task<object> CancelRoom(string roomId)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var room = await _mongo.GetMehfilRoomByIdAsync(roomId)
            ?? throw new HubException("Mehfil not found.");
        if (room.HostUserId != meId) throw new HubException("Only the host can cancel.");
        if (room.Status != "scheduled") throw new HubException("Already started — use End instead.");
        await _mongo.UpdateMehfilRoomStatusAsync(roomId, "cancelled");
        return new { ok = true };
    }

    public async Task<object> StartRoom(string roomId)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var room = await _mongo.GetMehfilRoomByIdAsync(roomId)
            ?? throw new HubException("Mehfil not found.");
        if (room.HostUserId != meId) throw new HubException("Only the host can start.");
        if (room.Status != "scheduled") throw new HubException("Mehfil isn't in a startable state.");
        await _mongo.UpdateMehfilRoomStatusAsync(roomId, "live", setStartedAt: true);
        var fresh = await _mongo.GetMehfilRoomByIdAsync(roomId);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoomStarted", ToCardDto(fresh!));
        return ToCardDto(fresh!);
    }

    public async Task<object> EndRoom(string roomId)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var room = await _mongo.GetMehfilRoomByIdAsync(roomId)
            ?? throw new HubException("Mehfil not found.");
        if (room.HostUserId != meId) throw new HubException("Only the host can end.");
        if (room.Status != "live") throw new HubException("Mehfil isn't live.");
        await _mongo.UpdateMehfilRoomStatusAsync(roomId, "ended", setEndedAt: true);
        var fresh = await _mongo.GetMehfilRoomByIdAsync(roomId);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoomEnded", ToCardDto(fresh!));
        return ToCardDto(fresh!);
    }

    // ─── Audience actions ──────────────────────────────────────

    public async Task<object> JoinRoom(string roomId)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var meName = JwtService.GetUsername(Context.User!);
        var room = await _mongo.GetMehfilRoomByIdAsync(roomId)
            ?? throw new HubException("Mehfil not found.");
        if (room.Status != "live") throw new HubException("Mehfil isn't live.");
        if (room.CurrentAudienceCount >= room.MaxAudience)
            throw new HubException("Audience cap reached.");

        await _mongo.JoinMehfilRoomAsync(roomId, meId, meName);
        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(roomId));

        var fresh = await _mongo.GetMehfilRoomByIdAsync(roomId);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoomAudience", new
        {
            roomId,
            currentAudienceCount = fresh?.CurrentAudienceCount ?? 0,
        });
        return ToCardDto(fresh!);
    }

    public async Task<object> LeaveRoom(string roomId)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        await _mongo.LeaveMehfilRoomAsync(roomId, meId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(roomId));
        var fresh = await _mongo.GetMehfilRoomByIdAsync(roomId);
        if (fresh is not null)
        {
            await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoomAudience", new
            {
                roomId,
                currentAudienceCount = fresh.CurrentAudienceCount,
            });
        }
        return new { ok = true };
    }

    public async Task<object> SendMessage(string roomId, string content)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var meName = JwtService.GetUsername(Context.User!);
        var room = await _mongo.GetMehfilRoomByIdAsync(roomId)
            ?? throw new HubException("Mehfil not found.");
        if (room.Status != "live") throw new HubException("Mehfil isn't live.");

        if (string.IsNullOrWhiteSpace(content))
            throw new HubException("Message can't be empty.");
        if (content.Length > MongoService.MehfilMessageMaxChars)
            throw new HubException($"Keep it under {MongoService.MehfilMessageMaxChars} chars.");

        var msg = new MehfilMessage
        {
            RoomId         = roomId,
            SenderUserId   = meId,
            SenderUsername = meName,
            IsHost         = room.HostUserId == meId,
            Content        = content.Trim(),
        };
        var saved = await _mongo.InsertMehfilMessageAsync(msg);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoomMessage", ToMessageDto(saved, meId));
        return ToMessageDto(saved, meId);
    }

    public async Task<object> Tip(string roomId, string giftType)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var meName = JwtService.GetUsername(Context.User!);
        var room = await _mongo.GetMehfilRoomByIdAsync(roomId)
            ?? throw new HubException("Mehfil not found.");
        if (room.Status != "live") throw new HubException("Tips only during live shows.");
        if (room.HostUserId == meId) throw new HubException("Host can't tip themselves.");
        if (!MongoService.MehfilGifts.ContainsKey(giftType))
            throw new HubException("Unknown gift.");

        var tip = new MehfilTip
        {
            RoomId          = roomId,
            SenderUserId    = meId,
            SenderUsername  = meName,
            RecipientUserId = room.HostUserId,
            GiftType        = giftType,
        };
        var saved = await _mongo.InsertMehfilTipAsync(tip);
        var fresh = await _mongo.GetMehfilRoomByIdAsync(roomId);

        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoomTip", new
        {
            roomId,
            senderUsername = saved.SenderUsername,
            giftType       = saved.GiftType,
            tokenAmount    = saved.TokenAmount,
            totalTipsTokens = fresh?.TotalTipsTokens ?? 0,
        });
        return new
        {
            id           = saved.Id,
            giftType     = saved.GiftType,
            tokenAmount  = saved.TokenAmount,
        };
    }

    // ─── DTO shaping ───────────────────────────────────────────

    private static object ToCardDto(MehfilRoom r) => new
    {
        id            = r.Id,
        title         = r.Title,
        description   = r.Description,
        templateKind  = r.TemplateKind,
        hostUsername  = r.HostUsername,
        scheduledFor  = r.ScheduledFor,
        startedAt     = r.StartedAt,
        endedAt       = r.EndedAt,
        status        = r.Status,
        maxAudience   = r.MaxAudience,
        currentAudienceCount = r.CurrentAudienceCount,
        totalAttendeesCount  = r.TotalAttendeesCount,
        totalTipsTokens      = r.TotalTipsTokens,
    };

    private static object ToMessageDto(MehfilMessage m, string viewerId) => new
    {
        id             = m.Id,
        senderUsername = m.SenderUsername,
        isHost         = m.IsHost,
        mine           = m.SenderUserId == viewerId,
        content        = m.Content,
        createdAt      = m.CreatedAt,
    };
}
