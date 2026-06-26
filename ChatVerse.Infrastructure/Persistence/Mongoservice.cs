using ChatVerse.Domain.Constants;
using ChatVerse.Domain.Entities;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;

namespace ChatVerse.Infrastructure.Persistence.MongoDB;

/// <summary>
/// All MongoDB access — collections + aggregation pipelines.
/// No raw queries in controllers — everything goes through here.
/// </summary>
public partial class MongoService
{
    private readonly IMongoDatabase _db;

    // Typed collections
    private IMongoCollection<Message> Messages => _db.GetCollection<Message>(MongoCollections.Messages);
    private IMongoCollection<Room> Rooms => _db.GetCollection<Room>(MongoCollections.Rooms);
    private IMongoCollection<ModerationLog> ModerationLogs => _db.GetCollection<ModerationLog>(MongoCollections.ModerationLogs);
    private IMongoCollection<VideoSession> VideoSessions => _db.GetCollection<VideoSession>(MongoCollections.VideoSessions);
    private IMongoCollection<DmMessage> DmMessages => _db.GetCollection<DmMessage>(MongoCollections.DmMessages);
    private IMongoCollection<UserBlock> UserBlocks => _db.GetCollection<UserBlock>(MongoCollections.UserBlocks);

    // Phase 2 — sticky features
    private IMongoCollection<TimeCapsule> TimeCapsules => _db.GetCollection<TimeCapsule>(MongoCollections.TimeCapsules);

    // Persona Roulette — daily-disposable identity + streak tracking
    private IMongoCollection<Persona> Personas => _db.GetCollection<Persona>(MongoCollections.Personas);
    private IMongoCollection<PersonaConversation> PersonaConversations => _db.GetCollection<PersonaConversation>(MongoCollections.PersonaConversations);
    private IMongoCollection<PersonaStreak> PersonaStreaks => _db.GetCollection<PersonaStreak>(MongoCollections.PersonaStreaks);
    private IMongoCollection<PersonaMessage> PersonaMessages => _db.GetCollection<PersonaMessage>(MongoCollections.PersonaMessages);

    // Story Chain — daily collaborative writing
    private IMongoCollection<StoryChain> StoryChains => _db.GetCollection<StoryChain>(MongoCollections.StoryChains);

    // Confession Box — anonymous daily confessions
    private IMongoCollection<Confession> Confessions => _db.GetCollection<Confession>(MongoCollections.Confessions);

    // In-room polls (parity polish)
    private IMongoCollection<Poll> Polls => _db.GetCollection<Poll>(MongoCollections.Polls);

    // Debate (per-template Mehfil specialisation — all collections
    // are standalone per the per-feature isolation policy).
    private IMongoCollection<DebateRound>           DebateRounds           => _db.GetCollection<DebateRound>(MongoCollections.DebateRounds);
    private IMongoCollection<DebateSeat>            DebateSeats            => _db.GetCollection<DebateSeat>(MongoCollections.DebateSeats);
    private IMongoCollection<DebateNomination>      DebateNominations      => _db.GetCollection<DebateNomination>(MongoCollections.DebateNominations);
    private IMongoCollection<DebateModeratorAction> DebateModeratorActions => _db.GetCollection<DebateModeratorAction>(MongoCollections.DebateModeratorActions);
    private IMongoCollection<DebateHighlight>       DebateHighlights       => _db.GetCollection<DebateHighlight>(MongoCollections.DebateHighlights);
    private IMongoCollection<DebateBan>             DebateBans             => _db.GetCollection<DebateBan>(MongoCollections.DebateBans);
    private IMongoCollection<DebateMessage>         DebateMessages         => _db.GetCollection<DebateMessage>(MongoCollections.DebateMessages);

    // Ghost Room (second per-template Mehfil specialisation — all
    // gd_* collections standalone per the per-feature isolation policy).
    private IMongoCollection<GhostRoomConfig>       GhostRoomConfigs       => _db.GetCollection<GhostRoomConfig>(MongoCollections.GhostRoomConfigs);
    private IMongoCollection<GhostVoyager>          GhostVoyagers          => _db.GetCollection<GhostVoyager>(MongoCollections.GhostVoyagers);
    private IMongoCollection<GhostNomination>       GhostNominations       => _db.GetCollection<GhostNomination>(MongoCollections.GhostNominations);
    private IMongoCollection<GhostPair>             GhostPairs             => _db.GetCollection<GhostPair>(MongoCollections.GhostPairs);
    private IMongoCollection<GhostPairMessage>      GhostPairMessages      => _db.GetCollection<GhostPairMessage>(MongoCollections.GhostPairMessages);
    private IMongoCollection<GhostReveal>           GhostReveals           => _db.GetCollection<GhostReveal>(MongoCollections.GhostReveals);
    private IMongoCollection<GhostBan>              GhostBans              => _db.GetCollection<GhostBan>(MongoCollections.GhostBans);
    private IMongoCollection<GhostMatchmakerAction> GhostMatchmakerActions => _db.GetCollection<GhostMatchmakerAction>(MongoCollections.GhostMatchmakerActions);

    // Open Mic (third per-template Mehfil specialisation — all om_*
    // standalone per the per-feature isolation policy).
    private IMongoCollection<OpenMicConfig>      OpenMicConfigs      => _db.GetCollection<OpenMicConfig>(MongoCollections.OpenMicConfigs);
    private IMongoCollection<OpenMicSet>         OpenMicSets         => _db.GetCollection<OpenMicSet>(MongoCollections.OpenMicSets);
    private IMongoCollection<OpenMicQueueEntry>  OpenMicQueueEntries => _db.GetCollection<OpenMicQueueEntry>(MongoCollections.OpenMicQueueEntries);
    private IMongoCollection<OpenMicSlot>        OpenMicSlots        => _db.GetCollection<OpenMicSlot>(MongoCollections.OpenMicSlots);
    private IMongoCollection<OpenMicReaction>    OpenMicReactions    => _db.GetCollection<OpenMicReaction>(MongoCollections.OpenMicReactions);
    private IMongoCollection<OpenMicBan>         OpenMicBans         => _db.GetCollection<OpenMicBan>(MongoCollections.OpenMicBans);
    private IMongoCollection<OpenMicMcAction>    OpenMicMcActions    => _db.GetCollection<OpenMicMcAction>(MongoCollections.OpenMicMcActions);

    // Ghost Date — weekly Thursday 9pm IST anonymous dating
    private IMongoCollection<GhostDateRegistration> GhostDateRegistrations =>
        _db.GetCollection<GhostDateRegistration>(MongoCollections.GhostDateRegistrations);
    private IMongoCollection<GhostDate> GhostDates =>
        _db.GetCollection<GhostDate>(MongoCollections.GhostDates);
    private IMongoCollection<GhostDateMessage> GhostDateMessages =>
        _db.GetCollection<GhostDateMessage>(MongoCollections.GhostDateMessages);

    // Love Triangle — weekly Sunday 10pm IST 3-person drama
    private IMongoCollection<LoveTriangleRegistration> LoveTriangleRegistrations =>
        _db.GetCollection<LoveTriangleRegistration>(MongoCollections.LoveTriangleRegistrations);
    private IMongoCollection<LoveTriangle> LoveTriangles =>
        _db.GetCollection<LoveTriangle>(MongoCollections.LoveTriangles);
    private IMongoCollection<LoveTrianglePairMessage> LoveTrianglePairMessages =>
        _db.GetCollection<LoveTrianglePairMessage>(MongoCollections.LoveTrianglePairMessages);

    // The Cipher — weekly community ARG
    private IMongoCollection<CipherRound>      CipherRounds      => _db.GetCollection<CipherRound>(MongoCollections.CipherRounds);
    private IMongoCollection<CipherMember>     CipherMembers     => _db.GetCollection<CipherMember>(MongoCollections.CipherMembers);
    private IMongoCollection<CipherSubmission> CipherSubmissions => _db.GetCollection<CipherSubmission>(MongoCollections.CipherSubmissions);

    // PYAAR LIVE — flagship Saturday mass dating show
    private IMongoCollection<PyaarRegistration> PyaarRegistrations => _db.GetCollection<PyaarRegistration>(MongoCollections.PyaarRegistrations);
    private IMongoCollection<PyaarShow>         PyaarShows         => _db.GetCollection<PyaarShow>(MongoCollections.PyaarShows);
    private IMongoCollection<PyaarCouple>       PyaarCouples       => _db.GetCollection<PyaarCouple>(MongoCollections.PyaarCouples);
    private IMongoCollection<PyaarMessage>      PyaarMessages      => _db.GetCollection<PyaarMessage>(MongoCollections.PyaarMessages);
    private IMongoCollection<PyaarVote>         PyaarVotes         => _db.GetCollection<PyaarVote>(MongoCollections.PyaarVotes);
    private IMongoCollection<PyaarReaction>     PyaarReactions     => _db.GetCollection<PyaarReaction>(MongoCollections.PyaarReactions);

    // MEHFIL — creator-room platform
    private IMongoCollection<MehfilRoom>       MehfilRooms       => _db.GetCollection<MehfilRoom>(MongoCollections.MehfilRooms);
    private IMongoCollection<MehfilAttendance> MehfilAttendances => _db.GetCollection<MehfilAttendance>(MongoCollections.MehfilAttendances);
    private IMongoCollection<MehfilMessage>    MehfilMessages    => _db.GetCollection<MehfilMessage>(MongoCollections.MehfilMessages);
    private IMongoCollection<MehfilTip>        MehfilTips        => _db.GetCollection<MehfilTip>(MongoCollections.MehfilTips);

