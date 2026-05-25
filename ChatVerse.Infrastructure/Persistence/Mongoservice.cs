using ChatVerse.Domain.Constants;
using ChatVerse.Domain.Entities;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;

namespace ChatVerse.Infrastructure.Persistence.MongoDB;

/// <summary>
/// All MongoDB access — collections + aggregation pipelines.
/// No raw queries in controllers — everything goes through here.
/// </summary>
public class MongoService
{
    private readonly IMongoDatabase _db;

    // Typed collections
    private IMongoCollection<Message> Messages => _db.GetCollection<Message>(MongoCollections.Messages);
    private IMongoCollection<Room> Rooms => _db.GetCollection<Room>(MongoCollections.Rooms);
    private IMongoCollection<ModerationLog> ModerationLogs => _db.GetCollection<ModerationLog>(MongoCollections.ModerationLogs);
    private IMongoCollection<VideoSession> VideoSessions => _db.GetCollection<VideoSession>(MongoCollections.VideoSessions);

    public MongoService(IMongoClient client, string databaseName)
    {
        // Register camelCase convention — matches MongoDB field names (isActive, displayName etc)
        var pack = new ConventionPack { new CamelCaseElementNameConvention() };
        ConventionRegistry.Register("camelCase", pack, _ => true);

        _db = client.GetDatabase(databaseName);
    }

    // ============================================================
    //  MESSAGES
    // ============================================================

    /// <summary>
    /// Paginated room messages — newest first, skip deleted.
    /// Maps to pipe_get_room_messages pipeline.
    /// </summary>
    public async Task<List<Message>> GetRoomMessagesAsync(
        string roomId, int skip = 0, int limit = Pagination.DefaultPageSize)
    {
        var filter = Builders<Message>.Filter.And(
            Builders<Message>.Filter.Eq(m => m.RoomId, roomId),
            Builders<Message>.Filter.Ne(m => m.IsDeleted, true)
        );

        return await Messages
            .Find(filter)
            .SortByDescending(m => m.CreatedAt)
            .Skip(skip)
            .Limit(limit)
            .ToListAsync();
    }

    /// <summary>
    /// Insert new message — moderation status starts as 'pending'.
    /// </summary>
    public async Task<Message> InsertMessageAsync(Message message)
    {
        message.CreatedAt = DateTime.UtcNow;
        message.Moderation.Status = "pending";
        await Messages.InsertOneAsync(message);
        return message;
    }

    /// <summary>
    /// Update moderation status after OpenAI check completes.
    /// </summary>
    public async Task UpdateModerationStatusAsync(
        string messageId, string status, string? flagReason, double? confidence)
    {
        var filter = Builders<Message>.Filter.Eq(m => m.Id, messageId);
        var update = Builders<Message>.Update
            .Set(m => m.Moderation.Status, status)
            .Set(m => m.Moderation.CheckedBy, "openai")
            .Set(m => m.Moderation.FlagReason, flagReason)
            .Set(m => m.Moderation.Confidence, confidence);

        await Messages.UpdateOneAsync(filter, update);
    }

    /// <summary>
    /// Soft delete a message.
    /// </summary>
    public async Task SoftDeleteMessageAsync(string messageId)
    {
        var filter = Builders<Message>.Filter.Eq(m => m.Id, messageId);
        var update = Builders<Message>.Update
            .Set(m => m.IsDeleted, true)
            .Set(m => m.DeletedAt, DateTime.UtcNow);

        await Messages.UpdateOneAsync(filter, update);
    }

    /// <summary>
    /// Flagged/blocked messages for admin dashboard.
    /// Maps to pipe_get_flagged_messages pipeline.
    /// </summary>
    public async Task<List<Message>> GetFlaggedMessagesAsync(
        string status = "flagged", int limit = 100)
    {
        var filter = Builders<Message>.Filter.And(
            Builders<Message>.Filter.Eq(m => m.Moderation.Status, status),
            Builders<Message>.Filter.Ne(m => m.IsDeleted, true)
        );

        return await Messages
            .Find(filter)
            .SortByDescending(m => m.CreatedAt)
            .Limit(limit)
            .ToListAsync();
    }

