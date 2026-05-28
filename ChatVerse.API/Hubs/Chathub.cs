using ChatVerse.API.Extensions;
using ChatVerse.Domain.Constants;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.ExternalServices.OpenAI;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using ChatVerse.Infrastructure.Persistence.Redis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private readonly MongoService _mongo;
    private readonly RedisService _redis;
    private readonly PostgresProcService _postgres;
    private readonly ModerationOrchestrator _moderation;
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(
        MongoService mongo,
        RedisService redis,
        PostgresProcService postgres,
        ModerationOrchestrator moderation,
        ILogger<ChatHub> logger)
    {
        _mongo = mongo;
        _redis = redis;
        _postgres = postgres;
        _moderation = moderation;
        _logger = logger;
    }

    // ============================================================
    //  OnConnectedAsync
    // ============================================================
    public override async Task OnConnectedAsync()
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);

        await _redis.SetUserOnlineAsync(userId);

        _logger.LogInformation("User {Username} connected [{ConnectionId}]",
            username, Context.ConnectionId);

        await base.OnConnectedAsync();
    }

    // ============================================================
    //  OnDisconnectedAsync
    // ============================================================
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);

        await _redis.SetUserOfflineAsync(userId);

        var roomSlug = await _redis.GetStringAsync($"conn:room:{Context.ConnectionId}");
        if (roomSlug != null)
        {
            await _redis.DecrementRoomCountAsync(roomSlug);
            await _redis.DeleteKeyAsync($"conn:room:{Context.ConnectionId}");

            await Clients.Group(roomSlug).SendAsync("UserLeft", new
            {
                userId,
                username,
                activeCount = await _redis.GetRoomActiveCountAsync(roomSlug)
            });
        }

        _logger.LogInformation("User {Username} disconnected", username);
        await base.OnDisconnectedAsync(exception);
    }

    // ============================================================
    //  JoinRoom
    // ============================================================
    public async Task JoinRoom(string roomSlug)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);
        var trustScore = JwtService.GetTrustScore(Context.User!);
        var ageVerified = JwtService.GetAgeVerified(Context.User!);

        // Room exists?
        var room = await _mongo.GetRoomBySlugAsync(roomSlug);
        if (room == null)
        {
            await Clients.Caller.SendAsync("Error", "Room not found");
            return;
        }

        // Age gate for 18+ rooms
        if (room.Category == "18plus" && !ageVerified)
        {
            await Clients.Caller.SendAsync("Error", "Age verification required for this room");
            return;
        }

        // Trust gate — block very low trust users
        if (trustScore < TrustBands.NewMax)
        {
            await Clients.Caller.SendAsync("Error", "Your trust score is too low to join rooms");
            return;
        }

        // Room ban check
        var (isBanned, banReason, banExpiry) = await _postgres.CheckRoomBanAsync(
            Guid.Parse(userId), roomSlug);
        if (isBanned)
        {
            await Clients.Caller.SendAsync("Error",
                $"You are banned from this room. {(banExpiry.HasValue ? $"Expires: {banExpiry:f}" : "Permanent")}");
            return;
        }

        // Join SignalR group
        await Groups.AddToGroupAsync(Context.ConnectionId, roomSlug);
        await _redis.SetStringAsync($"conn:room:{Context.ConnectionId}", roomSlug, TimeSpan.FromHours(24));
        await _redis.IncrementRoomCountAsync(roomSlug);

        // Send room history to caller
        var messages = await _mongo.GetRoomMessagesAsync(roomSlug, 0, 50);
        await Clients.Caller.SendAsync("RoomHistory", new
        {
            roomSlug,
            messages = messages.Select(MapMessage)
        });

        // Notify others
        await Clients.OthersInGroup(roomSlug).SendAsync("UserJoined", new
        {
            userId,
            username,
            activeCount = await _redis.GetRoomActiveCountAsync(roomSlug)
        });

        _logger.LogInformation("User {Username} joined room {Room}", username, roomSlug);
    }

    // ============================================================
    //  LeaveRoom
    // ============================================================
    public async Task LeaveRoom(string roomSlug)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomSlug);
        await _redis.DecrementRoomCountAsync(roomSlug);
        await _redis.DeleteKeyAsync($"conn:room:{Context.ConnectionId}");

        await Clients.OthersInGroup(roomSlug).SendAsync("UserLeft", new
        {
            userId,
            username,
            activeCount = await _redis.GetRoomActiveCountAsync(roomSlug)
        });
    }

    // ============================================================
    //  SendMessage
    //  1. Save to MongoDB
    //  2. Broadcast immediately
    //  3. Moderate async (never blocks)
    // ============================================================
    public async Task SendMessage(string roomSlug, string content, string? replyToId = null)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);
        var trustScore = JwtService.GetTrustScore(Context.User!);

        if (string.IsNullOrWhiteSpace(content) || content.Length > 2000)
        {
            await Clients.Caller.SendAsync("Error", "Invalid message content");
            return;
        }

        // Save to MongoDB
        var message = new Message
        {
            RoomId = roomSlug,
            SenderId = userId,
            SenderName = username,
            SenderTrustScore = trustScore,
            Content = content.Trim(),
            Type = "text",
            ReplyTo = replyToId,
            Moderation = new MessageModeration { Status = "pending" },
            CreatedAt = DateTime.UtcNow
        };

        var saved = await _mongo.InsertMessageAsync(message);
        await _mongo.IncrementRoomMessageCountAsync(roomSlug);

        // Broadcast immediately
        await Clients.Group(roomSlug).SendAsync("ReceiveMessage", MapMessage(saved));

        // Mark active day — direct call (no background task to avoid scope disposal)
        try { await _postgres.MarkUserActiveDayAsync(Guid.Parse(userId)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to mark active day"); }

        // Moderation (fire and forget — never blocks delivery)
        _ = Task.Run(async () =>
        {
            try
            {
                await _moderation.ModerateMessageAsync(
                    messageId: saved.Id!,
                    roomId: roomSlug,
                    senderId: userId,
                    content: content,
                    roomClients: Clients.Group(roomSlug)
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Moderation task failed for message {Id}", saved.Id);
            }
        });

        _logger.LogInformation("Message sent by {Username} in {Room}", username, roomSlug);
    }

    // ============================================================
    //  SendTyping
    // ============================================================
    public async Task SendTyping(string roomSlug)
    {
        var username = JwtService.GetUsername(Context.User!);
        await Clients.OthersInGroup(roomSlug).SendAsync("UserTyping", new { username });
    }

    // ============================================================
    //  ReactToMessage
    // ============================================================
    public async Task ReactToMessage(string roomSlug, string messageId, string emoji)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();
        await Clients.Group(roomSlug).SendAsync("MessageReaction", new
        {
            messageId,
            emoji,
            userId
        });
    }

    // ── Map message to client DTO ─────────────────────────────
    private static object MapMessage(Message m) => new
    {
        id = m.Id,
        roomId = m.RoomId,
        senderId = m.SenderId,
        senderName = m.SenderName,
        senderAvatar = m.SenderAvatarUrl,
        content = m.Content,
        type = m.Type,
        mediaUrl = m.MediaUrl,
        replyTo = m.ReplyTo,
        reactions = m.Reactions,
        modStatus = m.Moderation.Status,
        editedAt = m.EditedAt,
        createdAt = m.CreatedAt
    };
}