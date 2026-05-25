using ChatVerse.API.Extensions;
using ChatVerse.Domain.Constants;
using ChatVerse.Domain.Entities;
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
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(
        MongoService mongo,
        RedisService redis,
        PostgresProcService postgres,
        ILogger<ChatHub> logger)
    {
        _mongo = mongo;
        _redis = redis;
        _postgres = postgres;
        _logger = logger;
    }

    // ============================================================
    //  OnConnectedAsync
    //  Called when user connects to /hubs/chat
    // ============================================================
    public override async Task OnConnectedAsync()
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);

        // Mark online in Redis
        await _redis.SetUserOnlineAsync(userId);

        _logger.LogInformation("User {Username} connected to ChatHub [{ConnectionId}]",
            username, Context.ConnectionId);

        await base.OnConnectedAsync();
    }

    // ============================================================
    //  OnDisconnectedAsync
    //  Called when user disconnects or loses connection
    // ============================================================
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);

        await _redis.SetUserOfflineAsync(userId);

        // Find which room this connection was in and decrement count
        var roomSlug = await _redis.GetStringAsync($"conn:room:{Context.ConnectionId}");
        if (roomSlug != null)
        {
            await _redis.DecrementRoomCountAsync(roomSlug);
            await _redis.DeleteKeyAsync($"conn:room:{Context.ConnectionId}");

            // Notify room that user left
            await Clients.Group(roomSlug).SendAsync("UserLeft", new
            {
                userId,
                username,
                activeCount = await _redis.GetRoomActiveCountAsync(roomSlug)
            });
        }

        _logger.LogInformation("User {Username} disconnected from ChatHub", username);
        await base.OnDisconnectedAsync(exception);
    }

    // ============================================================
    //  JoinRoom
    //  Client calls this to enter a chat room
    // ============================================================
    public async Task JoinRoom(string roomSlug)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);
        var trustScore = JwtService.GetTrustScore(Context.User!);
        var ageVerified = JwtService.GetAgeVerified(Context.User!);

        // Check if room exists
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

        // Trust gate — restricted users (score < 20) cannot join
        if (trustScore < TrustBands.NewMax)
        {
            await Clients.Caller.SendAsync("Error", "Your trust score is too low to join rooms");
            return;
        }

        // Check room ban in PostgreSQL
        // (ban check via proc will be added when trust procs are built)

        // Add to SignalR group
        await Groups.AddToGroupAsync(Context.ConnectionId, roomSlug);

        // Track which room this connection is in
        await _redis.SetStringAsync(
            $"conn:room:{Context.ConnectionId}",
            roomSlug,
            TimeSpan.FromHours(24));

        // Increment room active count
        await _redis.IncrementRoomCountAsync(roomSlug);

        // Send last 50 messages to the joining user
        var messages = await _mongo.GetRoomMessagesAsync(roomSlug, 0, 50);
        await Clients.Caller.SendAsync("RoomHistory", new
        {
            roomSlug,
            messages = messages.Select(MapMessage)
        });

        // Notify others in room
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
    //  Client calls this to exit a room cleanly
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
    //  Main message flow:
    //  1. Save to MongoDB (status = pending)
    //  2. Broadcast immediately to room
    //  3. OpenAI moderation runs async — updates status after
    // ============================================================
    public async Task SendMessage(string roomSlug, string content, string? replyToId = null)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);
        var trustScore = JwtService.GetTrustScore(Context.User!);

        // Basic validation
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

        // Increment room message count
        await _mongo.IncrementRoomMessageCountAsync(roomSlug);

        // Broadcast to everyone in room immediately
        await Clients.Group(roomSlug).SendAsync("ReceiveMessage", MapMessage(saved));

        // Mark user active day (fire and forget)
        _ = Task.Run(async () =>
        {
            try { await _postgres.MarkUserActiveDayAsync(Guid.Parse(userId)); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to mark active day"); }
        });

        _logger.LogInformation("Message sent by {Username} in {Room} [{MessageId}]",
            username, roomSlug, saved.Id);
    }

    // ============================================================
    //  SendTyping
    //  Broadcast typing indicator to room (except sender)
    // ============================================================
    public async Task SendTyping(string roomSlug)
    {
        var username = JwtService.GetUsername(Context.User!);
        await Clients.OthersInGroup(roomSlug).SendAsync("UserTyping", new { username });
    }

    // ============================================================
    //  ReactToMessage
    //  Add/remove emoji reaction on a message
    // ============================================================
    public async Task ReactToMessage(string roomSlug, string messageId, string emoji)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();

        // TODO: update reaction in MongoDB
        // For now broadcast reaction event
        await Clients.Group(roomSlug).SendAsync("MessageReaction", new
        {
            messageId,
            emoji,
            userId
        });
    }

    // ============================================================
    //  Private helpers
    // ============================================================
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