    /// <summary>
    /// User message stats — total, flagged, blocked counts.
    /// Maps to pipe_get_user_message_stats pipeline.
    /// </summary>
    public async Task<UserMessageStats> GetUserMessageStatsAsync(string userId)
    {
        var pipeline = new[]
        {
            new BsonDocument("$match",  new BsonDocument("senderId", userId)),
            new BsonDocument("$group",  new BsonDocument
            {
                { "_id",           "$senderId" },
                { "totalMessages", new BsonDocument("$sum", 1) },
                { "flaggedCount",  new BsonDocument("$sum",
                    new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$eq", new BsonArray { "$moderation.status", "flagged" }), 1, 0
                    }))
                },
                { "blockedCount",  new BsonDocument("$sum",
                    new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$eq", new BsonArray { "$moderation.status", "blocked" }), 1, 0
                    }))
                },
                { "lastMessageAt", new BsonDocument("$max", "$createdAt") }
            }),
            new BsonDocument("$addFields", new BsonDocument("flagRate",
                new BsonDocument("$cond", new BsonArray
                {
                    new BsonDocument("$gt", new BsonArray { "$totalMessages", 0 }),
                    new BsonDocument("$divide", new BsonArray { "$flaggedCount", "$totalMessages" }),
                    0
                })))
        };

        var result = await Messages.Aggregate<BsonDocument>(pipeline).FirstOrDefaultAsync();
        if (result == null) return new UserMessageStats();

        return new UserMessageStats
        {
            TotalMessages = result.GetValue("totalMessages", 0).AsInt32,
            FlaggedCount = result.GetValue("flaggedCount", 0).AsInt32,
            BlockedCount = result.GetValue("blockedCount", 0).AsInt32,
            FlagRate = result.GetValue("flagRate", 0).ToDouble()
        };
    }

    // ============================================================
    //  ROOMS
    // ============================================================

    /// <summary>
    /// All active rooms sorted by online users.
    /// Maps to pipe_get_active_rooms pipeline.
    /// </summary>
    public async Task<List<Room>> GetActiveRoomsAsync()
    {
        var filter = Builders<Room>.Filter.Eq(r => r.IsActive, true);
        return await Rooms
            .Find(filter)
            .SortByDescending(r => r.Stats.ActiveNow)
            .ThenByDescending(r => r.Stats.TotalMessages)
            .ToListAsync();
    }

    /// <summary>
    /// Get single room by slug.
    /// </summary>
    public async Task<Room?> GetRoomBySlugAsync(string slug)
    {
        return await Rooms
            .Find(r => r.Slug == slug)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Increment total message count for a room.
    /// </summary>
    public async Task IncrementRoomMessageCountAsync(string roomSlug)
    {
        var filter = Builders<Room>.Filter.Eq(r => r.Slug, roomSlug);
        var update = Builders<Room>.Update.Inc("stats.totalMessages", 1);
        await Rooms.UpdateOneAsync(filter, update);
    }

    // ============================================================
    //  MODERATION LOGS
    // ============================================================

    /// <summary>
    /// Save full moderation log entry after AI check.
    /// </summary>
    public async Task InsertModerationLogAsync(ModerationLog log)
    {
        log.CreatedAt = DateTime.UtcNow;
        await ModerationLogs.InsertOneAsync(log);
    }

    /// <summary>
    /// Daily moderation summary for admin dashboard.
    /// Maps to pipe_get_moderation_summary pipeline.
    /// </summary>
    public async Task<List<BsonDocument>> GetModerationSummaryAsync(
        DateTime from, DateTime to)
    {
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument("createdAt",
                new BsonDocument { { "$gte", from }, { "$lte", to } })),
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", new BsonDocument
                    {
                        { "date",   new BsonDocument("$dateToString",
                            new BsonDocument { { "format", "%Y-%m-%d" }, { "date", "$createdAt" } }) },
                        { "action", "$action" },
                        { "source", "$source" }
                    }
                },
                { "count", new BsonDocument("$sum", 1) }
            }),
            new BsonDocument("$sort", new BsonDocument("_id.date", -1))
        };

        return await ModerationLogs.Aggregate<BsonDocument>(pipeline).ToListAsync();
    }

    // ============================================================
    //  VIDEO SESSIONS
    // ============================================================

    /// <summary>
    /// Create a new video session when two users are matched.
    /// </summary>
    public async Task<VideoSession> CreateVideoSessionAsync(VideoSession session)
    {
        session.StartedAt = DateTime.UtcNow;
        session.Outcome = "clean";
        await VideoSessions.InsertOneAsync(session);
        return session;
    }

    /// <summary>
    /// End a video session — set duration + endReason.
    /// </summary>
    public async Task EndVideoSessionAsync(
        string sessionId, string endReason, string outcome)
    {
        var endedAt = DateTime.UtcNow;
        var filter = Builders<VideoSession>.Filter.Eq(v => v.SessionId, sessionId);

        var session = await VideoSessions.Find(filter).FirstOrDefaultAsync();
        if (session == null) return;

        var duration = (int)(endedAt - session.StartedAt).TotalSeconds;
        var update = Builders<VideoSession>.Update
            .Set(v => v.EndedAt, endedAt)
            .Set(v => v.DurationSeconds, duration)
            .Set(v => v.EndReason, endReason)
            .Set(v => v.Outcome, outcome);

        await VideoSessions.UpdateOneAsync(filter, update);
    }

    /// <summary>
    /// Append an NSFW flag detected by nsfwjs on client.
    /// </summary>
    public async Task AppendNsfwFlagAsync(string sessionId, NsfwFlag flag)
    {
        var filter = Builders<VideoSession>.Filter.Eq(v => v.SessionId, sessionId);
        var update = Builders<VideoSession>.Update.Push(v => v.NsfwFlags, flag);
        await VideoSessions.UpdateOneAsync(filter, update);
    }

    /// <summary>
    /// User's video session history.
    /// Maps to pipe_get_user_video_history pipeline.
    /// </summary>
    public async Task<List<VideoSession>> GetUserVideoHistoryAsync(
        string userId, int limit = 20)
    {
        var filter = Builders<VideoSession>.Filter
            .ElemMatch(v => v.Participants, p => p.UserId == userId);

        return await VideoSessions
            .Find(filter)
            .SortByDescending(v => v.StartedAt)
            .Limit(limit)
            .ToListAsync();
    }
}

// ── Supporting result types ───────────────────────────────────
public class UserMessageStats
{
    public int TotalMessages { get; set; }
    public int FlaggedCount { get; set; }
    public int BlockedCount { get; set; }
    public double FlagRate { get; set; }
}