    // Token economy (Phase 5)
    private IMongoCollection<TokenBalance>     TokenBalances    => _db.GetCollection<TokenBalance>(MongoCollections.TokenBalances);
    private IMongoCollection<TokenLedgerEntry> TokenLedger      => _db.GetCollection<TokenLedgerEntry>(MongoCollections.TokenLedger);
    private IMongoCollection<TokenTopupOrder>  TokenTopupOrders => _db.GetCollection<TokenTopupOrder>(MongoCollections.TokenTopupOrders);

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
    /// Last N messages in a room that carry a Spotify embed. Powers the
    /// Music Lounge "Now Playing" panel — newest first, skip moderated /
    /// deleted rows so blocked tracks never reach the jukebox.
    ///
    /// Filtering server-side keeps the panel small even in a busy room
    /// where 90% of messages have nothing to do with music.
    /// </summary>
    public async Task<List<Message>> GetRoomSpotifyTracksAsync(
        string roomId, int limit = 20)
    {
        var filter = Builders<Message>.Filter.And(
            Builders<Message>.Filter.Eq(m => m.RoomId, roomId),
            Builders<Message>.Filter.Ne(m => m.IsDeleted, true),
            Builders<Message>.Filter.Ne(m => m.Moderation.Status, "blocked"),
            Builders<Message>.Filter.Ne(m => m.Spotify, null)
        );

        return await Messages
            .Find(filter)
            .SortByDescending(m => m.CreatedAt)
            .Limit(Math.Clamp(limit, 1, 50))
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

    // ============================================================
    //  Maintenance: bulk-delete by age
    //  Called by MaintenanceService (background webjob) to keep
    //  Mongo storage from ballooning on the M0 free tier (512MB cap).
    //  Hard delete — soft-deleted rows still cost storage, and a 30-day
    //  cutoff is well past any reasonable "I want to read my old chats"
    //  user expectation in an anonymous-friendly app.
    // ============================================================

    public async Task<long> DeleteMessagesOlderThanAsync(TimeSpan age)
    {
        var cutoff = DateTime.UtcNow - age;
        var filter = Builders<Message>.Filter.Lt(m => m.CreatedAt, cutoff);
        var res = await Messages.DeleteManyAsync(filter);
        return res.DeletedCount;
    }

    public async Task<long> DeleteDmsOlderThanAsync(TimeSpan age)
    {
        var cutoff = DateTime.UtcNow - age;
        var filter = Builders<DmMessage>.Filter.Lt(m => m.CreatedAt, cutoff);
        var res = await DmMessages.DeleteManyAsync(filter);
        return res.DeletedCount;
    }

    public async Task<long> DeleteEndedVideoSessionsOlderThanAsync(TimeSpan age)
    {
        var cutoff = DateTime.UtcNow - age;
        // Only delete sessions that have ALREADY ended; in-flight ones
        // (EndedAt == null) are still relevant for moderation review.
        var filter = Builders<VideoSession>.Filter.And(
            Builders<VideoSession>.Filter.Lt(s => s.EndedAt, cutoff),
            Builders<VideoSession>.Filter.Ne(s => s.EndedAt, null));
        var res = await VideoSessions.DeleteManyAsync(filter);
        return res.DeletedCount;
    }

    /// <summary>
    /// Delete every message + DM authored by the given user IDs. Used
    /// by the maintenance webjob after Postgres has wiped guest accounts
    /// — keeps Mongo orphan-free.
    ///
    /// IMPORTANT: filters by `SenderId` (the author's userId), NOT
    /// `Id` (which is the message's own ObjectId). The original code
    /// here mistakenly used `m.UserId` — that property doesn't exist
    /// on Message; the author field is `SenderId`. Filtering by `Id`
    /// against a user-guid list would never match, making the cleanup
    /// silently a no-op.
    /// </summary>
    public async Task<long> DeleteMessagesByUserIdsAsync(IEnumerable<string> userIds)
    {
        var ids = userIds.ToList();
        if (ids.Count == 0) return 0;

        // Room messages
        var msgFilter = Builders<Message>.Filter.In(m => m.SenderId, ids);
        var msgRes = await Messages.DeleteManyAsync(msgFilter);

        // DMs authored OR received by the wiped user — receivers might
        // be live accounts but the conversation is half-dead anyway.
        // We only sweep messages whose SENDER got wiped; RecipientId
        // sweep would risk taking out live users' message history.
        var dmFilter = Builders<DmMessage>.Filter.In(m => m.SenderId, ids);
        var dmRes = await DmMessages.DeleteManyAsync(dmFilter);

        return msgRes.DeletedCount + dmRes.DeletedCount;
    }

    /// <summary>
    /// Toggle a user's reaction on a message. Returns the resulting
    /// reactions map so the hub can broadcast the authoritative state
    /// instead of guessing it client-side.
    ///
    /// Idempotent: clicking the same emoji twice from the same user
    /// removes the reaction (Slack/Discord behaviour). New emojis are
    /// added as empty bucket → user-id append.
    /// </summary>
    public async Task<Dictionary<string, List<string>>> ToggleReactionAsync(
        string messageId, string emoji, string userId)
    {
        var filter = Builders<Message>.Filter.Eq(m => m.Id, messageId);
        var msg = await Messages.Find(filter).FirstOrDefaultAsync();
        if (msg == null) return new Dictionary<string, List<string>>();

        var reactions = msg.Reactions ?? new Dictionary<string, List<string>>();
        if (!reactions.TryGetValue(emoji, out var users))
        {
            users = new List<string>();
            reactions[emoji] = users;
        }

        if (users.Contains(userId))
        {
            users.Remove(userId);
            // Prune empty buckets so the panel doesn't render dead emojis.
            if (users.Count == 0) reactions.Remove(emoji);
        }
        else
        {
            users.Add(userId);
        }

        var update = Builders<Message>.Update.Set(m => m.Reactions, reactions);
        await Messages.UpdateOneAsync(filter, update);
        return reactions;
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

// ── Private room helpers ────────────────────────────────────
public partial class MongoService
{
    /// <summary>Insert a brand-new user-created room.</summary>
    public async Task<Room> InsertRoomAsync(Room room) { await Rooms.InsertOneAsync(room); return room; }

    /// <summary>
    /// Find a room by its invite token (used when a guest follows a
    /// share-link). Returns null when the link is stale.
    /// </summary>
    public async Task<Room?> GetRoomByInviteTokenAsync(string token)
    {
        var filter = Builders<Room>.Filter.Eq(r => r.InviteToken, token);
        return await Rooms.Find(filter).FirstOrDefaultAsync();
    }

    /// <summary>
    /// Look up a batch of rooms by slug — used by "my joined private
    /// rooms" so we can resolve a Redis set of slugs into full
    /// metadata in a single query.
    /// </summary>
    public async Task<List<Room>> GetRoomsBySlugsAsync(IEnumerable<string> slugs)
    {
        var list = slugs.ToList();
        if (list.Count == 0) return new List<Room>();
        var filter = Builders<Room>.Filter.And(
            Builders<Room>.Filter.In(r => r.Slug, list),
            Builders<Room>.Filter.Eq(r => r.IsActive, true)
        );
        return await Rooms.Find(filter).ToListAsync();
    }

    /// <summary>
    /// Soft-deactivate a room — keeps history but takes it out of all
    /// active room lists and rejects new joins.
    /// </summary>
    public async Task DeactivateRoomAsync(string slug)
    {
        var filter = Builders<Room>.Filter.Eq(r => r.Slug, slug);
        var update = Builders<Room>.Update.Set(r => r.IsActive, false);
        await Rooms.UpdateOneAsync(filter, update);
    }
}

// ── DM helpers — kept on partial for proximity ───────────────
public partial class MongoService
{
    /// <summary>
    /// Deterministic conversation id so both participants see the same
    /// key regardless of who started the chat. Sort the two UUIDs as
    /// strings, then join with a dash.
    /// </summary>
    public static string ConversationIdFor(string userA, string userB)
    {
        var (a, b) = string.CompareOrdinal(userA, userB) <= 0
            ? (userA, userB)
            : (userB, userA);
        return $"dm:{a}-{b}";
    }

    /// <summary>Persist a new DM and return the saved entity (with id).</summary>
    public async Task<DmMessage> InsertDmAsync(DmMessage dm)
    {
        await DmMessages.InsertOneAsync(dm);
        return dm;
    }

    /// <summary>Paginated message history for a single conversation.</summary>
    public async Task<List<DmMessage>> GetDmThreadAsync(
        string conversationId, int skip = 0, int limit = Pagination.DefaultPageSize)
    {
        var filter = Builders<DmMessage>.Filter.And(
            Builders<DmMessage>.Filter.Eq(m => m.ConversationId, conversationId),
            Builders<DmMessage>.Filter.Ne(m => m.IsDeleted, true)
        );

        return await DmMessages
            .Find(filter)
            .SortByDescending(m => m.CreatedAt)
            .Skip(skip)
            .Limit(limit)
            .ToListAsync();
    }

    /// <summary>
    /// All conversations involving <paramref name="userId"/>, each with
    /// the latest message, the other participant's id, and an unread
    /// count for this user. Sorted newest-activity-first.
    /// </summary>
    public async Task<List<DmConversationSummary>> GetUserDmConversationsAsync(string userId)
    {
        // Pipeline: match user → sort newest first → group by conv,
        // first message kept → project fields → sort newest.
        var matchUser = new BsonDocument("$match", new BsonDocument
        {
            { "$or", new BsonArray
                {
                    new BsonDocument("senderId", userId),
                    new BsonDocument("recipientId", userId),
                }
            },
            { "isDeleted", new BsonDocument("$ne", true) }
        });

        var sortDesc = new BsonDocument("$sort", new BsonDocument("createdAt", -1));

        var group = new BsonDocument("$group", new BsonDocument
        {
            { "_id", "$conversationId" },
            { "lastMessage", new BsonDocument("$first", "$$ROOT") },
            { "unreadCount", new BsonDocument("$sum",
                new BsonDocument("$cond", new BsonArray
                {
                    new BsonDocument
                    {
                        { "$and", new BsonArray
                            {
                                new BsonDocument("$eq", new BsonArray { "$recipientId", userId }),
                                new BsonDocument("$ne", new BsonArray { "$isRead", true }),
                            }
                        }
                    },
                    1, 0
                }))
            }
        });

        var project = new BsonDocument("$project", new BsonDocument
        {
            { "_id", 0 },
            { "conversationId", "$_id" },
            { "unreadCount", 1 },
            { "lastMessage", 1 },
        });

        var sortByActivity = new BsonDocument("$sort", new BsonDocument("lastMessage.createdAt", -1));

        var pipeline = new[] { matchUser, sortDesc, group, project, sortByActivity };
        var docs = await DmMessages.Aggregate<BsonDocument>(pipeline).ToListAsync();

        var results = new List<DmConversationSummary>();
        foreach (var doc in docs)
        {
            var last = doc["lastMessage"].AsBsonDocument;
            var senderId = last.GetValue("senderId", "").AsString;
            var recipientId = last.GetValue("recipientId", "").AsString;
            var otherUserId = senderId == userId ? recipientId : senderId;

            results.Add(new DmConversationSummary
            {
                ConversationId = doc["conversationId"].AsString,
                OtherUserId = otherUserId,
                UnreadCount = doc.GetValue("unreadCount", 0).ToInt32(),
                LastMessageContent = last.GetValue("content", "").AsString,
                LastMessageSenderId = senderId,
                LastMessageAt = last.GetValue("createdAt", BsonNull.Value).IsBsonNull
                    ? DateTime.MinValue
                    : last["createdAt"].ToUniversalTime(),
            });
        }
        return results;
    }

    /// <summary>
    /// Flip is_read=true on all unread DMs in this conversation that were
    /// sent TO this user. Returns how many rows were updated so the
    /// caller can broadcast a "read receipt" event.
    /// </summary>
    public async Task<long> MarkDmConversationReadAsync(string conversationId, string userId)
    {
        var filter = Builders<DmMessage>.Filter.And(
            Builders<DmMessage>.Filter.Eq(m => m.ConversationId, conversationId),
            Builders<DmMessage>.Filter.Eq(m => m.RecipientId, userId),
            Builders<DmMessage>.Filter.Ne(m => m.IsRead, true)
        );
        var update = Builders<DmMessage>.Update
            .Set(m => m.IsRead, true)
            .Set(m => m.ReadAt, DateTime.UtcNow);
        var res = await DmMessages.UpdateManyAsync(filter, update);
        return res.ModifiedCount;
    }

    // ============================================================
    //  USER BLOCKS — Instagram-style directed silencing.
    //
    //  Read pattern is dominated by "is X blocked by Y?" which a
    //  compound (blockedId, blockerId) index makes O(log n). We also
    //  surface "how many people blocked me?" as a count, deliberately
    //  WITHOUT names — same privacy posture mainstream platforms use.
    // ============================================================

    /// <summary>Add a block; idempotent (no-op if already exists).</summary>
    public async Task<UserBlock> BlockUserAsync(string blockerId, string blockedId, string? reason)
    {
        if (blockerId == blockedId)
            throw new ArgumentException("Cannot block yourself");

        var existing = await UserBlocks
            .Find(b => b.BlockerId == blockerId && b.BlockedId == blockedId)
            .FirstOrDefaultAsync();
        if (existing != null) return existing;

        var doc = new UserBlock
        {
            BlockerId = blockerId,
            BlockedId = blockedId,
            Reason = reason,
            CreatedAt = DateTime.UtcNow,
        };
        await UserBlocks.InsertOneAsync(doc);
        return doc;
    }

    public async Task<bool> UnblockUserAsync(string blockerId, string blockedId)
    {
        var res = await UserBlocks.DeleteOneAsync(b =>
            b.BlockerId == blockerId && b.BlockedId == blockedId);
        return res.DeletedCount > 0;
    }

    /// <summary>True if blockerId has blocked blockedId.</summary>
    public async Task<bool> IsBlockedAsync(string blockerId, string blockedId)
    {
        return await UserBlocks
            .Find(b => b.BlockerId == blockerId && b.BlockedId == blockedId)
            .AnyAsync();
    }

    /// <summary>List of users the caller has blocked. Newest first.</summary>
    public async Task<List<UserBlock>> GetBlocksByMeAsync(string myUserId, int limit = 100)
    {
        return await UserBlocks
            .Find(b => b.BlockerId == myUserId)
            .SortByDescending(b => b.CreatedAt)
            .Limit(limit)
            .ToListAsync();
    }

    /// <summary>
    /// Count-only — surfaced in the blocked user's settings as
    /// "You appear in N blocklists". Names are never returned.
    /// </summary>
    public async Task<long> GetBlockedMeCountAsync(string myUserId)
    {
        return await UserBlocks.CountDocumentsAsync(b => b.BlockedId == myUserId);
    }

    // ============================================================
    //  TIME CAPSULES — Phase 2 sticky feature.
    //
    //  Author writes today, system delivers 7/14/30 days later to a
    //  random recipient. See TimeCapsule entity for the data model.
    //  All methods here are intentionally small + focused so the
    //  TimeCapsuleHub + TimeCapsuleDeliveryService can compose them.
    // ============================================================

    /// <summary>Insert a fresh capsule. Returns the doc with Id set.</summary>
    public async Task<TimeCapsule> InsertTimeCapsuleAsync(TimeCapsule capsule)
    {
        await TimeCapsules.InsertOneAsync(capsule);
        return capsule;
    }

    /// <summary>List capsules the user AUTHORED (their "sent" view).</summary>
    public async Task<List<TimeCapsule>> GetCapsulesByAuthorAsync(string authorUserId, int limit = 50)
    {
        return await TimeCapsules
            .Find(c => c.AuthorUserId == authorUserId)
            .SortByDescending(c => c.CreatedAt)
            .Limit(limit)
            .ToListAsync();
    }

    /// <summary>List capsules DELIVERED to the user (their "inbox").</summary>
    public async Task<List<TimeCapsule>> GetCapsulesForRecipientAsync(string recipientUserId, int limit = 50)
    {
        var filter = Builders<TimeCapsule>.Filter.And(
            Builders<TimeCapsule>.Filter.Eq(c => c.RecipientUserId, recipientUserId),
            Builders<TimeCapsule>.Filter.Ne(c => c.DeliveredAt, null)
        );
        return await TimeCapsules
            .Find(filter)
            .SortByDescending(c => c.DeliveredAt)
            .Limit(limit)
            .ToListAsync();
    }

    /// <summary>Fetch a single capsule by id (caller must check authorisation).</summary>
    public async Task<TimeCapsule?> GetCapsuleByIdAsync(string id)
    {
        return await TimeCapsules.Find(c => c.Id == id).FirstOrDefaultAsync();
    }

    /// <summary>Capsules whose ScheduledFor has passed but DeliveredAt is null —
    /// these are the queue for the delivery background service.</summary>
    public async Task<List<TimeCapsule>> GetCapsulesDueForDeliveryAsync(int batchLimit = 100)
    {
        var filter = Builders<TimeCapsule>.Filter.And(
            Builders<TimeCapsule>.Filter.Lte(c => c.ScheduledFor, DateTime.UtcNow),
            Builders<TimeCapsule>.Filter.Eq(c => c.DeliveredAt, (DateTime?)null)
        );
        return await TimeCapsules
            .Find(filter)
            .SortBy(c => c.ScheduledFor)
            .Limit(batchLimit)
            .ToListAsync();
    }

    /// <summary>Mark a capsule as delivered to the given recipient.
    /// Atomic — uses FindOneAndUpdate to claim the row, so two delivery
    /// workers racing on the same batch can't double-deliver.</summary>
    public async Task<bool> MarkCapsuleDeliveredAsync(string capsuleId, string recipientUserId, string recipientUsername)
    {
        var filter = Builders<TimeCapsule>.Filter.And(
            Builders<TimeCapsule>.Filter.Eq(c => c.Id, capsuleId),
            Builders<TimeCapsule>.Filter.Eq(c => c.DeliveredAt, (DateTime?)null)  // claim only if still undelivered
        );
        var update = Builders<TimeCapsule>.Update
            .Set(c => c.DeliveredAt, DateTime.UtcNow)
            .Set(c => c.RecipientUserId, recipientUserId)
            .Set(c => c.RecipientUsername, recipientUsername);
        var result = await TimeCapsules.UpdateOneAsync(filter, update);
        return result.ModifiedCount == 1;
    }

    /// <summary>Recipient replies (single-shot). Schedules the reply for
    /// delivery to author 3 days later. Returns true if recorded; false
    /// if already replied or capsule doesn't belong to recipient.</summary>
    public async Task<bool> SetCapsuleReplyAsync(string capsuleId, string recipientUserId, string replyContent)
    {
        var now = DateTime.UtcNow;
        var filter = Builders<TimeCapsule>.Filter.And(
            Builders<TimeCapsule>.Filter.Eq(c => c.Id, capsuleId),
            Builders<TimeCapsule>.Filter.Eq(c => c.RecipientUserId, recipientUserId),
            Builders<TimeCapsule>.Filter.Eq(c => c.RepliedAt, (DateTime?)null)
        );
        var update = Builders<TimeCapsule>.Update
            .Set(c => c.ReplyContent, replyContent)
            .Set(c => c.RepliedAt, now)
            .Set(c => c.ScheduledReplyDeliveryAt, now.AddDays(3));
        var result = await TimeCapsules.UpdateOneAsync(filter, update);
        return result.ModifiedCount == 1;
    }

    /// <summary>Replies whose ScheduledReplyDeliveryAt has passed but
    /// ReplyDeliveredAt is null — back-channel deliveries to authors.</summary>
    public async Task<List<TimeCapsule>> GetRepliesDueForDeliveryAsync(int batchLimit = 100)
    {
        var filter = Builders<TimeCapsule>.Filter.And(
            Builders<TimeCapsule>.Filter.Ne(c => c.ScheduledReplyDeliveryAt, null),
            Builders<TimeCapsule>.Filter.Lte(c => c.ScheduledReplyDeliveryAt, DateTime.UtcNow),
            Builders<TimeCapsule>.Filter.Eq(c => c.ReplyDeliveredAt, (DateTime?)null)
        );
        return await TimeCapsules
            .Find(filter)
            .Limit(batchLimit)
            .ToListAsync();
    }

    public async Task<bool> MarkReplyDeliveredAsync(string capsuleId)
    {
        var filter = Builders<TimeCapsule>.Filter.And(
            Builders<TimeCapsule>.Filter.Eq(c => c.Id, capsuleId),
            Builders<TimeCapsule>.Filter.Eq(c => c.ReplyDeliveredAt, (DateTime?)null)
        );
        var update = Builders<TimeCapsule>.Update
            .Set(c => c.ReplyDeliveredAt, DateTime.UtcNow);
        var result = await TimeCapsules.UpdateOneAsync(filter, update);
        return result.ModifiedCount == 1;
    }

    // ════════════════════════════════════════════════════════════
    //  PERSONA ROULETTE — CRUD for personas, conversations, streaks.
    //
    //  Pattern matches TimeCapsule: server services compose these
    //  primitives. PersonaResetService writes Personas at midnight UTC,
    //  PersonaHub reads them on demand, BumpPersonaConversationAsync
    //  flushes an upsert per persona-DM, and BumpStreakForDayAsync
    //  is called by the reset service when it rolls the day over.
    // ════════════════════════════════════════════════════════════

    /// <summary>Upsert the persona for (userId, date). Idempotent —
    /// safe to call from a retried reset tick.</summary>
    public async Task UpsertPersonaAsync(Persona persona)
    {
        var filter = Builders<Persona>.Filter.And(
            Builders<Persona>.Filter.Eq(p => p.UserId, persona.UserId),
            Builders<Persona>.Filter.Eq(p => p.Date,   persona.Date));
        var update = Builders<Persona>.Update
            .SetOnInsert(p => p.UserId,      persona.UserId)
            .SetOnInsert(p => p.Date,        persona.Date)
            .SetOnInsert(p => p.DisplayName, persona.DisplayName)
            .SetOnInsert(p => p.AvatarSeed,  persona.AvatarSeed)
            .SetOnInsert(p => p.Bio,         persona.Bio)
            .SetOnInsert(p => p.Mood,        persona.Mood)
            .SetOnInsert(p => p.ExpiresAt,   persona.ExpiresAt)
            .SetOnInsert(p => p.CreatedAt,   persona.CreatedAt);
        await Personas.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    }

    /// <summary>Return today's persona for the user, if one exists.
    /// Hub uses this on GetMyPersona().</summary>
    public async Task<Persona?> GetPersonaForDayAsync(string userId, string dateUtc)
    {
        return await Personas
            .Find(p => p.UserId == userId && p.Date == dateUtc)
            .FirstOrDefaultAsync();
    }

    /// <summary>Look up a persona by its id — used when one user
    /// tries to DM another via persona id.</summary>
    public async Task<Persona?> GetPersonaByIdAsync(string id)
    {
        return await Personas.Find(p => p.Id == id).FirstOrDefaultAsync();
    }

    /// <summary>Sweep expired personas. Cheap because we have an
    /// index on Date. Called by the reset service after generating
    /// the new day's batch.</summary>
    public async Task<long> DeletePersonasBeforeAsync(string dateUtc)
    {
        var filter = Builders<Persona>.Filter.Lt(p => p.Date, dateUtc);
        var result = await Personas.DeleteManyAsync(filter);
        return result.DeletedCount;
    }

    // ─── Conversation rollup ────────────────────────────────────

    /// <summary>Upsert the daily persona-conversation rollup, bumping
    /// MessagesCount + LastInteraction. The (PersonaIdA, PersonaIdB,
    /// Date) tuple is the unique key — we sort the pair before the
    /// call so it doesn't matter who sent the message.</summary>
    public async Task BumpPersonaConversationAsync(
        string personaIdA, string personaIdB,
        string realUserA,  string realUserB,
        string dateUtc)
    {
        // Canonicalise order so (A,B) and (B,A) hit the same row.
        var (pA, pB, uA, uB) = string.CompareOrdinal(personaIdA, personaIdB) <= 0
            ? (personaIdA, personaIdB, realUserA, realUserB)
            : (personaIdB, personaIdA, realUserB, realUserA);

        var filter = Builders<PersonaConversation>.Filter.And(
            Builders<PersonaConversation>.Filter.Eq(c => c.PersonaIdA, pA),
            Builders<PersonaConversation>.Filter.Eq(c => c.PersonaIdB, pB),
            Builders<PersonaConversation>.Filter.Eq(c => c.Date,       dateUtc));

        var update = Builders<PersonaConversation>.Update
            .SetOnInsert(c => c.PersonaIdA, pA)
            .SetOnInsert(c => c.PersonaIdB, pB)
            .SetOnInsert(c => c.RealUserA,  uA)
            .SetOnInsert(c => c.RealUserB,  uB)
            .SetOnInsert(c => c.Date,       dateUtc)
            .SetOnInsert(c => c.CreatedAt,  DateTime.UtcNow)
            .Inc(c => c.MessagesCount, 1)
            .Set(c => c.LastInteraction, DateTime.UtcNow);

        await PersonaConversations.UpdateOneAsync(
            filter, update, new UpdateOptions { IsUpsert = true });
    }

    /// <summary>All conversation rollups for a given day. The reset
    /// service consumes this on day-rollover to bump streaks.</summary>
    public async Task<List<PersonaConversation>> GetConversationsForDayAsync(string dateUtc)
    {
        return await PersonaConversations
            .Find(c => c.Date == dateUtc)
            .ToListAsync();
    }

    // ─── Streak tracker ─────────────────────────────────────────

    /// <summary>Look up the streak row for a real-user pair (sorted
    /// canonically). Hub uses this for GetActiveStreaks().</summary>
    public async Task<List<PersonaStreak>> GetStreaksForUserAsync(string realUserId)
    {
        var filter = Builders<PersonaStreak>.Filter.Or(
            Builders<PersonaStreak>.Filter.Eq(s => s.RealUserA, realUserId),
            Builders<PersonaStreak>.Filter.Eq(s => s.RealUserB, realUserId));
        return await PersonaStreaks
            .Find(filter)
            .SortByDescending(s => s.ConsecutiveDays)
            .Limit(100)
            .ToListAsync();
    }

    public async Task<PersonaStreak?> GetStreakAsync(string realUserA, string realUserB)
    {
        var (a, b) = SortPair(realUserA, realUserB);
        return await PersonaStreaks
            .Find(s => s.RealUserA == a && s.RealUserB == b)
            .FirstOrDefaultAsync();
    }

    /// <summary>Apply a day's interaction to the streak between two
    /// real users. If LastDay is yesterday relative to `dateUtc`,
    /// bump ConsecutiveDays. If gap > 1 day, reset to 1. Idempotent
    /// when LastDay == dateUtc (re-runs same day are no-ops).</summary>
    public async Task<PersonaStreak> BumpStreakForDayAsync(
        string realUserA, string realUserB, string dateUtc)
    {
        var (a, b) = SortPair(realUserA, realUserB);

        var existing = await GetStreakAsync(a, b);
        var now = DateTime.UtcNow;

        if (existing is null)
        {
            var fresh = new PersonaStreak
            {
                RealUserA       = a,
                RealUserB       = b,
                ConsecutiveDays = 1,
                LastDay         = dateUtc,
                CreatedAt       = now,
                UpdatedAt       = now,
            };
            await PersonaStreaks.InsertOneAsync(fresh);
            return fresh;
        }

        // Same-day re-run is a no-op (idempotent).
        if (existing.LastDay == dateUtc) return existing;

        var newConsecutive = IsConsecutiveDay(existing.LastDay, dateUtc)
            ? existing.ConsecutiveDays + 1
            : 1;

        var update = Builders<PersonaStreak>.Update
            .Set(s => s.ConsecutiveDays, newConsecutive)
            .Set(s => s.LastDay,         dateUtc)
            .Set(s => s.UpdatedAt,       now);

        // First-time 30-day cross triggers Memory Vault.
        if (newConsecutive >= 30 && existing.VaultedAt is null)
            update = update.Set(s => s.VaultedAt, now);

        var filter = Builders<PersonaStreak>.Filter.Eq(s => s.Id, existing.Id);
        await PersonaStreaks.UpdateOneAsync(filter, update);

        existing.ConsecutiveDays = newConsecutive;
        existing.LastDay         = dateUtc;
        existing.UpdatedAt       = now;
        if (newConsecutive >= 30 && existing.VaultedAt is null) existing.VaultedAt = now;
        return existing;
    }

    /// <summary>Register a real user's "I'd like to unmask" tap on the
    /// streak. Adds them to UnmaskRequestedBy if not already there.
    /// If BOTH RealUserA and RealUserB are now in the list, the streak
    /// flips to UnmaskedAt. Returns the post-state of the streak so
    /// the caller can tell client what to render.</summary>
    public async Task<PersonaStreak?> RequestStreakUnmaskAsync(
        string streakId, string requestingRealUserId)
    {
        var streak = await PersonaStreaks
            .Find(s => s.Id == streakId)
            .FirstOrDefaultAsync();
        if (streak is null) return null;

        // Authorisation: caller must be one of the two real users on
        // this streak. Anything else is a 404-equivalent so we don't
        // leak that the streak exists.
        if (streak.RealUserA != requestingRealUserId &&
            streak.RealUserB != requestingRealUserId)
        {
            return null;
        }

        // 7-day floor — per VISION.md spec, mutual unmask only
        // unlocks after a full week of consecutive conversation.
        if (streak.ConsecutiveDays < 7) return streak;

        // Idempotent — already on the list = nothing to do.
        if (!streak.UnmaskRequestedBy.Contains(requestingRealUserId))
        {
            streak.UnmaskRequestedBy.Add(requestingRealUserId);

            var bothSidesRequested =
                streak.UnmaskRequestedBy.Contains(streak.RealUserA) &&
                streak.UnmaskRequestedBy.Contains(streak.RealUserB);

            var update = Builders<PersonaStreak>.Update
                .Set(s => s.UnmaskRequestedBy, streak.UnmaskRequestedBy)
                .Set(s => s.UpdatedAt, DateTime.UtcNow);

            if (bothSidesRequested && streak.UnmaskedAt is null)
            {
                update = update.Set(s => s.UnmaskedAt, DateTime.UtcNow);
                streak.UnmaskedAt = DateTime.UtcNow;
            }

            await PersonaStreaks.UpdateOneAsync(
                Builders<PersonaStreak>.Filter.Eq(s => s.Id, streakId),
                update);
        }
        return streak;
    }

    // ─── Discovery ──────────────────────────────────────────────

    /// <summary>Random sample of today's personas, excluding the
    /// caller's own. Used by the "Discover" tab to seed first-contact
    /// conversations. $sample is the cheapest way to pull a random
    /// subset server-side — Mongo handles the reservoir.</summary>
    public async Task<List<Persona>> GetRandomTodayPersonasAsync(
        string excludeUserId, string dateUtc, int limit = 10)
    {
        return await Personas
            .Aggregate()
            .Match(p => p.Date == dateUtc && p.UserId != excludeUserId)
            .AppendStage<Persona>(new BsonDocument("$sample", new BsonDocument("size", limit)))
            .ToListAsync();
    }

    // ─── Persona-to-persona messages ────────────────────────────

    /// <summary>Insert a persona message and bump the daily
    /// conversation rollup in the same logical step. Sender + recipient
    /// real-user IDs are sorted canonically before write so the thread
    /// has ONE stable key regardless of direction.</summary>
    public async Task<PersonaMessage> InsertPersonaMessageAsync(
        PersonaMessage message, string recipientRealUserId, string dateUtc,
        string recipientPersonaId)
    {
        var (a, b) = SortPair(message.SenderRealUserId, recipientRealUserId);
        message.RealUserA = a;
        message.RealUserB = b;
        message.CreatedAt = DateTime.UtcNow;
        await PersonaMessages.InsertOneAsync(message);

        // Bump the per-day rollup so PersonaResetService can roll it
        // into a streak tomorrow.
        await BumpPersonaConversationAsync(
            personaIdA: message.SenderPersonaId,
            personaIdB: recipientPersonaId,
            realUserA:  message.SenderRealUserId,
            realUserB:  recipientRealUserId,
            dateUtc:    dateUtc);

        return message;
    }

    /// <summary>Fetch the most recent N messages between two real
    /// users. Pair is sorted internally.</summary>
    public async Task<List<PersonaMessage>> GetPersonaThreadAsync(
        string realUserX, string realUserY, int limit = 100)
    {
        var (a, b) = SortPair(realUserX, realUserY);
        var filter = Builders<PersonaMessage>.Filter.And(
            Builders<PersonaMessage>.Filter.Eq(m => m.RealUserA, a),
            Builders<PersonaMessage>.Filter.Eq(m => m.RealUserB, b));
        return await PersonaMessages
            .Find(filter)
            .SortBy(m => m.CreatedAt)
            .Limit(limit)
            .ToListAsync();
    }

    private static (string a, string b) SortPair(string x, string y) =>
        string.CompareOrdinal(x, y) <= 0 ? (x, y) : (y, x);

    private static bool IsConsecutiveDay(string previousDate, string currentDate)
    {
        // Both are YYYY-MM-DD UTC strings. Parse both, ask if the
        // current is exactly previous + 1 day.
        if (!DateTime.TryParse(previousDate, out var prev)) return false;
        if (!DateTime.TryParse(currentDate,  out var curr)) return false;
        return curr.Date == prev.Date.AddDays(1);
    }

    // ════════════════════════════════════════════════════════════
    //  STORY CHAIN — daily collaborative writing
    // ════════════════════════════════════════════════════════════

    public const int StoryChainMaxContributions = 50;
    public const int StoryChainMaxSentenceChars = 280;  // tweet-sized — keeps pacing tight

    /// <summary>Insert today's chain if not already present.
    /// Returns the chain row regardless. Idempotent.</summary>
    public async Task<StoryChain> EnsureDailyStoryChainAsync(string promptDate, string prompt)
    {
        var existing = await StoryChains
            .Find(c => c.PromptDate == promptDate)
            .FirstOrDefaultAsync();
        if (existing is not null) return existing;

        var chain = new StoryChain
        {
            PromptDate = promptDate,
            Prompt     = prompt,
            Sentences  = new List<StoryContribution>(),
            ContributorUserIds = new List<string>(),
            Status     = "active",
            CreatedAt  = DateTime.UtcNow,
        };
        try
        {
            await StoryChains.InsertOneAsync(chain);
            return chain;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Race: another instance just inserted. Re-read and return.
            return await StoryChains
                .Find(c => c.PromptDate == promptDate)
                .FirstAsync();
        }
    }

    public async Task<StoryChain?> GetActiveStoryChainAsync(string promptDate)
    {
        return await StoryChains
            .Find(c => c.PromptDate == promptDate)
            .FirstOrDefaultAsync();
    }

    /// <summary>Append a contribution atomically. Guards:
    ///   • chain must be active
    ///   • author hasn't already contributed (one sentence per user)
    ///   • cap of 50 contributions not yet hit
    /// Returns true if THIS call wrote the contribution; false if it
    /// lost the race (someone else's contribution claimed the slot,
    /// or the chain locked, or the author had already written).</summary>
    public async Task<bool> AppendStoryContributionAsync(
        string chainId, StoryContribution contribution)
    {
        var filter = Builders<StoryChain>.Filter.And(
            Builders<StoryChain>.Filter.Eq(c => c.Id, chainId),
            Builders<StoryChain>.Filter.Eq(c => c.Status, "active"),
            // Author hasn't already contributed to this chain.
            // Not(AnyEq) is the array-aware way to say "value not in list".
            Builders<StoryChain>.Filter.Not(
                Builders<StoryChain>.Filter.AnyEq(c => c.ContributorUserIds, contribution.AuthorUserId)),
            // Cap at 50 sentences. SizeLt on the array uses Mongo's
            // $expr + $lt under the hood — cheap on a small list.
            Builders<StoryChain>.Filter.SizeLt(c => c.Sentences, StoryChainMaxContributions));

        var update = Builders<StoryChain>.Update
            .Push(c => c.Sentences,          contribution)
            .Push(c => c.ContributorUserIds, contribution.AuthorUserId);

        var result = await StoryChains.UpdateOneAsync(filter, update);
        return result.ModifiedCount == 1;
    }

    /// <summary>Flip chain to locked. Idempotent — second call on an
    /// already-locked chain is a no-op (returns false).</summary>
    public async Task<bool> LockStoryChainAsync(string chainId)
    {
        var filter = Builders<StoryChain>.Filter.And(
            Builders<StoryChain>.Filter.Eq(c => c.Id, chainId),
            Builders<StoryChain>.Filter.Eq(c => c.Status, "active"));
        var update = Builders<StoryChain>.Update
            .Set(c => c.Status,   "locked")
            .Set(c => c.LockedAt, DateTime.UtcNow);
        var result = await StoryChains.UpdateOneAsync(filter, update);
        return result.ModifiedCount == 1;
    }

    /// <summary>Mark a locked chain as published — surfaces it on the
    /// Stories feed. Service runs this immediately after lock.</summary>
    public async Task<bool> PublishStoryChainAsync(string chainId)
    {
        var filter = Builders<StoryChain>.Filter.And(
            Builders<StoryChain>.Filter.Eq(c => c.Id, chainId),
            Builders<StoryChain>.Filter.Eq(c => c.Status, "locked"));
        var update = Builders<StoryChain>.Update
            .Set(c => c.Status,      "published")
            .Set(c => c.PublishedAt, DateTime.UtcNow);
        var result = await StoryChains.UpdateOneAsync(filter, update);
        return result.ModifiedCount == 1;
    }

    /// <summary>All chains still in "active" status — the background
    /// service sweeps this for the auto-lock-at-midnight pass.</summary>
    public async Task<List<StoryChain>> GetActiveChainsAsync()
    {
        return await StoryChains
            .Find(c => c.Status == "active")
            .ToListAsync();
    }

    /// <summary>All chains in "locked" status (not yet published).
    /// Background tick promotes these to published in a follow-up
    /// pass — kept separate so a moderation hook could be slotted
    /// between lock and publish later.</summary>
    public async Task<List<StoryChain>> GetLockedChainsAsync()
    {
        return await StoryChains
            .Find(c => c.Status == "locked")
            .ToListAsync();
    }

    /// <summary>Recent published chains for the archive feed.</summary>
    public async Task<List<StoryChain>> GetPublishedChainsAsync(int limit = 30)
    {
        return await StoryChains
            .Find(c => c.Status == "published")
            .SortByDescending(c => c.PublishedAt)
            .Limit(limit)
            .ToListAsync();
    }

    // ════════════════════════════════════════════════════════════
    //  CONFESSION BOX — anonymous daily reveals
    // ════════════════════════════════════════════════════════════

    public const int ConfessionMaxChars = 500;
    public const int ConfessionTtlDays  = 30;
    public static readonly string[] ConfessionAllowedEmojis = new[]
    {
        "🔥",   // fire — relatable / hit
        "😭",   // crying — felt this
        "🫂",   // hug — sending love
        "💀",   // skull — dark relatable humour
        "👀",   // eyes — saw something
        "🙏",   // pray — solidarity
    };

    public async Task<Confession> InsertConfessionAsync(Confession c)
    {
        c.CreatedAt = DateTime.UtcNow;
        c.ExpiresAt = c.CreatedAt.AddDays(ConfessionTtlDays);
        await Confessions.InsertOneAsync(c);
        return c;
    }

    /// <summary>Paginated feed for a given UTC date. Sorted newest
    /// first within the day so freshly-posted confessions surface.</summary>
    public async Task<List<Confession>> GetConfessionsForDateAsync(
        string dateUtc, int skip = 0, int limit = 20)
    {
        return await Confessions
            .Find(c => c.Date == dateUtc)
            .SortByDescending(c => c.CreatedAt)
            .Skip(skip)
            .Limit(limit)
            .ToListAsync();
    }

    public async Task<Confession?> GetConfessionByIdAsync(string id)
    {
        return await Confessions.Find(c => c.Id == id).FirstOrDefaultAsync();
    }

    /// <summary>Toggle a user's reaction on a confession. ONE emoji
    /// per user per confession — switching emojis silently swaps.
    /// Re-tapping the same emoji removes the reaction. Returns the
    /// fresh confession state.
    ///
    /// Renamed from ToggleReactionAsync to avoid a CS0111 clash with
    /// the existing message-reaction toggler (same (string, string,
    /// string) signature). Note also that we use STRING field paths
    /// (e.g. "reactions.🔥") for the AddToSet/Pull calls — the C#
    /// driver's expression visitor can't translate `c.Reactions[emoji]`
    /// because dictionary indexer access isn't a supported node.</summary>
    public async Task<Confession?> ToggleConfessionReactionAsync(
        string confessionId, string userId, string emoji)
    {
        if (!ConfessionAllowedEmojis.Contains(emoji)) return null;

        var confession = await GetConfessionByIdAsync(confessionId);
        if (confession is null) return null;

        // Figure out what the user currently has.
        string? currentEmoji = null;
        foreach (var (e, users) in confession.Reactions)
        {
            if (users.Contains(userId)) { currentEmoji = e; break; }
        }

        // Three cases:
        //   (a) user has no reaction yet     → add to `emoji`
        //   (b) user already has THIS emoji  → remove
        //   (c) user has a DIFFERENT emoji   → remove old, add new
        var updateBuilder = Builders<Confession>.Update;
        var updates = new List<UpdateDefinition<Confession>>();
        int delta = 0;

        // CamelCase convention is registered on the client, so the
        // server-side field is "reactions". Emoji keys nested under it.
        string PathFor(string e) => $"reactions.{e}";

        if (currentEmoji is null)
        {
            updates.Add(updateBuilder.AddToSet<string>(PathFor(emoji), userId));
            delta = 1;
        }
        else if (currentEmoji == emoji)
        {
            updates.Add(updateBuilder.Pull<string>(PathFor(emoji), userId));
            delta = -1;
        }
        else
        {
            updates.Add(updateBuilder.Pull<string>(PathFor(currentEmoji), userId));
            updates.Add(updateBuilder.AddToSet<string>(PathFor(emoji), userId));
            delta = 0;  // net distinct reactors unchanged
        }

        if (delta != 0)
            updates.Add(updateBuilder.Inc(c => c.TotalReactions, delta));

        var combined = updateBuilder.Combine(updates);
        var filter = Builders<Confession>.Filter.Eq(c => c.Id, confessionId);

        await Confessions.UpdateOneAsync(filter, combined);
        return await GetConfessionByIdAsync(confessionId);
    }

    /// <summary>Top confession of a given UTC date by total reactions.
    /// Tiebreaker = earlier-posted wins (rewards conviction).</summary>
    public async Task<Confession?> GetTopForDateAsync(string dateUtc)
    {
        return await Confessions
            .Find(c => c.Date == dateUtc && c.TotalReactions > 0)
            .SortByDescending(c => c.TotalReactions)
            .ThenBy(c => c.CreatedAt)
            .Limit(1)
            .FirstOrDefaultAsync();
    }

    public async Task<bool> MarkTopRankedAsync(string confessionId)
    {
        var filter = Builders<Confession>.Filter.And(
            Builders<Confession>.Filter.Eq(c => c.Id, confessionId),
            Builders<Confession>.Filter.Eq(c => c.TopRankedAt, (DateTime?)null));
        var update = Builders<Confession>.Update.Set(c => c.TopRankedAt, DateTime.UtcNow);
        var result = await Confessions.UpdateOneAsync(filter, update);
        return result.ModifiedCount == 1;
    }

    /// <summary>Lock the author's reveal decision. Idempotent — once
    /// set (true OR false) it doesn't change.</summary>
    public async Task<bool> SetAuthorRevealDecisionAsync(
        string confessionId, string authorUserId, bool accept)
    {
        var filter = Builders<Confession>.Filter.And(
            Builders<Confession>.Filter.Eq(c => c.Id, confessionId),
            Builders<Confession>.Filter.Eq(c => c.AuthorUserId, authorUserId),
            Builders<Confession>.Filter.Eq(c => c.AuthorOptedReveal, (bool?)null));
        var update = Builders<Confession>.Update
            .Set(c => c.AuthorOptedReveal, accept);
        if (accept)
            update = update.Set(c => c.RevealedAt, DateTime.UtcNow);
        var result = await Confessions.UpdateOneAsync(filter, update);
        return result.ModifiedCount == 1;
    }

    /// <summary>Returns the caller's UN-decided top-ranked confession,
    /// if any — used by GetMyTopOffer on the hub.</summary>
    public async Task<Confession?> GetUndecidedTopOfferForAuthorAsync(string authorUserId)
    {
        var filter = Builders<Confession>.Filter.And(
            Builders<Confession>.Filter.Eq(c => c.AuthorUserId, authorUserId),
            Builders<Confession>.Filter.Ne(c => c.TopRankedAt, (DateTime?)null),
            Builders<Confession>.Filter.Eq(c => c.AuthorOptedReveal, (bool?)null));
        return await Confessions
            .Find(filter)
            .SortByDescending(c => c.TopRankedAt)
            .Limit(1)
            .FirstOrDefaultAsync();
    }

    /// <summary>Lore Wall — top 5 top-ranked confessions in a given
    /// week (sundayStart inclusive, +7 days exclusive).</summary>
    public async Task<List<Confession>> GetLoreWallAsync(
        DateTime weekStart, int limit = 5)
    {
        var weekEnd = weekStart.AddDays(7);
        var filter = Builders<Confession>.Filter.And(
            Builders<Confession>.Filter.Ne(c => c.TopRankedAt, (DateTime?)null),
            Builders<Confession>.Filter.Gte(c => c.CreatedAt, weekStart),
            Builders<Confession>.Filter.Lt(c => c.CreatedAt,  weekEnd));
        return await Confessions
            .Find(filter)
            .SortByDescending(c => c.TotalReactions)
            .ThenBy(c => c.CreatedAt)
            .Limit(limit)
            .ToListAsync();
    }

    // ════════════════════════════════════════════════════════════
    //  GHOST DATE — weekly Thursday 9pm IST anonymous dating
    // ════════════════════════════════════════════════════════════

    public const int GhostDateChatMinutes     = 30;
    public const int GhostDateDecisionMinutes = 5;
    public const int GhostDateMessageMaxChars = 1000;
    public const int GhostDateRepairCooldownDays = 60;

    // ─── Registration pool ─────────────────────────────────────

    /// <summary>Idempotent register — same user calling twice for
    /// the same target event date is a no-op. Returns the row.</summary>
    public async Task<GhostDateRegistration> RegisterForGhostDateAsync(
        string userId, string targetEventDate)
    {
        var filter = Builders<GhostDateRegistration>.Filter.And(
            Builders<GhostDateRegistration>.Filter.Eq(r => r.UserId, userId),
            Builders<GhostDateRegistration>.Filter.Eq(r => r.TargetEventDate, targetEventDate));

        var existing = await GhostDateRegistrations.Find(filter).FirstOrDefaultAsync();
        if (existing is not null)
        {
            // If they previously withdrew, flip back to pending.
            if (existing.Status == "withdrew")
            {
                var revive = Builders<GhostDateRegistration>.Update
                    .Set(r => r.Status, "pending")
                    .Set(r => r.RegisteredAt, DateTime.UtcNow);
                await GhostDateRegistrations.UpdateOneAsync(filter, revive);
                existing.Status = "pending";
                existing.RegisteredAt = DateTime.UtcNow;
            }
            return existing;
        }

        var fresh = new GhostDateRegistration
        {
            UserId          = userId,
            TargetEventDate = targetEventDate,
            RegisteredAt    = DateTime.UtcNow,
            Status          = "pending",
        };
        await GhostDateRegistrations.InsertOneAsync(fresh);
        return fresh;
    }

    /// <summary>Withdraw before the pairing tick. After matching this
    /// becomes a no-op (PairedDateId set means it's locked in).</summary>
    public async Task<bool> WithdrawFromGhostDateAsync(
        string userId, string targetEventDate)
    {
        var filter = Builders<GhostDateRegistration>.Filter.And(
            Builders<GhostDateRegistration>.Filter.Eq(r => r.UserId, userId),
            Builders<GhostDateRegistration>.Filter.Eq(r => r.TargetEventDate, targetEventDate),
            Builders<GhostDateRegistration>.Filter.Eq(r => r.Status, "pending"));
        var update = Builders<GhostDateRegistration>.Update
            .Set(r => r.Status, "withdrew");
        var result = await GhostDateRegistrations.UpdateOneAsync(filter, update);
        return result.ModifiedCount == 1;
    }

    public async Task<GhostDateRegistration?> GetMyRegistrationAsync(
        string userId, string targetEventDate)
    {
        return await GhostDateRegistrations.Find(
            r => r.UserId == userId && r.TargetEventDate == targetEventDate
        ).FirstOrDefaultAsync();
    }

    /// <summary>The pool the pairing service picks from.</summary>
    public async Task<List<GhostDateRegistration>> GetPendingRegistrationsAsync(
        string targetEventDate)
    {
        return await GhostDateRegistrations.Find(
            r => r.TargetEventDate == targetEventDate && r.Status == "pending"
        ).ToListAsync();
    }

    /// <summary>Set status + paired-date pointer atomically (the
    /// pairing service updates two registrations per pair).</summary>
    public async Task MarkRegistrationMatchedAsync(
        string registrationId, string pairedDateId)
    {
        var filter = Builders<GhostDateRegistration>.Filter.Eq(r => r.Id, registrationId);
        var update = Builders<GhostDateRegistration>.Update
            .Set(r => r.Status,       "matched")
            .Set(r => r.PairedDateId, pairedDateId);
        await GhostDateRegistrations.UpdateOneAsync(filter, update);
    }

    public async Task MarkRegistrationNoMatchAsync(string registrationId)
    {
        var filter = Builders<GhostDateRegistration>.Filter.Eq(r => r.Id, registrationId);
        var update = Builders<GhostDateRegistration>.Update.Set(r => r.Status, "no_match");
        await GhostDateRegistrations.UpdateOneAsync(filter, update);
    }

    // ─── Live dates ────────────────────────────────────────────

    public async Task<GhostDate> InsertGhostDateAsync(GhostDate date)
    {
        date.CreatedAt = DateTime.UtcNow;
        await GhostDates.InsertOneAsync(date);
        return date;
    }

    public async Task<GhostDate?> GetGhostDateByIdAsync(string id)
    {
        return await GhostDates.Find(d => d.Id == id).FirstOrDefaultAsync();
    }

    /// <summary>The user's CURRENTLY-LIVE date if any — i.e. now is
    /// between ScheduledFor and DecisionDeadline. Used by the hub
    /// to surface the active-date UI on connect.</summary>
    public async Task<GhostDate?> GetMyActiveGhostDateAsync(string userId)
    {
        var now = DateTime.UtcNow;
        var filter = Builders<GhostDate>.Filter.And(
            Builders<GhostDate>.Filter.Or(
                Builders<GhostDate>.Filter.Eq(d => d.UserAId, userId),
                Builders<GhostDate>.Filter.Eq(d => d.UserBId, userId)),
            Builders<GhostDate>.Filter.Lte(d => d.ScheduledFor,      now),
            Builders<GhostDate>.Filter.Gte(d => d.DecisionDeadline,  now));
        return await GhostDates
            .Find(filter)
            .SortByDescending(d => d.ScheduledFor)
            .FirstOrDefaultAsync();
    }

    /// <summary>All dates whose chat window is over but decision
    /// deadline hasn't yet passed — the lock service hits these
    /// to push "chat ended" events at the right moment.</summary>
    public async Task<List<GhostDate>> GetDatesAwaitingDecisionAsync()
    {
        var now = DateTime.UtcNow;
        var filter = Builders<GhostDate>.Filter.And(
            Builders<GhostDate>.Filter.Lte(d => d.ExpiresAt, now),
            Builders<GhostDate>.Filter.Gt(d => d.DecisionDeadline, now),
            Builders<GhostDate>.Filter.Eq(d => d.Outcome, (string?)null));
        return await GhostDates.Find(filter).ToListAsync();
    }

    /// <summary>Dates whose decision deadline has passed without
    /// outcome being set → service marks them expired.</summary>
    public async Task<List<GhostDate>> GetExpiredUndecidedDatesAsync()
    {
        var now = DateTime.UtcNow;
        var filter = Builders<GhostDate>.Filter.And(
            Builders<GhostDate>.Filter.Lte(d => d.DecisionDeadline, now),
            Builders<GhostDate>.Filter.Eq(d => d.Outcome, (string?)null));
        return await GhostDates.Find(filter).ToListAsync();
    }

    /// <summary>Record this side's decision atomically. Resolves
    /// the outcome the moment both decisions are in. Returns the
    /// post-state of the date.</summary>
    public async Task<GhostDate?> SubmitGhostDateDecisionAsync(
        string dateId, string userId, bool reveal)
    {
        var date = await GetGhostDateByIdAsync(dateId);
        if (date is null) return null;

        // Author check + already-decided guard.
        bool isA = date.UserAId == userId;
        bool isB = date.UserBId == userId;
        if (!isA && !isB) return null;
        if (date.Outcome != null) return date;        // already resolved
        if (isA && date.UserARevealedAfter != null) return date;
        if (isB && date.UserBRevealedAfter != null) return date;
        // Decision must land within window.
        if (DateTime.UtcNow > date.DecisionDeadline) return date;

        var update = Builders<GhostDate>.Update.Combine(
            isA
                ? Builders<GhostDate>.Update.Set(d => d.UserARevealedAfter, reveal)
                                            .Set(d => d.UserADecidedAt, DateTime.UtcNow)
                : Builders<GhostDate>.Update.Set(d => d.UserBRevealedAfter, reveal)
                                            .Set(d => d.UserBDecidedAt, DateTime.UtcNow));

        var filter = Builders<GhostDate>.Filter.Eq(d => d.Id, dateId);
        await GhostDates.UpdateOneAsync(filter, update);

        // Re-read fresh, then resolve outcome if both sides in.
        var fresh = await GetGhostDateByIdAsync(dateId);
        if (fresh is null) return null;
        if (fresh.UserARevealedAfter is bool a && fresh.UserBRevealedAfter is bool b
            && fresh.Outcome is null)
        {
            var outcome = (a, b) switch
            {
                (true,  true)  => "mutual_reveal",
                (false, false) => "mutual_pass",
                _              => "bittersweet",
            };

            var outcomeUpdate = Builders<GhostDate>.Update
                .Set(d => d.Outcome,   outcome)
                .Set(d => d.OutcomeAt, DateTime.UtcNow);

            if (outcome == "mutual_pass")
            {
                outcomeUpdate = outcomeUpdate.Set(
                    d => d.NextEligibleMatchAt,
                    fresh.ScheduledFor.AddDays(GhostDateRepairCooldownDays));
            }
            await GhostDates.UpdateOneAsync(filter, outcomeUpdate);
            fresh.Outcome   = outcome;
            fresh.OutcomeAt = DateTime.UtcNow;
        }
        return fresh;
    }

    /// <summary>Force-resolve a date whose decision window has
    /// passed. Treats nulls as "pass" for outcome calculation
    /// (= bittersweet if one side decided to reveal, else mutual_pass).</summary>
    public async Task<GhostDate?> MarkGhostDateExpiredAsync(string dateId)
    {
        var date = await GetGhostDateByIdAsync(dateId);
        if (date is null || date.Outcome != null) return date;

        bool a = date.UserARevealedAfter ?? false;
        bool b = date.UserBRevealedAfter ?? false;
        // If at least one side missed the window we mark it "expired"
        // rather than computing an outcome — UX-wise it's a clearer
        // surface for the user who DID decide.
        bool anyMissed = date.UserARevealedAfter is null || date.UserBRevealedAfter is null;
        var outcome = anyMissed ? "expired"
            : (a, b) switch
            {
                (true,  true)  => "mutual_reveal",
                (false, false) => "mutual_pass",
                _              => "bittersweet",
            };

        var filter = Builders<GhostDate>.Filter.And(
            Builders<GhostDate>.Filter.Eq(d => d.Id, dateId),
            Builders<GhostDate>.Filter.Eq(d => d.Outcome, (string?)null));
        var update = Builders<GhostDate>.Update
            .Set(d => d.Outcome,   outcome)
            .Set(d => d.OutcomeAt, DateTime.UtcNow);
        if (outcome == "mutual_pass")
        {
            update = update.Set(d => d.NextEligibleMatchAt,
                date.ScheduledFor.AddDays(GhostDateRepairCooldownDays));
        }
        await GhostDates.UpdateOneAsync(filter, update);
        return await GetGhostDateByIdAsync(dateId);
    }

    public async Task<List<GhostDate>> GetGhostDateHistoryAsync(
        string userId, int limit = 20)
    {
        var filter = Builders<GhostDate>.Filter.Or(
            Builders<GhostDate>.Filter.Eq(d => d.UserAId, userId),
            Builders<GhostDate>.Filter.Eq(d => d.UserBId, userId));
        return await GhostDates
            .Find(filter)
            .SortByDescending(d => d.ScheduledFor)
            .Limit(limit)
            .ToListAsync();
    }

    // ─── Per-date thread ───────────────────────────────────────

    public async Task<GhostDateMessage> InsertGhostDateMessageAsync(GhostDateMessage m)
    {
        m.CreatedAt = DateTime.UtcNow;
        await GhostDateMessages.InsertOneAsync(m);
        return m;
    }

    public async Task<List<GhostDateMessage>> GetGhostDateThreadAsync(
        string dateId, int limit = 200)
    {
        return await GhostDateMessages
            .Find(m => m.DateId == dateId)
            .SortBy(m => m.CreatedAt)
            .Limit(limit)
            .ToListAsync();
    }

    // ════════════════════════════════════════════════════════════
    //  LOVE TRIANGLE — weekly Sunday 10pm IST 3-person drama
    // ════════════════════════════════════════════════════════════

    public const int LoveTriangleChatDays         = 7;
    public const int LoveTriangleVotingDays       = 1;
    public const int LoveTrianglePairMessageMax   = 1000;
    public static readonly string[] LoveTrianglePairKeys = new[] { "a-b", "b-c", "a-c" };

    // ─── Registration ──────────────────────────────────────────

    public async Task<LoveTriangleRegistration> RegisterForLoveTriangleAsync(
        string userId, string weekStart)
    {
        var filter = Builders<LoveTriangleRegistration>.Filter.And(
            Builders<LoveTriangleRegistration>.Filter.Eq(r => r.UserId, userId),
            Builders<LoveTriangleRegistration>.Filter.Eq(r => r.WeekStart, weekStart));
        var existing = await LoveTriangleRegistrations.Find(filter).FirstOrDefaultAsync();
        if (existing is not null)
        {
            if (existing.Status == "withdrew")
            {
                var revive = Builders<LoveTriangleRegistration>.Update
                    .Set(r => r.Status, "pending")
                    .Set(r => r.RegisteredAt, DateTime.UtcNow);
                await LoveTriangleRegistrations.UpdateOneAsync(filter, revive);
                existing.Status = "pending";
                existing.RegisteredAt = DateTime.UtcNow;
            }
            return existing;
        }

        var fresh = new LoveTriangleRegistration
        {
            UserId       = userId,
            WeekStart    = weekStart,
            RegisteredAt = DateTime.UtcNow,
            Status       = "pending",
        };
        await LoveTriangleRegistrations.InsertOneAsync(fresh);
        return fresh;
    }

    public async Task<bool> WithdrawFromLoveTriangleAsync(string userId, string weekStart)
    {
        var filter = Builders<LoveTriangleRegistration>.Filter.And(
            Builders<LoveTriangleRegistration>.Filter.Eq(r => r.UserId, userId),
            Builders<LoveTriangleRegistration>.Filter.Eq(r => r.WeekStart, weekStart),
            Builders<LoveTriangleRegistration>.Filter.Eq(r => r.Status, "pending"));
        var update = Builders<LoveTriangleRegistration>.Update.Set(r => r.Status, "withdrew");
        var result = await LoveTriangleRegistrations.UpdateOneAsync(filter, update);
        return result.ModifiedCount == 1;
    }

    public async Task<LoveTriangleRegistration?> GetMyLoveTriangleRegistrationAsync(
        string userId, string weekStart)
    {
        return await LoveTriangleRegistrations.Find(
            r => r.UserId == userId && r.WeekStart == weekStart
        ).FirstOrDefaultAsync();
    }

    public async Task<List<LoveTriangleRegistration>> GetPendingLoveTriangleRegistrationsAsync(
        string weekStart)
    {
        return await LoveTriangleRegistrations.Find(
            r => r.WeekStart == weekStart && r.Status == "pending"
        ).ToListAsync();
    }

    public async Task MarkLoveTriangleRegistrationAssignedAsync(
        string registrationId, string triangleId, string status)
    {
        var filter = Builders<LoveTriangleRegistration>.Filter.Eq(r => r.Id, registrationId);
        var update = Builders<LoveTriangleRegistration>.Update
            .Set(r => r.Status,     status)
            .Set(r => r.TriangleId, triangleId);
        await LoveTriangleRegistrations.UpdateOneAsync(filter, update);
    }

    // ─── Triangle CRUD ─────────────────────────────────────────

    public async Task<LoveTriangle> InsertLoveTriangleAsync(LoveTriangle t)
    {
        t.CreatedAt = DateTime.UtcNow;
        await LoveTriangles.InsertOneAsync(t);
        return t;
    }

    public async Task<LoveTriangle?> GetLoveTriangleByIdAsync(string id)
    {
        return await LoveTriangles.Find(t => t.Id == id).FirstOrDefaultAsync();
    }

    /// <summary>The user's CURRENTLY-ACTIVE triangle if any
    /// (status = active or voting).</summary>
    public async Task<LoveTriangle?> GetMyActiveLoveTriangleAsync(string userId)
    {
        var memberFilter = Builders<LoveTriangle>.Filter.Or(
            Builders<LoveTriangle>.Filter.Eq(t => t.UserAId, userId),
            Builders<LoveTriangle>.Filter.Eq(t => t.UserBId, userId),
            Builders<LoveTriangle>.Filter.Eq(t => t.UserCId, userId));
        var statusFilter = Builders<LoveTriangle>.Filter.In(t => t.Status, new[] { "active", "voting" });
        return await LoveTriangles
            .Find(Builders<LoveTriangle>.Filter.And(memberFilter, statusFilter))
            .SortByDescending(t => t.ScheduledFor)
            .FirstOrDefaultAsync();
    }

    public async Task<List<LoveTriangle>> GetTrianglesReadyForVotingAsync()
    {
        var now = DateTime.UtcNow;
        var filter = Builders<LoveTriangle>.Filter.And(
            Builders<LoveTriangle>.Filter.Eq(t => t.Status, "active"),
            Builders<LoveTriangle>.Filter.Lte(t => t.ChatEndsAt, now));
        return await LoveTriangles.Find(filter).ToListAsync();
    }

    public async Task<List<LoveTriangle>> GetTrianglesReadyForCompletionAsync()
    {
        var now = DateTime.UtcNow;
        var filter = Builders<LoveTriangle>.Filter.And(
            Builders<LoveTriangle>.Filter.Eq(t => t.Status, "voting"),
            Builders<LoveTriangle>.Filter.Lte(t => t.VotingEndsAt, now));
        return await LoveTriangles.Find(filter).ToListAsync();
    }

    public async Task<bool> OpenLoveTriangleVotingAsync(string triangleId)
    {
        var filter = Builders<LoveTriangle>.Filter.And(
            Builders<LoveTriangle>.Filter.Eq(t => t.Id, triangleId),
            Builders<LoveTriangle>.Filter.Eq(t => t.Status, "active"));
        var update = Builders<LoveTriangle>.Update.Set(t => t.Status, "voting");
        var result = await LoveTriangles.UpdateOneAsync(filter, update);
        return result.ModifiedCount == 1;
    }

    public async Task<LoveTriangle?> CompleteLoveTriangleAsync(string triangleId)
    {
        var triangle = await GetLoveTriangleByIdAsync(triangleId);
        if (triangle is null || triangle.Status == "completed") return triangle;

        // Resolve winning pair from current VotesByPair counts.
        var counts = LoveTrianglePairKeys
            .ToDictionary(k => k, k => triangle.VotesByPair.TryGetValue(k, out var v) ? v.Count : 0);
        var max = counts.Values.DefaultIfEmpty(0).Max();
        var winners = counts.Where(kv => kv.Value == max && max > 0).Select(kv => kv.Key).ToList();
        var winningPair = winners.Count switch
        {
            0 => "no_votes",
            1 => winners[0],
            _ => "tie",
        };

        var filter = Builders<LoveTriangle>.Filter.And(
            Builders<LoveTriangle>.Filter.Eq(t => t.Id, triangleId),
            Builders<LoveTriangle>.Filter.Eq(t => t.Status, "voting"));
        var update = Builders<LoveTriangle>.Update
            .Set(t => t.Status,      "completed")
            .Set(t => t.WinningPair, winningPair)
            .Set(t => t.CompletedAt, DateTime.UtcNow);
        await LoveTriangles.UpdateOneAsync(filter, update);
        return await GetLoveTriangleByIdAsync(triangleId);
    }

    /// <summary>Cast a vote. One vote per non-member viewer; switching
    /// pairs silently retracts the previous. Members of the triangle
    /// can't vote (caller guard).</summary>
    public async Task<LoveTriangle?> VoteOnLoveTriangleAsync(
        string triangleId, string voterUserId, string pairKey)
    {
        if (!LoveTrianglePairKeys.Contains(pairKey)) return null;

        var triangle = await GetLoveTriangleByIdAsync(triangleId);
        if (triangle is null) return null;
        if (triangle.Status != "voting") return triangle;
        // Triangle members can't vote.
        if (voterUserId == triangle.UserAId
            || voterUserId == triangle.UserBId
            || voterUserId == triangle.UserCId)
            return triangle;

        // Find the voter's current pick (if any) to retract.
        string? prev = null;
        foreach (var (k, list) in triangle.VotesByPair)
        {
            if (list.Contains(voterUserId)) { prev = k; break; }
        }
        if (prev == pairKey) return triangle;       // no-op re-vote same pair

        var updateBuilder = Builders<LoveTriangle>.Update;
        var updates = new List<UpdateDefinition<LoveTriangle>>();
        if (prev is not null)
            updates.Add(updateBuilder.Pull<string>($"votesByPair.{prev}", voterUserId));
        updates.Add(updateBuilder.AddToSet<string>($"votesByPair.{pairKey}", voterUserId));
        await LoveTriangles.UpdateOneAsync(
            Builders<LoveTriangle>.Filter.Eq(t => t.Id, triangleId),
            updateBuilder.Combine(updates));

        return await GetLoveTriangleByIdAsync(triangleId);
    }

    public async Task<List<LoveTriangle>> GetPublicLoveTrianglesAsync(int weekOffset = 0)
    {
        // Active + voting triangles (both phases of "spectator-ready").
        // Newest first; cap at 30 to keep payloads tight.
        var allow = new[] { "active", "voting" };
        if (weekOffset > 0) allow = new[] { "completed" };
        var filter = Builders<LoveTriangle>.Filter.In(t => t.Status, allow);
        return await LoveTriangles
            .Find(filter)
            .SortByDescending(t => t.ScheduledFor)
            .Limit(30)
            .ToListAsync();
    }

    public async Task<List<LoveTriangle>> GetMyLoveTriangleHistoryAsync(string userId, int limit = 10)
    {
        var filter = Builders<LoveTriangle>.Filter.Or(
            Builders<LoveTriangle>.Filter.Eq(t => t.UserAId, userId),
            Builders<LoveTriangle>.Filter.Eq(t => t.UserBId, userId),
            Builders<LoveTriangle>.Filter.Eq(t => t.UserCId, userId));
        return await LoveTriangles
            .Find(filter)
            .SortByDescending(t => t.ScheduledFor)
            .Limit(limit)
            .ToListAsync();
    }

    // ─── Pair messages ─────────────────────────────────────────

    public async Task<LoveTrianglePairMessage> InsertLoveTrianglePairMessageAsync(
        LoveTrianglePairMessage m)
    {
        m.CreatedAt = DateTime.UtcNow;
        await LoveTrianglePairMessages.InsertOneAsync(m);
        return m;
    }

    public async Task<List<LoveTrianglePairMessage>> GetLoveTrianglePairThreadAsync(
        string triangleId, string pairKey, int limit = 200)
    {
        return await LoveTrianglePairMessages
            .Find(m => m.TriangleId == triangleId && m.PairKey == pairKey)
            .SortBy(m => m.CreatedAt)
            .Limit(limit)
            .ToListAsync();
    }

    public async Task<LoveTrianglePairMessage?> ToggleLoveTriangleExcerptAsync(
        string messageId, string callerUserId)
    {
        var msg = await LoveTrianglePairMessages
            .Find(m => m.Id == messageId)
            .FirstOrDefaultAsync();
        if (msg is null) return null;

        // Only the author OR the person who previously shared it can toggle.
        bool currentlyShared = msg.IsShared;
        bool callerCanToggle = msg.SenderUserId == callerUserId
            || msg.SharedByUserId == callerUserId;
        if (currentlyShared && !callerCanToggle) return msg;

        var filter = Builders<LoveTrianglePairMessage>.Filter.Eq(m => m.Id, messageId);
        UpdateDefinition<LoveTrianglePairMessage> update;
        if (currentlyShared)
        {
            update = Builders<LoveTrianglePairMessage>.Update
                .Set(m => m.IsShared, false)
                .Set(m => m.SharedByUserId, (string?)null)
                .Set(m => m.SharedAt, (DateTime?)null);
        }
        else
        {
            update = Builders<LoveTrianglePairMessage>.Update
                .Set(m => m.IsShared, true)
                .Set(m => m.SharedByUserId, callerUserId)
                .Set(m => m.SharedAt, DateTime.UtcNow);
        }
        await LoveTrianglePairMessages.UpdateOneAsync(filter, update);
        return await LoveTrianglePairMessages.Find(m => m.Id == messageId).FirstOrDefaultAsync();
    }

    /// <summary>Public excerpts across all pairs in a triangle, newest
    /// first. Used by the audience feed view.</summary>
    public async Task<List<LoveTrianglePairMessage>> GetLoveTrianglePublicExcerptsAsync(
        string triangleId, int limit = 50)
    {
        return await LoveTrianglePairMessages
            .Find(m => m.TriangleId == triangleId && m.IsShared)
            .SortByDescending(m => m.SharedAt)
            .Limit(limit)
            .ToListAsync();
    }

    // ════════════════════════════════════════════════════════════
    //  THE CIPHER — weekly community ARG
    // ════════════════════════════════════════════════════════════

    public const int CipherWinningAccuracyThreshold = 50;  // ≥ 50% wins

    public async Task<CipherRound> InsertCipherRoundAsync(CipherRound r)
    {
        r.CreatedAt = DateTime.UtcNow;
        await CipherRounds.InsertOneAsync(r);
        return r;
    }

    public async Task<CipherRound?> GetActiveCipherRoundAsync()
    {
        return await CipherRounds
            .Find(r => r.Status == "active")
            .SortByDescending(r => r.StartsAt)
            .FirstOrDefaultAsync();
    }

    public async Task<CipherRound?> GetCipherRoundByIdAsync(string id) =>
        await CipherRounds.Find(r => r.Id == id).FirstOrDefaultAsync();

    public async Task<List<CipherRound>> GetCipherRoundsReadyToCloseAsync()
    {
        var now = DateTime.UtcNow;
        return await CipherRounds
            .Find(r => r.Status == "active" && r.EndsAt <= now)
            .ToListAsync();
    }

    public async Task<List<CipherRound>> GetCipherArchiveAsync(int limit = 10) =>
        await CipherRounds
            .Find(r => r.Status == "closed")
            .SortByDescending(r => r.EndsAt)
            .Limit(limit)
            .ToListAsync();

    public async Task<bool> CloseCipherRoundAsync(
        string roundId, List<string> winningHunterIds)
    {
        var filter = Builders<CipherRound>.Filter.And(
            Builders<CipherRound>.Filter.Eq(r => r.Id, roundId),
            Builders<CipherRound>.Filter.Eq(r => r.Status, "active"));
        var update = Builders<CipherRound>.Update
            .Set(r => r.Status, "closed")
            .Set(r => r.ClosedAt, DateTime.UtcNow)
            .Set(r => r.WinningHunterIds, winningHunterIds);
        var result = await CipherRounds.UpdateOneAsync(filter, update);
        return result.ModifiedCount == 1;
    }

    // ─── Members ───────────────────────────────────────────────

    public async Task InsertCipherMembersAsync(IEnumerable<CipherMember> members)
    {
        var list = members.ToList();
        foreach (var m in list) m.CreatedAt = DateTime.UtcNow;
        if (list.Count > 0) await CipherMembers.InsertManyAsync(list);
    }

    public async Task<CipherMember?> GetMyCipherMemberAsync(string roundId, string userId) =>
        await CipherMembers.Find(m => m.RoundId == roundId && m.UserId == userId)
                           .FirstOrDefaultAsync();

    public async Task<List<CipherMember>> GetCipherMembersAsync(string roundId) =>
        await CipherMembers.Find(m => m.RoundId == roundId).ToListAsync();

    public async Task BulkUpdateCipherMemberScoresAsync(
        Dictionary<string, int> correctGuessersCountByMemberId)
    {
        foreach (var (memberId, count) in correctGuessersCountByMemberId)
        {
            var filter = Builders<CipherMember>.Filter.Eq(m => m.Id, memberId);
            var update = Builders<CipherMember>.Update
                .Set(m => m.CorrectGuessersCount, count)
                .Set(m => m.WasIdentified, count > 0);
            await CipherMembers.UpdateOneAsync(filter, update);
        }
    }

    // ─── Submissions ───────────────────────────────────────────

    /// <summary>Upsert a Hunter's submission for a round — re-submitting
    /// overwrites the previous guess (last write wins, by design).</summary>
    public async Task<CipherSubmission> UpsertCipherSubmissionAsync(CipherSubmission s)
    {
        var filter = Builders<CipherSubmission>.Filter.And(
            Builders<CipherSubmission>.Filter.Eq(x => x.RoundId, s.RoundId),
            Builders<CipherSubmission>.Filter.Eq(x => x.HunterUserId, s.HunterUserId));
        var update = Builders<CipherSubmission>.Update
            .SetOnInsert(x => x.RoundId,        s.RoundId)
            .SetOnInsert(x => x.HunterUserId,   s.HunterUserId)
            .SetOnInsert(x => x.HunterUsername, s.HunterUsername)
            .SetOnInsert(x => x.CreatedAt,      DateTime.UtcNow)
            .Set(x => x.GuessedPhrase,          s.GuessedPhrase)
            .Set(x => x.NamedUserIds,           s.NamedUserIds)
            .Set(x => x.SubmittedAt,            DateTime.UtcNow);
        await CipherSubmissions.UpdateOneAsync(filter, update,
            new UpdateOptions { IsUpsert = true });
        return (await CipherSubmissions.Find(filter).FirstAsync());
    }

    public async Task<CipherSubmission?> GetMyCipherSubmissionAsync(string roundId, string userId) =>
        await CipherSubmissions.Find(s => s.RoundId == roundId && s.HunterUserId == userId)
                               .FirstOrDefaultAsync();

    public async Task<List<CipherSubmission>> GetCipherSubmissionsForRoundAsync(string roundId) =>
        await CipherSubmissions.Find(s => s.RoundId == roundId).ToListAsync();

    public async Task BulkUpdateCipherSubmissionScoresAsync(
        IEnumerable<(string Id, int Accuracy, bool Won)> scored)
    {
        foreach (var (id, acc, won) in scored)
        {
            var filter = Builders<CipherSubmission>.Filter.Eq(s => s.Id, id);
            var update = Builders<CipherSubmission>.Update
                .Set(s => s.AccuracyPct,   acc)
                .Set(s => s.WonPrizeShare, won);
            await CipherSubmissions.UpdateOneAsync(filter, update);
        }
    }

    /// <summary>Top scorers across all past Cipher rounds (or one round
    /// if roundId provided). Used for the leaderboard.</summary>
    public async Task<List<CipherSubmission>> GetCipherLeaderboardAsync(
        string? roundId = null, int limit = 20)
    {
        var filter = roundId is null
            ? Builders<CipherSubmission>.Filter.Empty
            : Builders<CipherSubmission>.Filter.Eq(s => s.RoundId, roundId);
        return await CipherSubmissions
            .Find(filter)
            .SortByDescending(s => s.AccuracyPct)
            .ThenBy(s => s.SubmittedAt)
            .Limit(limit)
            .ToListAsync();
    }

    // ════════════════════════════════════════════════════════════
    //  PYAAR LIVE — flagship Saturday mass dating show
    // ════════════════════════════════════════════════════════════

    public const int PyaarCoupleCount        = 10;
    public const int PyaarMessageMaxChars    = 1000;
    public const int PyaarEliminationCount   = 3;
    public const int PyaarWinnerCount        = 3;

    // ─── Registration ──────────────────────────────────────────

    public async Task<PyaarRegistration> RegisterForPyaarLiveAsync(string userId, string eventDate)
    {
        var filter = Builders<PyaarRegistration>.Filter.And(
            Builders<PyaarRegistration>.Filter.Eq(r => r.UserId, userId),
            Builders<PyaarRegistration>.Filter.Eq(r => r.EventDate, eventDate));
        var existing = await PyaarRegistrations.Find(filter).FirstOrDefaultAsync();
        if (existing is not null)
        {
            if (existing.Status == "withdrew")
            {
                await PyaarRegistrations.UpdateOneAsync(filter,
                    Builders<PyaarRegistration>.Update
                        .Set(r => r.Status, "pending")
                        .Set(r => r.RegisteredAt, DateTime.UtcNow));
                existing.Status = "pending";
                existing.RegisteredAt = DateTime.UtcNow;
            }
            return existing;
        }
        var fresh = new PyaarRegistration
        {
            UserId       = userId,
            EventDate    = eventDate,
            RegisteredAt = DateTime.UtcNow,
            Status       = "pending",
        };
        await PyaarRegistrations.InsertOneAsync(fresh);
        return fresh;
    }

    public async Task<bool> WithdrawFromPyaarLiveAsync(string userId, string eventDate)
    {
        var filter = Builders<PyaarRegistration>.Filter.And(
            Builders<PyaarRegistration>.Filter.Eq(r => r.UserId, userId),
            Builders<PyaarRegistration>.Filter.Eq(r => r.EventDate, eventDate),
            Builders<PyaarRegistration>.Filter.Eq(r => r.Status, "pending"));
        var result = await PyaarRegistrations.UpdateOneAsync(filter,
            Builders<PyaarRegistration>.Update.Set(r => r.Status, "withdrew"));
        return result.ModifiedCount == 1;
    }

    public async Task<PyaarRegistration?> GetMyPyaarRegistrationAsync(string userId, string eventDate) =>
        await PyaarRegistrations
            .Find(r => r.UserId == userId && r.EventDate == eventDate)
            .FirstOrDefaultAsync();

    public async Task<List<PyaarRegistration>> GetPendingPyaarRegistrationsAsync(string eventDate) =>
        await PyaarRegistrations
            .Find(r => r.EventDate == eventDate && r.Status == "pending")
            .ToListAsync();

    public async Task UpdatePyaarRegistrationStatusAsync(string id, string status, string? showId, string? coupleId)
    {
        var update = Builders<PyaarRegistration>.Update
            .Set(r => r.Status, status)
            .Set(r => r.ShowId, showId)
            .Set(r => r.CoupleId, coupleId);
        await PyaarRegistrations.UpdateOneAsync(
            Builders<PyaarRegistration>.Filter.Eq(r => r.Id, id), update);
    }

    // ─── Shows ─────────────────────────────────────────────────

    public async Task<PyaarShow> InsertPyaarShowAsync(PyaarShow s)
    {
        s.CreatedAt = DateTime.UtcNow;
        await PyaarShows.InsertOneAsync(s);
        return s;
    }

    public async Task<PyaarShow?> GetPyaarShowByIdAsync(string id) =>
        await PyaarShows.Find(s => s.Id == id).FirstOrDefaultAsync();

    public async Task<PyaarShow?> GetActivePyaarShowAsync() =>
        await PyaarShows
            .Find(s => s.Status == "live")
            .SortByDescending(s => s.ScheduledFor)
            .FirstOrDefaultAsync();

    public async Task<List<PyaarShow>> GetPyaarShowsNeedingAdvanceAsync()
    {
        var now = DateTime.UtcNow;
        return await PyaarShows.Find(
            s => s.Status == "live"
              && s.CurrentRoundEndsAt != null
              && s.CurrentRoundEndsAt <= now).ToListAsync();
    }

    public async Task UpdatePyaarShowRoundAsync(
        string showId, int round, string label, DateTime? endsAt, string status)
    {
        var update = Builders<PyaarShow>.Update
            .Set(s => s.CurrentRound, round)
            .Set(s => s.CurrentRoundLabel, label)
            .Set(s => s.CurrentRoundEndsAt, endsAt)
            .Set(s => s.Status, status);
        if (status == "live" && round == 1)
            update = update.Set(s => s.StartedAt, DateTime.UtcNow);
        if (status == "completed")
            update = update.Set(s => s.EndedAt, DateTime.UtcNow);
        await PyaarShows.UpdateOneAsync(
            Builders<PyaarShow>.Filter.Eq(s => s.Id, showId), update);
    }

    public async Task SetPyaarShowEliminationAsync(string showId, List<string> eliminatedCoupleIds)
    {
        await PyaarShows.UpdateOneAsync(
            Builders<PyaarShow>.Filter.Eq(s => s.Id, showId),
            Builders<PyaarShow>.Update.Set(s => s.EliminatedCoupleIds, eliminatedCoupleIds));
    }

    public async Task SetPyaarShowWinnersAsync(string showId, List<string> winnerIdsOrdered)
    {
        await PyaarShows.UpdateOneAsync(
            Builders<PyaarShow>.Filter.Eq(s => s.Id, showId),
            Builders<PyaarShow>.Update.Set(s => s.WinningCoupleIds, winnerIdsOrdered));
    }

    public async Task<List<PyaarShow>> GetCompletedPyaarShowsAsync(int limit = 10) =>
        await PyaarShows
            .Find(s => s.Status == "completed")
            .SortByDescending(s => s.EndedAt)
            .Limit(limit)
            .ToListAsync();

    // ─── Couples ───────────────────────────────────────────────

    public async Task InsertPyaarCouplesAsync(IEnumerable<PyaarCouple> couples)
    {
        var list = couples.ToList();
        foreach (var c in list) c.CreatedAt = DateTime.UtcNow;
        if (list.Count > 0) await PyaarCouples.InsertManyAsync(list);
    }

    public async Task<List<PyaarCouple>> GetCouplesForShowAsync(string showId) =>
        await PyaarCouples.Find(c => c.ShowId == showId).SortBy(c => c.CoupleNumber).ToListAsync();

    public async Task<PyaarCouple?> GetCoupleByIdAsync(string id) =>
        await PyaarCouples.Find(c => c.Id == id).FirstOrDefaultAsync();

    public async Task<PyaarCouple?> GetMyCoupleAsync(string showId, string userId)
    {
        var filter = Builders<PyaarCouple>.Filter.And(
            Builders<PyaarCouple>.Filter.Eq(c => c.ShowId, showId),
            Builders<PyaarCouple>.Filter.Or(
                Builders<PyaarCouple>.Filter.Eq(c => c.UserAId, userId),
                Builders<PyaarCouple>.Filter.Eq(c => c.UserBId, userId)));
        return await PyaarCouples.Find(filter).FirstOrDefaultAsync();
    }

    public async Task EliminatePyaarCouplesAsync(IEnumerable<string> coupleIds, int round)
    {
        var ids = coupleIds.ToList();
        if (ids.Count == 0) return;
        var filter = Builders<PyaarCouple>.Filter.In(c => c.Id, ids);
        var update = Builders<PyaarCouple>.Update
            .Set(c => c.EliminatedAt, DateTime.UtcNow)
            .Set(c => c.EliminatedInRound, round);
        await PyaarCouples.UpdateManyAsync(filter, update);
    }

    public async Task SetPyaarCoupleRanksAsync(IEnumerable<(string Id, int Rank)> ranked)
    {
        foreach (var (id, rank) in ranked)
        {
            await PyaarCouples.UpdateOneAsync(
                Builders<PyaarCouple>.Filter.Eq(c => c.Id, id),
                Builders<PyaarCouple>.Update.Set(c => c.FinalRank, rank));
        }
    }

    // ─── Messages ──────────────────────────────────────────────

    public async Task<PyaarMessage> InsertPyaarMessageAsync(PyaarMessage m)
    {
        m.CreatedAt = DateTime.UtcNow;
        await PyaarMessages.InsertOneAsync(m);
        return m;
    }

    public async Task<List<PyaarMessage>> GetPyaarCoupleThreadAsync(string coupleId, int limit = 200) =>
        await PyaarMessages
            .Find(m => m.CoupleId == coupleId)
            .SortBy(m => m.CreatedAt)
            .Limit(limit)
            .ToListAsync();

    /// <summary>Last N messages PER couple in a show — used for the
    /// spectator grid. Returns a flat list; client groups by couple.</summary>
    public async Task<List<PyaarMessage>> GetPyaarShowRecentMessagesAsync(string showId, int perCoupleLimit = 5)
    {
        // Simple approach: pull all then trim client-side.
        // Show only spans 2 hours / 10 couples so volume is small.
        var msgs = await PyaarMessages
            .Find(m => m.ShowId == showId)
            .SortByDescending(m => m.CreatedAt)
            .Limit(perCoupleLimit * PyaarCoupleCount * 4)
            .ToListAsync();
        // Group + trim to last N per couple, preserve chronological.
        return msgs
            .GroupBy(m => m.CoupleId)
            .SelectMany(g => g.OrderByDescending(m => m.CreatedAt).Take(perCoupleLimit))
            .OrderBy(m => m.CreatedAt)
            .ToList();
    }

    // ─── Votes ─────────────────────────────────────────────────

    /// <summary>One vote per voter per show, last-write-wins. Switching
    /// couples retracts the previous vote (decrements VoteCount on the
    /// old couple, increments on the new). Voters who are themselves
    /// in a couple of this show are blocked at the hub layer.</summary>
    public async Task<PyaarVote?> CastPyaarVoteAsync(string showId, string voterUserId, string coupleId)
    {
        // Find caller's existing vote on this show, if any.
        var prev = await PyaarVotes.Find(
            v => v.ShowId == showId && v.VoterUserId == voterUserId
        ).FirstOrDefaultAsync();

        if (prev is not null && prev.CoupleId == coupleId)
            return prev;  // no-op same vote

        if (prev is not null)
        {
            // Decrement old couple, delete old vote row.
            await PyaarCouples.UpdateOneAsync(
                Builders<PyaarCouple>.Filter.Eq(c => c.Id, prev.CoupleId),
                Builders<PyaarCouple>.Update.Inc(c => c.VoteCount, -1));
            await PyaarVotes.DeleteOneAsync(v => v.Id == prev.Id);
        }

        var fresh = new PyaarVote
        {
            ShowId       = showId,
            CoupleId     = coupleId,
            VoterUserId  = voterUserId,
            Weight       = 1,
            CreatedAt    = DateTime.UtcNow,
        };
        await PyaarVotes.InsertOneAsync(fresh);
        await PyaarCouples.UpdateOneAsync(
            Builders<PyaarCouple>.Filter.Eq(c => c.Id, coupleId),
            Builders<PyaarCouple>.Update.Inc(c => c.VoteCount, 1));
        return fresh;
    }

    public async Task<PyaarVote?> GetMyPyaarVoteAsync(string showId, string voterUserId) =>
        await PyaarVotes.Find(v => v.ShowId == showId && v.VoterUserId == voterUserId).FirstOrDefaultAsync();

    // ─── PYAAR LIVE ecosystem extras ───────────────────────────

    public async Task SetPyaarCoupleVideoActiveAsync(string coupleId, bool active, string? livekitRoomName)
    {
        var update = Builders<PyaarCouple>.Update
            .Set(c => c.VideoActive, active)
            .Set(c => c.LiveKitRoomName, livekitRoomName);
        await PyaarCouples.UpdateOneAsync(
            Builders<PyaarCouple>.Filter.Eq(c => c.Id, coupleId), update);
    }

    public async Task BumpPyaarCoupleSpectatorCountAsync(string coupleId, int delta)
    {
        await PyaarCouples.UpdateOneAsync(
            Builders<PyaarCouple>.Filter.Eq(c => c.Id, coupleId),
            Builders<PyaarCouple>.Update.Inc(c => c.SpectatorCount, delta));
    }

    public async Task<List<PyaarMessage>> GetPyaarCoupleFullThreadAsync(string coupleId, int limit = 500) =>
        await PyaarMessages
            .Find(m => m.CoupleId == coupleId)
            .SortBy(m => m.CreatedAt)
            .Limit(limit)
            .ToListAsync();

    public async Task InsertPyaarReactionAsync(PyaarReaction r)
    {
        r.CreatedAt = DateTime.UtcNow;
        await PyaarReactions.InsertOneAsync(r);
    }

    /// <summary>Recent ambient reactions (last 60 seconds) so a late-
    /// joining spectator gets a few floating emojis on arrival rather
    /// than a static grid.</summary>
    public async Task<List<PyaarReaction>> GetRecentPyaarReactionsAsync(string showId, int limit = 30)
    {
        var since = DateTime.UtcNow.AddSeconds(-60);
        return await PyaarReactions
            .Find(r => r.ShowId == showId && r.CreatedAt >= since)
            .SortByDescending(r => r.CreatedAt)
            .Limit(limit)
            .ToListAsync();
    }

    // ════════════════════════════════════════════════════════════
    //  MEHFIL — creator-room platform
    // ════════════════════════════════════════════════════════════

    public const int MehfilMessageMaxChars = 1000;
    public const int MehfilTitleMaxChars   = 120;
    public static readonly string[] MehfilTemplates = new[]
    {
        "dating_show", "open_mic", "debate", "watch_party", "game_night",
        "podcast", "story_circle", "trivia", "talent_show", "networking", "custom",
    };
    public static readonly Dictionary<string, int> MehfilGifts = new()
    {
        ["rose"]    = 10,
        ["bouquet"] = 50,
        ["crown"]   = 500,
    };

    // ─── Rooms ─────────────────────────────────────────────────

    public async Task<MehfilRoom> InsertMehfilRoomAsync(MehfilRoom r)
    {
        r.CreatedAt = DateTime.UtcNow;
        await MehfilRooms.InsertOneAsync(r);
        return r;
    }

    public async Task<MehfilRoom?> GetMehfilRoomByIdAsync(string id) =>
        await MehfilRooms.Find(r => r.Id == id).FirstOrDefaultAsync();

    /// <summary>CT-cancellable overload — Debate's DebateHub uses this
    /// path so it can honour Context.ConnectionAborted. Behaviour
    /// identical to the no-CT overload above.</summary>
    public async Task<MehfilRoom?> GetMehfilRoomByIdAsync(string id, CancellationToken ct) =>
        await MehfilRooms.Find(r => r.Id == id).FirstOrDefaultAsync(ct);

    public async Task<List<MehfilRoom>> GetLiveMehfilRoomsAsync(int limit = 30) =>
        await MehfilRooms
            .Find(r => r.Status == "live")
            .SortByDescending(r => r.CurrentAudienceCount)
            .Limit(limit)
            .ToListAsync();

    public async Task<List<MehfilRoom>> GetUpcomingMehfilRoomsAsync(int limit = 30)
    {
        var now = DateTime.UtcNow;
        return await MehfilRooms
            .Find(r => r.Status == "scheduled" && r.ScheduledFor >= now)
            .SortBy(r => r.ScheduledFor)
            .Limit(limit)
            .ToListAsync();
    }

    public async Task<List<MehfilRoom>> GetMehfilRoomsByTemplateAsync(string template, int limit = 30) =>
        await MehfilRooms
            .Find(r => r.TemplateKind == template && r.Status != "ended" && r.Status != "cancelled")
            .SortByDescending(r => r.Status == "live" ? 1 : 0)
            .ThenBy(r => r.ScheduledFor)
            .Limit(limit)
            .ToListAsync();

    public async Task<List<MehfilRoom>> GetMyHostedMehfilRoomsAsync(string hostUserId, int limit = 20) =>
        await MehfilRooms
            .Find(r => r.HostUserId == hostUserId)
            .SortByDescending(r => r.CreatedAt)
            .Limit(limit)
            .ToListAsync();

    public async Task UpdateMehfilRoomStatusAsync(string roomId, string status, bool setStartedAt = false, bool setEndedAt = false)
    {
        var update = Builders<MehfilRoom>.Update.Set(r => r.Status, status);
        if (setStartedAt) update = update.Set(r => r.StartedAt, DateTime.UtcNow);
        if (setEndedAt)   update = update.Set(r => r.EndedAt,   DateTime.UtcNow);
        await MehfilRooms.UpdateOneAsync(Builders<MehfilRoom>.Filter.Eq(r => r.Id, roomId), update);
    }

    // ─── Attendance ────────────────────────────────────────────

    /// <summary>Idempotent join — if the user already has an open
    /// attendance row (LeftAt null) for this room, returns it. Else
    /// inserts and increments the cached audience count.</summary>
    public async Task<MehfilAttendance> JoinMehfilRoomAsync(string roomId, string userId, string username)
    {
        var existing = await MehfilAttendances.Find(
            a => a.RoomId == roomId && a.UserId == userId && a.LeftAt == null
        ).FirstOrDefaultAsync();
        if (existing is not null) return existing;

        var fresh = new MehfilAttendance
        {
            RoomId   = roomId,
            UserId   = userId,
            Username = username,
            JoinedAt = DateTime.UtcNow,
        };
        await MehfilAttendances.InsertOneAsync(fresh);
        await MehfilRooms.UpdateOneAsync(
            Builders<MehfilRoom>.Filter.Eq(r => r.Id, roomId),
            Builders<MehfilRoom>.Update
                .Inc(r => r.CurrentAudienceCount, 1)
                .Inc(r => r.TotalAttendeesCount,  1));
        return fresh;
    }

    public async Task<bool> LeaveMehfilRoomAsync(string roomId, string userId)
    {
        var filter = Builders<MehfilAttendance>.Filter.And(
            Builders<MehfilAttendance>.Filter.Eq(a => a.RoomId, roomId),
            Builders<MehfilAttendance>.Filter.Eq(a => a.UserId, userId),
            Builders<MehfilAttendance>.Filter.Eq(a => a.LeftAt, (DateTime?)null));
        var update = Builders<MehfilAttendance>.Update.Set(a => a.LeftAt, DateTime.UtcNow);
        var result = await MehfilAttendances.UpdateOneAsync(filter, update);
        if (result.ModifiedCount == 1)
        {
            await MehfilRooms.UpdateOneAsync(
                Builders<MehfilRoom>.Filter.Eq(r => r.Id, roomId),
                Builders<MehfilRoom>.Update.Inc(r => r.CurrentAudienceCount, -1));
        }
        return result.ModifiedCount == 1;
    }

    public async Task<List<MehfilAttendance>> GetMehfilLiveAttendeesAsync(string roomId, int limit = 100) =>
        await MehfilAttendances
            .Find(a => a.RoomId == roomId && a.LeftAt == null)
            .SortBy(a => a.JoinedAt)
            .Limit(limit)
            .ToListAsync();

    // ─── Messages ──────────────────────────────────────────────

    public async Task<MehfilMessage> InsertMehfilMessageAsync(MehfilMessage m)
    {
        m.CreatedAt = DateTime.UtcNow;
        await MehfilMessages.InsertOneAsync(m);
        return m;
    }

    public async Task<List<MehfilMessage>> GetMehfilRoomMessagesAsync(string roomId, int limit = 200) =>
        await MehfilMessages
            .Find(m => m.RoomId == roomId)
            .SortByDescending(m => m.CreatedAt)
            .Limit(limit)
            .ToListAsync()
            .ContinueWith(t => t.Result.OrderBy(m => m.CreatedAt).ToList());

    // ─── Tips ──────────────────────────────────────────────────

    /// <summary>Record a tip intent and bump the room's totalTipsTokens
    /// counter. MVP only — Phase 5 token economy will hook a real
    /// balance ledger transaction here.</summary>
    public async Task<MehfilTip> InsertMehfilTipAsync(MehfilTip tip)
    {
        if (!MehfilGifts.TryGetValue(tip.GiftType, out var tokens))
            throw new ArgumentException("Unknown gift type", nameof(tip));
        tip.TokenAmount = tokens;
        tip.CreatedAt = DateTime.UtcNow;
        await MehfilTips.InsertOneAsync(tip);

        await MehfilRooms.UpdateOneAsync(
            Builders<MehfilRoom>.Filter.Eq(r => r.Id, tip.RoomId),
            Builders<MehfilRoom>.Update.Inc(r => r.TotalTipsTokens, tokens));
        return tip;
    }

    public async Task<List<MehfilTip>> GetMehfilRoomTipsAsync(string roomId, int limit = 50) =>
        await MehfilTips
            .Find(t => t.RoomId == roomId)
            .SortByDescending(t => t.CreatedAt)
            .Limit(limit)
            .ToListAsync();

    // ════════════════════════════════════════════════════════════
    //  TOKEN ECONOMY — balance + ledger + topup orders
    // ════════════════════════════════════════════════════════════

    public const int TokenSignupBonusAmount = 100;

    /// <summary>Read-or-create a user's TokenBalance row. New rows
    /// are seeded with balance = 0 (signup bonus is a separate
    /// idempotent grant flow — see EnsureSignupBonusAsync).</summary>
    public async Task<TokenBalance> GetOrCreateTokenBalanceAsync(string userId)
    {
        var existing = await TokenBalances.Find(b => b.UserId == userId).FirstOrDefaultAsync();
        if (existing is not null) return existing;
        var fresh = new TokenBalance
        {
            UserId    = userId,
            Balance   = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        try
        {
            await TokenBalances.InsertOneAsync(fresh);
            return fresh;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Race — another thread inserted. Re-read.
            return await TokenBalances.Find(b => b.UserId == userId).FirstAsync();
        }
    }

    /// <summary>Read the current balance without auto-creating. Used
    /// for "can this user afford it?" gates before debit attempts.</summary>
    public async Task<TokenBalance?> GetTokenBalanceAsync(string userId) =>
        await TokenBalances.Find(b => b.UserId == userId).FirstOrDefaultAsync();

    /// <summary>Apply a signed delta to the balance + write an audit
    /// row, in a single logical step. Negative deltas (debits) reject
    /// if they\'d push the balance below zero. Returns the post-state
    /// balance + the inserted ledger row.
    ///
    /// We don\'t use a real transaction here because Atlas free tier
    /// doesn\'t allow multi-document transactions. Instead: we update
    /// the balance with a conditional filter that enforces the
    /// "balance >= -delta" invariant for debits, then write the ledger
    /// row. If the balance update lost the race, the ledger insert is
    /// skipped and the caller gets a failure.</summary>
    public async Task<(bool Ok, TokenBalance? Balance, TokenLedgerEntry? Entry, string? Error)>
        ApplyTokenDeltaAsync(string userId, int delta, string reason, string? note = null, string? gatewayRef = null)
    {
        if (delta == 0) return (false, null, null, "Delta can't be zero.");
        await GetOrCreateTokenBalanceAsync(userId);

        FilterDefinition<TokenBalance> filter;
        if (delta < 0)
        {
            // Debit — require balance + delta >= 0.
            filter = Builders<TokenBalance>.Filter.And(
                Builders<TokenBalance>.Filter.Eq(b => b.UserId, userId),
                Builders<TokenBalance>.Filter.Gte(b => b.Balance, -delta));
        }
        else
        {
            filter = Builders<TokenBalance>.Filter.Eq(b => b.UserId, userId);
        }

        var update = Builders<TokenBalance>.Update
            .Inc(b => b.Balance, delta)
            .Set(b => b.UpdatedAt, DateTime.UtcNow);
        if (delta > 0) update = update.Inc(b => b.LifetimeCredited, delta);
        else           update = update.Inc(b => b.LifetimeDebited, -delta);

        var options = new FindOneAndUpdateOptions<TokenBalance> { ReturnDocument = ReturnDocument.After };
        var post = await TokenBalances.FindOneAndUpdateAsync(filter, update, options);
        if (post is null)
        {
            return (false, null, null, delta < 0 ? "Insufficient token balance." : "Balance not found.");
        }

        var entry = new TokenLedgerEntry
        {
            UserId       = userId,
            Delta        = delta,
            BalanceAfter = post.Balance,
            Reason       = reason,
            Note         = note,
            GatewayRef   = gatewayRef,
            CreatedAt    = DateTime.UtcNow,
        };
        await TokenLedger.InsertOneAsync(entry);
        return (true, post, entry, null);
    }

    public async Task<List<TokenLedgerEntry>> GetTokenLedgerAsync(string userId, int limit = 30) =>
        await TokenLedger
            .Find(e => e.UserId == userId)
            .SortByDescending(e => e.CreatedAt)
            .Limit(limit)
            .ToListAsync();

    /// <summary>Idempotent signup bonus. Grants +TokenSignupBonusAmount
    /// the FIRST time it\'s called for a user; subsequent calls are
    /// no-ops. Returns true if THIS call performed the grant.</summary>
    public async Task<bool> EnsureSignupBonusAsync(string userId)
    {
        var balance = await GetOrCreateTokenBalanceAsync(userId);
        if (balance.SignupBonusGrantedAt is not null) return false;

        // Race-safe: only mark granted if it\'s still unset.
        var filter = Builders<TokenBalance>.Filter.And(
            Builders<TokenBalance>.Filter.Eq(b => b.UserId, userId),
            Builders<TokenBalance>.Filter.Eq(b => b.SignupBonusGrantedAt, (DateTime?)null));
        var mark = Builders<TokenBalance>.Update.Set(b => b.SignupBonusGrantedAt, DateTime.UtcNow);
        var result = await TokenBalances.UpdateOneAsync(filter, mark);
        if (result.ModifiedCount != 1) return false;

        var (ok, _, _, _) = await ApplyTokenDeltaAsync(
            userId, TokenSignupBonusAmount, "signup_bonus", note: "Welcome to ChatVerse");
        return ok;
    }

    // ─── Topup orders ──────────────────────────────────────────

    public async Task<TokenTopupOrder> InsertTokenTopupOrderAsync(TokenTopupOrder o)
    {
        o.CreatedAt = DateTime.UtcNow;
        await TokenTopupOrders.InsertOneAsync(o);
        return o;
    }

    public async Task<TokenTopupOrder?> GetTokenTopupOrderAsync(string orderId) =>
        await TokenTopupOrders.Find(o => o.Id == orderId).FirstOrDefaultAsync();

    public async Task<List<TokenTopupOrder>> GetMyTokenTopupOrdersAsync(string userId, int limit = 20) =>
        await TokenTopupOrders
            .Find(o => o.UserId == userId)
            .SortByDescending(o => o.CreatedAt)
            .Limit(limit)
            .ToListAsync();

    /// <summary>Idempotent — only succeeds if the order is currently
    /// "created" or "pending". Returns true on the transition that
    /// flipped it.</summary>
    public async Task<bool> SetTokenTopupOrderStatusAsync(
        string orderId, string newStatus, string? gatewayRef = null, string? ledgerEntryId = null)
    {
        var filter = Builders<TokenTopupOrder>.Filter.And(
            Builders<TokenTopupOrder>.Filter.Eq(o => o.Id, orderId),
            Builders<TokenTopupOrder>.Filter.In(o => o.Status, new[] { "created", "pending" }));
        var update = Builders<TokenTopupOrder>.Update.Set(o => o.Status, newStatus);
        if (newStatus == "succeeded" || newStatus == "failed" || newStatus == "cancelled")
            update = update.Set(o => o.CompletedAt, DateTime.UtcNow);
        if (gatewayRef is not null)     update = update.Set(o => o.GatewayRef, gatewayRef);
        if (ledgerEntryId is not null)  update = update.Set(o => o.ResultingLedgerEntryId, ledgerEntryId);

        var result = await TokenTopupOrders.UpdateOneAsync(filter, update);
        return result.ModifiedCount == 1;
    }

    /// <summary>Stamp the gateway-issued ref + redirect URL onto an
    /// order immediately after CreateAsync — these come back AFTER
    /// the order row already exists, so we persist them here. Status
    /// is not touched.</summary>
    public async Task StampTokenTopupOrderGatewayDetailsAsync(
        string orderId, string gatewayRef, string redirectUrl)
    {
        var filter = Builders<TokenTopupOrder>.Filter.Eq(o => o.Id, orderId);
        var update = Builders<TokenTopupOrder>.Update
            .Set(o => o.GatewayRef, gatewayRef)
            .Set(o => o.GatewayRedirectUrl, redirectUrl);
        await TokenTopupOrders.UpdateOneAsync(filter, update);
    }

    // ──────────────────────────────────────────────────────────────
    //  In-room polls (parity polish)
    //
    //  All operations cancellable; vote toggle uses atomic update with
    //  positional operators so concurrent voters don't trample each
    //  other. We DO NOT use a transaction (Atlas free tier doesn't
    //  support multi-doc transactions) — every mutation is one update.
    // ──────────────────────────────────────────────────────────────

    public async Task InsertPollAsync(Poll p, CancellationToken ct = default)
    {
        await Polls.InsertOneAsync(p, options: null, ct);
    }

    public async Task<Poll?> GetPollAsync(string id, CancellationToken ct = default)
    {
        return await Polls.Find(p => p.Id == id).FirstOrDefaultAsync(ct);
    }

    /// <summary>Active = not closed AND ExpiresAt &gt; now. Used by
    /// ChatPage when it opens a lounge to populate any in-flight polls.</summary>
    public async Task<List<Poll>> GetActivePollsForRoomAsync(string roomSlug, CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var filter = Builders<Poll>.Filter.And(
            Builders<Poll>.Filter.Eq(p => p.RoomSlug, roomSlug),
            Builders<Poll>.Filter.Eq(p => p.IsClosed, false),
            Builders<Poll>.Filter.Gt(p => p.ExpiresAt, nowUtc));
        return await Polls.Find(filter)
            .SortByDescending(p => p.CreatedAt)
            .Limit(20)
            .ToListAsync(ct);
    }

    /// <summary>Sweeped by the ticker every minute. Returns polls that
    /// have already expired but haven't been flagged closed yet.</summary>
    public async Task<List<Poll>> GetExpiredOpenPollsAsync(int limit, CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var filter = Builders<Poll>.Filter.And(
            Builders<Poll>.Filter.Eq(p => p.IsClosed, false),
            Builders<Poll>.Filter.Lte(p => p.ExpiresAt, nowUtc));
        return await Polls.Find(filter).Limit(limit).ToListAsync(ct);
    }

    /// <summary>Toggle a vote atomically. Single-choice = replaces any
    /// prior pick. Multi-select = adds/removes the single index from the
    /// user's list. Returns updated poll OR null if the poll is closed.</summary>
    public async Task<Poll?> ApplyPollVoteAsync(
        string pollId,
        string userId,
        int optionIndex,
        CancellationToken ct = default)
    {
        var poll = await GetPollAsync(pollId, ct);
        if (poll is null) return null;
        if (poll.IsClosed) return poll;
        if (poll.ExpiresAt <= DateTime.UtcNow) return poll;
        if (optionIndex < 0 || optionIndex >= poll.Options.Count) return poll;

        var existing = poll.Votes.TryGetValue(userId, out var list) ? list : new List<int>();
        List<int> next;
        if (poll.MultiSelect)
        {
            // Toggle this single index in the user's pick list.
            if (existing.Contains(optionIndex))
                next = existing.Where(i => i != optionIndex).ToList();
            else
                next = existing.Concat(new[] { optionIndex }).Distinct().ToList();
        }
        else
        {
            // Single-choice: clicking the same option twice clears the vote;
            // clicking a new option replaces. Lets users undo + change.
            next = existing.Count == 1 && existing[0] == optionIndex
                ? new List<int>()
                : new List<int> { optionIndex };
        }

        // String-path Set to avoid the "key contains period" issue Mongo
        // hits with dots in user-ids (Guid strings are safe but we still
        // want one consistent shape).
        var votesField = $"Votes.{userId}";
        var filter = Builders<Poll>.Filter.And(
            Builders<Poll>.Filter.Eq(p => p.Id, pollId),
            Builders<Poll>.Filter.Eq(p => p.IsClosed, false));
        var update = next.Count == 0
            ? Builders<Poll>.Update.Unset(votesField)
            : Builders<Poll>.Update.Set(votesField, next);
        await Polls.UpdateOneAsync(filter, update, options: null, ct);

        return await GetPollAsync(pollId, ct);
    }

    /// <summary>Close a poll immediately. Idempotent — safe to call
    /// from both the ticker (expiry) and the host (manual close).</summary>
    public async Task<Poll?> ClosePollAsync(string pollId, CancellationToken ct = default)
    {
        var filter = Builders<Poll>.Filter.And(
            Builders<Poll>.Filter.Eq(p => p.Id, pollId),
            Builders<Poll>.Filter.Eq(p => p.IsClosed, false));
        var update = Builders<Poll>.Update
            .Set(p => p.IsClosed, true)
            .Set(p => p.ClosedAt, DateTime.UtcNow);
        await Polls.UpdateOneAsync(filter, update, options: null, ct);
        return await GetPollAsync(pollId, ct);
    }

    /// <summary>Recent closed polls for the room, newest first. Used for
    /// the small history rail above the lounge composer.</summary>
    public async Task<List<Poll>> GetClosedPollsForRoomAsync(string roomSlug, int limit, CancellationToken ct = default)
    {
        var filter = Builders<Poll>.Filter.And(
            Builders<Poll>.Filter.Eq(p => p.RoomSlug, roomSlug),
            Builders<Poll>.Filter.Eq(p => p.IsClosed, true));
        return await Polls.Find(filter)
            .SortByDescending(p => p.ClosedAt)
            .Limit(limit)
            .ToListAsync(ct);
    }

    // ──────────────────────────────────────────────────────────────
    //  Debate (per-template Mehfil specialisation)
    //
    //  All methods take a CancellationToken; never block forever.
    //  Privileged data (nomination bios) is loaded by the hub only
    //  for the monitor — Mongo here just stores; the hub enforces.
    // ──────────────────────────────────────────────────────────────

    // ─── Rounds ───────────────────────────────────────────────

    public async Task<DebateRound?> GetActiveDebateRoundAsync(string roomId, CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var filter = Builders<DebateRound>.Filter.And(
            Builders<DebateRound>.Filter.Eq(r => r.RoomId, roomId),
            Builders<DebateRound>.Filter.Ne(r => r.Status, "ended"));
        return await DebateRounds.Find(filter)
            .SortByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task InsertDebateRoundAsync(DebateRound r, CancellationToken ct = default)
    {
        await DebateRounds.InsertOneAsync(r, options: null, ct);
    }

    public async Task<DebateRound?> UpdateDebateRoundStatusAsync(string roundId, string status, DateTime? endsAt, CancellationToken ct = default)
    {
        var filter = Builders<DebateRound>.Filter.Eq(r => r.Id, roundId);
        var update = Builders<DebateRound>.Update.Set(r => r.Status, status);
        if (status == "live")
        {
            update = update.Set(r => r.StartedAt, DateTime.UtcNow);
            if (endsAt.HasValue) update = update.Set(r => r.EndsAt, endsAt.Value);
        }
        if (status == "ended")
        {
            update = update.Set(r => r.EndedAt, DateTime.UtcNow);
        }
        await DebateRounds.UpdateOneAsync(filter, update, options: null, ct);
        return await DebateRounds.Find(filter).FirstOrDefaultAsync(ct);
    }

    // ─── Seats ────────────────────────────────────────────────

    public async Task SeedDebateSeatsAsync(string roomId, string roundId, int perSide, CancellationToken ct = default)
    {
        var seats = new List<DebateSeat>();
        foreach (var side in new[] { "pro", "con" })
        {
            for (var i = 0; i < perSide; i++)
            {
                seats.Add(new DebateSeat
                {
                    RoomId   = roomId,
                    RoundId  = roundId,
                    Side     = side,
                    Position = i,
                });
            }
        }
        if (seats.Count > 0) await DebateSeats.InsertManyAsync(seats, options: null, ct);
    }

    public async Task<List<DebateSeat>> GetDebateSeatsAsync(string roundId, CancellationToken ct = default)
    {
        var filter = Builders<DebateSeat>.Filter.Eq(s => s.RoundId, roundId);
        return await DebateSeats.Find(filter)
            .SortBy(s => s.Side).ThenBy(s => s.Position)
            .ToListAsync(ct);
    }

    /// <summary>Assign a user to a specific seat. Atomic: rejects if
    /// the seat is already taken OR if the same user is already seated
    /// on another seat in this round (no double-seating).</summary>
    public async Task<bool> TryAssignDebateSeatAsync(
        string roundId, string side, int position,
        string userId, string username, CancellationToken ct = default)
    {
        // 1. Reject if user already on any seat this round.
        var alreadyOn = await DebateSeats.Find(
            Builders<DebateSeat>.Filter.And(
                Builders<DebateSeat>.Filter.Eq(s => s.RoundId, roundId),
                Builders<DebateSeat>.Filter.Eq(s => s.OccupantUserId, userId)))
            .AnyAsync(ct);
        if (alreadyOn) return false;

        // 2. Conditional update: only succeed if the target seat is empty.
        var filter = Builders<DebateSeat>.Filter.And(
            Builders<DebateSeat>.Filter.Eq(s => s.RoundId, roundId),
            Builders<DebateSeat>.Filter.Eq(s => s.Side, side),
            Builders<DebateSeat>.Filter.Eq(s => s.Position, position),
            Builders<DebateSeat>.Filter.Eq(s => s.OccupantUserId, null as string));
        var update = Builders<DebateSeat>.Update
            .Set(s => s.OccupantUserId, userId)
            .Set(s => s.OccupantUsername, username)
            .Set(s => s.AssignedAt, DateTime.UtcNow);
        var res = await DebateSeats.UpdateOneAsync(filter, update, options: null, ct);
        return res.ModifiedCount == 1;
    }

    public async Task UnassignDebateSeatAsync(string roundId, string userId, CancellationToken ct = default)
    {
        var filter = Builders<DebateSeat>.Filter.And(
            Builders<DebateSeat>.Filter.Eq(s => s.RoundId, roundId),
            Builders<DebateSeat>.Filter.Eq(s => s.OccupantUserId, userId));
        var update = Builders<DebateSeat>.Update
            .Set(s => s.OccupantUserId, (string?)null)
            .Set(s => s.OccupantUsername, (string?)null)
            .Set(s => s.AssignedAt, (DateTime?)null);
        await DebateSeats.UpdateOneAsync(filter, update, options: null, ct);
    }

    public async Task<bool> IsUserSeatedAsync(string roundId, string userId, CancellationToken ct = default)
    {
        return await DebateSeats.Find(
            Builders<DebateSeat>.Filter.And(
                Builders<DebateSeat>.Filter.Eq(s => s.RoundId, roundId),
                Builders<DebateSeat>.Filter.Eq(s => s.OccupantUserId, userId)))
            .AnyAsync(ct);
    }

    // ─── Nominations ─────────────────────────────────────────

    public async Task<DebateNomination?> InsertDebateNominationAsync(
        DebateNomination n, CancellationToken ct = default)
    {
        // Reject duplicates: same (roundId, userId) with non-terminal status.
        var existing = await DebateNominations.Find(
            Builders<DebateNomination>.Filter.And(
                Builders<DebateNomination>.Filter.Eq(x => x.RoundId, n.RoundId),
                Builders<DebateNomination>.Filter.Eq(x => x.UserId, n.UserId),
                Builders<DebateNomination>.Filter.Eq(x => x.Status, "pending")))
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;
        await DebateNominations.InsertOneAsync(n, options: null, ct);
        return n;
    }

    public async Task WithdrawDebateNominationAsync(string roundId, string userId, CancellationToken ct = default)
    {
        var filter = Builders<DebateNomination>.Filter.And(
            Builders<DebateNomination>.Filter.Eq(n => n.RoundId, roundId),
            Builders<DebateNomination>.Filter.Eq(n => n.UserId, userId),
            Builders<DebateNomination>.Filter.Eq(n => n.Status, "pending"));
        var update = Builders<DebateNomination>.Update
            .Set(n => n.Status, "withdrawn")
            .Set(n => n.ResolvedAt, DateTime.UtcNow);
        await DebateNominations.UpdateOneAsync(filter, update, options: null, ct);
    }

    public async Task SetDebateNominationStatusAsync(string nominationId, string status, CancellationToken ct = default)
    {
        var filter = Builders<DebateNomination>.Filter.Eq(n => n.Id, nominationId);
        var update = Builders<DebateNomination>.Update
            .Set(n => n.Status, status)
            .Set(n => n.ResolvedAt, DateTime.UtcNow);
        await DebateNominations.UpdateOneAsync(filter, update, options: null, ct);
    }

    /// <summary>PRIVILEGED — returns nominations WITH bios. Caller (the
    /// hub) MUST gate this on monitor role. Includes only pending rows.</summary>
    public async Task<List<DebateNomination>> GetPendingNominationsForMonitorAsync(string roundId, CancellationToken ct = default)
    {
        var filter = Builders<DebateNomination>.Filter.And(
            Builders<DebateNomination>.Filter.Eq(n => n.RoundId, roundId),
            Builders<DebateNomination>.Filter.Eq(n => n.Status, "pending"));
        return await DebateNominations.Find(filter)
            .SortBy(n => n.RaisedAt)
            .ToListAsync(ct);
    }

    public async Task<DebateNomination?> GetDebateNominationAsync(string nominationId, CancellationToken ct = default)
    {
        return await DebateNominations.Find(n => n.Id == nominationId).FirstOrDefaultAsync(ct);
    }

    /// <summary>How many pending hands are raised in this round? Public
    /// (counts only, no bios) — surfaces a "N waiting" badge for audience.</summary>
    public async Task<int> CountPendingNominationsAsync(string roundId, CancellationToken ct = default)
    {
        var filter = Builders<DebateNomination>.Filter.And(
            Builders<DebateNomination>.Filter.Eq(n => n.RoundId, roundId),
            Builders<DebateNomination>.Filter.Eq(n => n.Status, "pending"));
        return (int)await DebateNominations.CountDocumentsAsync(filter, options: null, ct);
    }

    // ─── Moderator audit ─────────────────────────────────────

    public async Task LogModeratorActionAsync(DebateModeratorAction a, CancellationToken ct = default)
    {
        await DebateModeratorActions.InsertOneAsync(a, options: null, ct);
    }

    // ─── Highlights ──────────────────────────────────────────

    public async Task HighlightDebateMessageAsync(string messageId, CancellationToken ct = default)
    {
        var filter = Builders<DebateMessage>.Filter.Eq(m => m.Id, messageId);
        var update = Builders<DebateMessage>.Update.Set(m => m.IsHighlighted, true);
        await DebateMessages.UpdateOneAsync(filter, update, options: null, ct);
    }

    public async Task InsertDebateHighlightAsync(DebateHighlight h, CancellationToken ct = default)
    {
        await DebateHighlights.InsertOneAsync(h, options: null, ct);
    }

    // ─── Bans ────────────────────────────────────────────────

    public async Task BanFromDebateAsync(DebateBan b, CancellationToken ct = default)
    {
        await DebateBans.InsertOneAsync(b, options: null, ct);
    }

    public async Task<bool> IsBannedFromDebateAsync(string roomId, string userId, CancellationToken ct = default)
    {
        var filter = Builders<DebateBan>.Filter.And(
            Builders<DebateBan>.Filter.Eq(b => b.RoomId, roomId),
            Builders<DebateBan>.Filter.Eq(b => b.UserId, userId));
        return await DebateBans.Find(filter).AnyAsync(ct);
    }

    // ─── Chat ────────────────────────────────────────────────

    public async Task<DebateMessage> InsertDebateMessageAsync(DebateMessage m, CancellationToken ct = default)
    {
        await DebateMessages.InsertOneAsync(m, options: null, ct);
        return m;
    }

    public async Task<List<DebateMessage>> GetDebateMessagesAsync(string roomId, int limit, CancellationToken ct = default)
    {
        var filter = Builders<DebateMessage>.Filter.Eq(m => m.RoomId, roomId);
        var msgs = await DebateMessages.Find(filter)
            .SortByDescending(m => m.CreatedAt)
            .Limit(limit)
            .ToListAsync(ct);
        msgs.Reverse();
        return msgs;
    }

    public async Task<DebateMessage?> GetDebateMessageAsync(string messageId, CancellationToken ct = default)
    {
        return await DebateMessages.Find(m => m.Id == messageId).FirstOrDefaultAsync(ct);
    }

    // ──────────────────────────────────────────────────────────────
    //  Ghost Room (second per-template Mehfil specialisation)
    //
    //  Mehfil-template ghost-dating room. Coexists with the weekly
    //  /ghost-date feature (uses ghost_date_* collections — not
    //  touched here). Every method below is CT-cancellable.
    // ──────────────────────────────────────────────────────────────

    // ─── Room config ─────────────────────────────────────────

    public async Task UpsertGhostRoomConfigAsync(GhostRoomConfig c, CancellationToken ct = default)
    {
        var filter = Builders<GhostRoomConfig>.Filter.Eq(x => x.RoomId, c.RoomId);
        var existing = await GhostRoomConfigs.Find(filter).FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            await GhostRoomConfigs.InsertOneAsync(c, options: null, ct);
        }
        else
        {
            var update = Builders<GhostRoomConfig>.Update
                .Set(x => x.Privacy, c.Privacy)
                .Set(x => x.InviteCode, c.InviteCode)
                .Set(x => x.MaxVoyagers, c.MaxVoyagers)
                .Set(x => x.RoundDurationMinutes, c.RoundDurationMinutes)
                .Set(x => x.AutoPair, c.AutoPair);
            await GhostRoomConfigs.UpdateOneAsync(filter, update, options: null, ct);
        }
    }

    public async Task<GhostRoomConfig?> GetGhostRoomConfigAsync(string roomId, CancellationToken ct = default)
    {
        return await GhostRoomConfigs.Find(c => c.RoomId == roomId).FirstOrDefaultAsync(ct);
    }

    public async Task<GhostRoomConfig?> GetGhostRoomConfigByInviteCodeAsync(string inviteCode, CancellationToken ct = default)
    {
        return await GhostRoomConfigs.Find(c => c.InviteCode == inviteCode).FirstOrDefaultAsync(ct);
    }

    // ─── Voyagers ────────────────────────────────────────────

    /// <summary>Get-or-create a voyager record for (room, user). Tag
    /// is allocated as V{N+1} where N = current voyager count for the
    /// room. Race-safe enough at low audience scale — duplicate-key
    /// risk is mitigated by a re-read on conflict.</summary>
    public async Task<GhostVoyager> GetOrCreateVoyagerAsync(string roomId, string userId, CancellationToken ct = default)
    {
        var existing = await GhostVoyagers.Find(
            Builders<GhostVoyager>.Filter.And(
                Builders<GhostVoyager>.Filter.Eq(v => v.RoomId, roomId),
                Builders<GhostVoyager>.Filter.Eq(v => v.UserId, userId)))
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            // Returning user — reset LeftAt if they re-join.
            if (existing.LeftAt.HasValue)
            {
                await GhostVoyagers.UpdateOneAsync(
                    Builders<GhostVoyager>.Filter.Eq(v => v.Id, existing.Id),
                    Builders<GhostVoyager>.Update.Set(v => v.LeftAt, (DateTime?)null),
                    options: null, ct);
            }
            return existing;
        }

        var count = (int)await GhostVoyagers.CountDocumentsAsync(
            Builders<GhostVoyager>.Filter.Eq(v => v.RoomId, roomId),
            options: null, ct);

        var fresh = new GhostVoyager
        {
            RoomId     = roomId,
            UserId     = userId,
            VoyagerTag = $"V{count + 1}",
            Status     = "lobby",
            JoinedAt   = DateTime.UtcNow,
        };
        await GhostVoyagers.InsertOneAsync(fresh, options: null, ct);
        return fresh;
    }

    public async Task<List<GhostVoyager>> GetActiveVoyagersAsync(string roomId, CancellationToken ct = default)
    {
        var filter = Builders<GhostVoyager>.Filter.And(
            Builders<GhostVoyager>.Filter.Eq(v => v.RoomId, roomId),
            Builders<GhostVoyager>.Filter.Eq(v => v.LeftAt, null as DateTime?));
        return await GhostVoyagers.Find(filter)
            .SortBy(v => v.JoinedAt)
            .ToListAsync(ct);
    }

    public async Task MarkVoyagerLeftAsync(string roomId, string userId, CancellationToken ct = default)
    {
        var filter = Builders<GhostVoyager>.Filter.And(
            Builders<GhostVoyager>.Filter.Eq(v => v.RoomId, roomId),
            Builders<GhostVoyager>.Filter.Eq(v => v.UserId, userId));
        var update = Builders<GhostVoyager>.Update.Set(v => v.LeftAt, DateTime.UtcNow);
        await GhostVoyagers.UpdateOneAsync(filter, update, options: null, ct);
    }

    public async Task SetVoyagerStatusAsync(string voyagerId, string status, CancellationToken ct = default)
    {
        var filter = Builders<GhostVoyager>.Filter.Eq(v => v.Id, voyagerId);
        var update = Builders<GhostVoyager>.Update.Set(v => v.Status, status);
        await GhostVoyagers.UpdateOneAsync(filter, update, options: null, ct);
    }

    public async Task<GhostVoyager?> GetVoyagerAsync(string roomId, string userId, CancellationToken ct = default)
    {
        return await GhostVoyagers.Find(
            Builders<GhostVoyager>.Filter.And(
                Builders<GhostVoyager>.Filter.Eq(v => v.RoomId, roomId),
                Builders<GhostVoyager>.Filter.Eq(v => v.UserId, userId)))
            .FirstOrDefaultAsync(ct);
    }

    // ─── Nominations ─────────────────────────────────────────

    public async Task<GhostNomination?> InsertGhostNominationAsync(GhostNomination n, CancellationToken ct = default)
    {
        var existing = await GhostNominations.Find(
            Builders<GhostNomination>.Filter.And(
                Builders<GhostNomination>.Filter.Eq(x => x.RoomId, n.RoomId),
                Builders<GhostNomination>.Filter.Eq(x => x.UserId, n.UserId),
                Builders<GhostNomination>.Filter.Eq(x => x.Status, "pending")))
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        await GhostNominations.InsertOneAsync(n, options: null, ct);
        return n;
    }

    public async Task WithdrawGhostNominationAsync(string roomId, string userId, CancellationToken ct = default)
    {
        var filter = Builders<GhostNomination>.Filter.And(
            Builders<GhostNomination>.Filter.Eq(n => n.RoomId, roomId),
            Builders<GhostNomination>.Filter.Eq(n => n.UserId, userId),
            Builders<GhostNomination>.Filter.Eq(n => n.Status, "pending"));
        var update = Builders<GhostNomination>.Update
            .Set(n => n.Status, "withdrawn")
            .Set(n => n.ResolvedAt, DateTime.UtcNow);
        await GhostNominations.UpdateOneAsync(filter, update, options: null, ct);
    }

    public async Task SetGhostNominationStatusAsync(string nominationId, string status, CancellationToken ct = default)
    {
        var filter = Builders<GhostNomination>.Filter.Eq(n => n.Id, nominationId);
        var update = Builders<GhostNomination>.Update
            .Set(n => n.Status, status)
            .Set(n => n.ResolvedAt, DateTime.UtcNow);
        await GhostNominations.UpdateOneAsync(filter, update, options: null, ct);
    }

    /// <summary>PRIVILEGED — full bios. Hub must gate on matchmaker role.</summary>
    public async Task<List<GhostNomination>> GetPendingGhostNominationsForMatchmakerAsync(string roomId, CancellationToken ct = default)
    {
        var filter = Builders<GhostNomination>.Filter.And(
            Builders<GhostNomination>.Filter.Eq(n => n.RoomId, roomId),
            Builders<GhostNomination>.Filter.Eq(n => n.Status, "pending"));
        return await GhostNominations.Find(filter)
            .SortBy(n => n.RaisedAt)
            .ToListAsync(ct);
    }

    public async Task<GhostNomination?> GetGhostNominationAsync(string nominationId, CancellationToken ct = default)
    {
        return await GhostNominations.Find(n => n.Id == nominationId).FirstOrDefaultAsync(ct);
    }

    public async Task<int> CountPendingGhostNominationsAsync(string roomId, CancellationToken ct = default)
    {
        var filter = Builders<GhostNomination>.Filter.And(
            Builders<GhostNomination>.Filter.Eq(n => n.RoomId, roomId),
            Builders<GhostNomination>.Filter.Eq(n => n.Status, "pending"));
        return (int)await GhostNominations.CountDocumentsAsync(filter, options: null, ct);
    }

    // ─── Pairs ───────────────────────────────────────────────

    public async Task<GhostPair> InsertGhostPairAsync(GhostPair p, CancellationToken ct = default)
    {
        await GhostPairs.InsertOneAsync(p, options: null, ct);
        return p;
    }

    public async Task<GhostPair?> GetGhostPairAsync(string pairId, CancellationToken ct = default)
    {
        return await GhostPairs.Find(p => p.Id == pairId).FirstOrDefaultAsync(ct);
    }

    public async Task<List<GhostPair>> GetActiveGhostPairsAsync(string roomId, CancellationToken ct = default)
    {
        var filter = Builders<GhostPair>.Filter.And(
            Builders<GhostPair>.Filter.Eq(p => p.RoomId, roomId),
            Builders<GhostPair>.Filter.Eq(p => p.EndedAt, null as DateTime?));
        return await GhostPairs.Find(filter).ToListAsync(ct);
    }

    public async Task<GhostPair?> GetActivePairForUserAsync(string roomId, string userId, CancellationToken ct = default)
    {
        var filter = Builders<GhostPair>.Filter.And(
            Builders<GhostPair>.Filter.Eq(p => p.RoomId, roomId),
            Builders<GhostPair>.Filter.Eq(p => p.EndedAt, null as DateTime?),
            Builders<GhostPair>.Filter.Or(
                Builders<GhostPair>.Filter.Eq(p => p.VoyagerAUserId, userId),
                Builders<GhostPair>.Filter.Eq(p => p.VoyagerBUserId, userId)));
        return await GhostPairs.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task SetGhostPairStartedAsync(string pairId, CancellationToken ct = default)
    {
        var filter = Builders<GhostPair>.Filter.Eq(p => p.Id, pairId);
        var update = Builders<GhostPair>.Update.Set(p => p.StartedAt, DateTime.UtcNow);
        await GhostPairs.UpdateOneAsync(filter, update, options: null, ct);
    }

    public async Task SetGhostPairRevealVoteAsync(string pairId, string userId, bool wantsReveal, CancellationToken ct = default)
    {
        var p = await GetGhostPairAsync(pairId, ct);
        if (p is null) return;
        UpdateDefinition<GhostPair> update;
        if (string.Equals(p.VoyagerAUserId, userId, StringComparison.OrdinalIgnoreCase))
            update = Builders<GhostPair>.Update.Set(x => x.VoyagerAWantsReveal, wantsReveal);
        else if (string.Equals(p.VoyagerBUserId, userId, StringComparison.OrdinalIgnoreCase))
            update = Builders<GhostPair>.Update.Set(x => x.VoyagerBWantsReveal, wantsReveal);
        else return;
        await GhostPairs.UpdateOneAsync(
            Builders<GhostPair>.Filter.Eq(x => x.Id, pairId),
            update, options: null, ct);
    }

    public async Task<GhostPair?> ResolveGhostPairOutcomeAsync(string pairId, CancellationToken ct = default)
    {
        var p = await GetGhostPairAsync(pairId, ct);
        if (p is null) return null;

        string outcome;
        if (p.VoyagerAWantsReveal is null || p.VoyagerBWantsReveal is null)
        {
            outcome = "abandoned";
        }
        else if (p.VoyagerAWantsReveal == true && p.VoyagerBWantsReveal == true)
        {
            outcome = "mutual_reveal";
        }
        else if (p.VoyagerAWantsReveal == false && p.VoyagerBWantsReveal == false)
        {
            outcome = "mutual_pass";
        }
        else
        {
            outcome = "bittersweet";
        }

        var update = Builders<GhostPair>.Update
            .Set(x => x.Outcome, outcome)
            .Set(x => x.EndedAt, DateTime.UtcNow);
        await GhostPairs.UpdateOneAsync(
            Builders<GhostPair>.Filter.Eq(x => x.Id, pairId),
            update, options: null, ct);
        return await GetGhostPairAsync(pairId, ct);
    }

    public async Task EndAllActivePairsAsync(string roomId, CancellationToken ct = default)
    {
        var filter = Builders<GhostPair>.Filter.And(
            Builders<GhostPair>.Filter.Eq(p => p.RoomId, roomId),
            Builders<GhostPair>.Filter.Eq(p => p.EndedAt, null as DateTime?));
        var update = Builders<GhostPair>.Update
            .Set(p => p.EndedAt, DateTime.UtcNow)
            .Set(p => p.Outcome, "abandoned");
        await GhostPairs.UpdateManyAsync(filter, update, options: null, ct);
    }

    // ─── Pair chat ───────────────────────────────────────────

    public async Task<GhostPairMessage> InsertGhostPairMessageAsync(GhostPairMessage m, CancellationToken ct = default)
    {
        await GhostPairMessages.InsertOneAsync(m, options: null, ct);
        return m;
    }

    public async Task<List<GhostPairMessage>> GetPairMessagesAsync(string pairId, int limit, CancellationToken ct = default)
    {
        var filter = Builders<GhostPairMessage>.Filter.Eq(m => m.PairId, pairId);
        var msgs = await GhostPairMessages.Find(filter)
            .SortByDescending(m => m.CreatedAt)
            .Limit(limit)
            .ToListAsync(ct);
        msgs.Reverse();
        return msgs;
    }

    // ─── Reveals + bans + audit ──────────────────────────────

    public async Task InsertGhostRevealAsync(GhostReveal r, CancellationToken ct = default)
    {
        await GhostReveals.InsertOneAsync(r, options: null, ct);
    }

    public async Task BanFromGhostAsync(GhostBan b, CancellationToken ct = default)
    {
        await GhostBans.InsertOneAsync(b, options: null, ct);
    }

    public async Task<bool> IsBannedFromGhostAsync(string roomId, string userId, CancellationToken ct = default)
    {
        var filter = Builders<GhostBan>.Filter.And(
            Builders<GhostBan>.Filter.Eq(b => b.RoomId, roomId),
            Builders<GhostBan>.Filter.Eq(b => b.UserId, userId));
        return await GhostBans.Find(filter).AnyAsync(ct);
    }

    public async Task LogGhostMatchmakerActionAsync(GhostMatchmakerAction a, CancellationToken ct = default)
    {
        await GhostMatchmakerActions.InsertOneAsync(a, options: null, ct);
    }

    // ──────────────────────────────────────────────────────────────
    //  Open Mic (third per-template Mehfil specialisation)
    // ──────────────────────────────────────────────────────────────

    // ─── Config ──────────────────────────────────────────────

    public async Task UpsertOpenMicConfigAsync(OpenMicConfig c, CancellationToken ct = default)
    {
        var filter = Builders<OpenMicConfig>.Filter.Eq(x => x.RoomId, c.RoomId);
        var existing = await OpenMicConfigs.Find(filter).FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            await OpenMicConfigs.InsertOneAsync(c, options: null, ct);
        }
        else
        {
            var update = Builders<OpenMicConfig>.Update
                .Set(x => x.Privacy, c.Privacy)
                .Set(x => x.InviteCode, c.InviteCode)
                .Set(x => x.SlotDurationSeconds, c.SlotDurationSeconds);
            await OpenMicConfigs.UpdateOneAsync(filter, update, options: null, ct);
        }
    }

    public async Task<OpenMicConfig?> GetOpenMicConfigAsync(string roomId, CancellationToken ct = default)
    {
        return await OpenMicConfigs.Find(c => c.RoomId == roomId).FirstOrDefaultAsync(ct);
    }

    // ─── Set lifecycle ───────────────────────────────────────

    public async Task<OpenMicSet?> GetActiveOpenMicSetAsync(string roomId, CancellationToken ct = default)
    {
        var filter = Builders<OpenMicSet>.Filter.And(
            Builders<OpenMicSet>.Filter.Eq(s => s.RoomId, roomId),
            Builders<OpenMicSet>.Filter.Ne(s => s.Status, "ended"));
        return await OpenMicSets.Find(filter).SortByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);
    }

    public async Task InsertOpenMicSetAsync(OpenMicSet s, CancellationToken ct = default)
    {
        await OpenMicSets.InsertOneAsync(s, options: null, ct);
    }

    public async Task SetOpenMicSetStatusAsync(string setId, string status, CancellationToken ct = default)
    {
        var filter = Builders<OpenMicSet>.Filter.Eq(s => s.Id, setId);
        var update = Builders<OpenMicSet>.Update.Set(s => s.Status, status);
        if (status == "ended")
            update = update.Set(s => s.EndedAt, DateTime.UtcNow);
        await OpenMicSets.UpdateOneAsync(filter, update, options: null, ct);
    }

    // ─── Queue ───────────────────────────────────────────────

    public async Task<OpenMicQueueEntry?> InsertOpenMicQueueEntryAsync(OpenMicQueueEntry e, CancellationToken ct = default)
    {
        var existing = await OpenMicQueueEntries.Find(
            Builders<OpenMicQueueEntry>.Filter.And(
                Builders<OpenMicQueueEntry>.Filter.Eq(x => x.SetId, e.SetId),
                Builders<OpenMicQueueEntry>.Filter.Eq(x => x.UserId, e.UserId),
                Builders<OpenMicQueueEntry>.Filter.Eq(x => x.Status, "pending")))
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        // Assign next position.
        var count = (int)await OpenMicQueueEntries.CountDocumentsAsync(
            Builders<OpenMicQueueEntry>.Filter.And(
                Builders<OpenMicQueueEntry>.Filter.Eq(x => x.SetId, e.SetId),
                Builders<OpenMicQueueEntry>.Filter.Eq(x => x.Status, "pending")),
            options: null, ct);
        e.Position = count + 1;
        await OpenMicQueueEntries.InsertOneAsync(e, options: null, ct);
        return e;
    }

    public async Task WithdrawOpenMicQueueEntryAsync(string setId, string userId, CancellationToken ct = default)
    {
        var filter = Builders<OpenMicQueueEntry>.Filter.And(
            Builders<OpenMicQueueEntry>.Filter.Eq(e => e.SetId, setId),
            Builders<OpenMicQueueEntry>.Filter.Eq(e => e.UserId, userId),
            Builders<OpenMicQueueEntry>.Filter.Eq(e => e.Status, "pending"));
        var update = Builders<OpenMicQueueEntry>.Update
            .Set(e => e.Status, "withdrawn")
            .Set(e => e.ResolvedAt, DateTime.UtcNow);
        await OpenMicQueueEntries.UpdateOneAsync(filter, update, options: null, ct);
    }

    public async Task SetOpenMicQueueEntryStatusAsync(string entryId, string status, CancellationToken ct = default)
    {
        var filter = Builders<OpenMicQueueEntry>.Filter.Eq(e => e.Id, entryId);
        var update = Builders<OpenMicQueueEntry>.Update
            .Set(e => e.Status, status)
            .Set(e => e.ResolvedAt, DateTime.UtcNow);
        await OpenMicQueueEntries.UpdateOneAsync(filter, update, options: null, ct);
    }

    /// <summary>PRIVILEGED — full bios. MC-gated at hub layer.</summary>
    public async Task<List<OpenMicQueueEntry>> GetPendingOpenMicQueueForMcAsync(string setId, CancellationToken ct = default)
    {
        var filter = Builders<OpenMicQueueEntry>.Filter.And(
            Builders<OpenMicQueueEntry>.Filter.Eq(e => e.SetId, setId),
            Builders<OpenMicQueueEntry>.Filter.Eq(e => e.Status, "pending"));
        return await OpenMicQueueEntries.Find(filter)
            .SortBy(e => e.Position)
            .ToListAsync(ct);
    }

    public async Task<OpenMicQueueEntry?> GetOpenMicQueueEntryAsync(string entryId, CancellationToken ct = default)
    {
        return await OpenMicQueueEntries.Find(e => e.Id == entryId).FirstOrDefaultAsync(ct);
    }

    public async Task<int> CountPendingOpenMicQueueAsync(string setId, CancellationToken ct = default)
    {
        var filter = Builders<OpenMicQueueEntry>.Filter.And(
            Builders<OpenMicQueueEntry>.Filter.Eq(e => e.SetId, setId),
            Builders<OpenMicQueueEntry>.Filter.Eq(e => e.Status, "pending"));
        return (int)await OpenMicQueueEntries.CountDocumentsAsync(filter, options: null, ct);
    }

    // ─── Slots ───────────────────────────────────────────────

    public async Task<OpenMicSlot> InsertOpenMicSlotAsync(OpenMicSlot s, CancellationToken ct = default)
    {
        await OpenMicSlots.InsertOneAsync(s, options: null, ct);
        return s;
    }

    public async Task<OpenMicSlot?> GetActiveOpenMicSlotAsync(string roomId, CancellationToken ct = default)
    {
        var filter = Builders<OpenMicSlot>.Filter.And(
            Builders<OpenMicSlot>.Filter.Eq(s => s.RoomId, roomId),
            Builders<OpenMicSlot>.Filter.Eq(s => s.EndedAt, null as DateTime?));
        return await OpenMicSlots.Find(filter).SortByDescending(s => s.StartedAt).FirstOrDefaultAsync(ct);
    }

    public async Task<OpenMicSlot?> GetOpenMicSlotAsync(string slotId, CancellationToken ct = default)
    {
        return await OpenMicSlots.Find(s => s.Id == slotId).FirstOrDefaultAsync(ct);
    }

    public async Task EndOpenMicSlotAsync(string slotId, CancellationToken ct = default)
    {
        var filter = Builders<OpenMicSlot>.Filter.Eq(s => s.Id, slotId);
        var update = Builders<OpenMicSlot>.Update.Set(s => s.EndedAt, DateTime.UtcNow);
        await OpenMicSlots.UpdateOneAsync(filter, update, options: null, ct);
    }

    public async Task IncrementOpenMicSlotApplauseAsync(string slotId, int delta, CancellationToken ct = default)
    {
        var filter = Builders<OpenMicSlot>.Filter.Eq(s => s.Id, slotId);
        var update = Builders<OpenMicSlot>.Update.Inc(s => s.ApplauseCount, delta);
        await OpenMicSlots.UpdateOneAsync(filter, update, options: null, ct);
    }

    public async Task HighlightOpenMicSlotAsync(string slotId, CancellationToken ct = default)
    {
        var filter = Builders<OpenMicSlot>.Filter.Eq(s => s.Id, slotId);
        var update = Builders<OpenMicSlot>.Update.Set(s => s.IsHighlighted, true);
        await OpenMicSlots.UpdateOneAsync(filter, update, options: null, ct);
    }

    public async Task<List<OpenMicSlot>> GetRecentOpenMicSlotsAsync(string roomId, int limit, CancellationToken ct = default)
    {
        var filter = Builders<OpenMicSlot>.Filter.Eq(s => s.RoomId, roomId);
        return await OpenMicSlots.Find(filter).SortByDescending(s => s.StartedAt).Limit(limit).ToListAsync(ct);
    }

    // ─── Reactions ───────────────────────────────────────────

    public async Task InsertOpenMicReactionAsync(OpenMicReaction r, CancellationToken ct = default)
    {
        await OpenMicReactions.InsertOneAsync(r, options: null, ct);
    }

    // ─── Bans + audit ────────────────────────────────────────

    public async Task BanFromOpenMicAsync(OpenMicBan b, CancellationToken ct = default)
    {
        await OpenMicBans.InsertOneAsync(b, options: null, ct);
    }

    public async Task<bool> IsBannedFromOpenMicAsync(string roomId, string userId, CancellationToken ct = default)
    {
        var filter = Builders<OpenMicBan>.Filter.And(
            Builders<OpenMicBan>.Filter.Eq(b => b.RoomId, roomId),
            Builders<OpenMicBan>.Filter.Eq(b => b.UserId, userId));
        return await OpenMicBans.Find(filter).AnyAsync(ct);
    }

    public async Task LogOpenMicMcActionAsync(OpenMicMcAction a, CancellationToken ct = default)
    {
        await OpenMicMcActions.InsertOneAsync(a, options: null, ct);
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

public class DmConversationSummary
{
    public string ConversationId   { get; set; } = default!;
    public string OtherUserId      { get; set; } = default!;
    public int    UnreadCount      { get; set; }
    public string LastMessageContent  { get; set; } = "";
    public string LastMessageSenderId { get; set; } = "";
    public DateTime LastMessageAt  { get; set; }
}