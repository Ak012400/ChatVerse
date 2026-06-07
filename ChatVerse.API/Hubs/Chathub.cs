using ChatVerse.API.Extensions;
using ChatVerse.API.Models.Games;
using ChatVerse.API.Services;
using ChatVerse.Domain.Constants;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.ExternalServices.OpenAI;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using ChatVerse.Infrastructure.Persistence.Redis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatVerse.API.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private readonly MongoService _mongo;
    private readonly RedisService _redis;
    private readonly PostgresProcService _postgres;
    private readonly ModerationOrchestrator _moderation;
    private readonly ILogger<ChatHub> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private static readonly ConcurrentQueue<string> _waitingUsers = new ConcurrentQueue<string>();

    public ChatHub(
        MongoService mongo,
        RedisService redis,
        PostgresProcService postgres,
        ModerationOrchestrator moderation,
        ILogger<ChatHub> logger,
        IServiceScopeFactory scopeFactory)
    {
        _mongo = mongo;
        _redis = redis;
        _postgres = postgres;
        _moderation = moderation;
        _logger = logger;
        _scopeFactory = scopeFactory;
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

        // Rebuild waiting queue without this user.
        // Order matters: snapshot the survivors FIRST, then clear the
        // shared queue, then enqueue the survivors. The earlier version
        // cleared after enqueueing, which wiped the entire queue on every
        // disconnect.
        var survivors = _waitingUsers.Where(u => u != userId).ToList();
        _waitingUsers.Clear();
        foreach (var user in survivors) _waitingUsers.Enqueue(user);

        await _redis.SetUserOfflineAsync(userId);

        var roomSlug = await _redis.GetStringAsync($"conn:room:{Context.ConnectionId}");
        if (roomSlug != null)
        {
            await _redis.DecrementRoomCountAsync(roomSlug);
            await _redis.LeaveRoomPresenceAsync(roomSlug, userId);
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

        var room = await _mongo.GetRoomBySlugAsync(roomSlug);
        if (room == null)
        {
            await Clients.Caller.SendAsync("Error", "Room not found");
            return;
        }

        if (room.Category == "18plus" && !ageVerified)
        {
            await Clients.Caller.SendAsync("Error", "Age verification required for this room");
            return;
        }

        if (trustScore < TrustBands.NewMax)
        {
            await Clients.Caller.SendAsync("Error", "Your trust score is too low to join rooms");
            return;
        }

        var (isBanned, _, _) = await _postgres.CheckRoomBanAsync(Guid.Parse(userId), roomSlug);
        if (isBanned)
        {
            await Clients.Caller.SendAsync("Error", "You are banned from this room");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, roomSlug);
        await _redis.SetStringAsync($"conn:room:{Context.ConnectionId}", roomSlug, TimeSpan.FromHours(24));
        await _redis.IncrementRoomCountAsync(roomSlug);
        // Set-based presence dedups across multiple tabs from the same user.
        // The legacy counter above is kept for backward compat with anything
        // still polling RoomActiveCount.
        await _redis.JoinRoomPresenceAsync(roomSlug, userId);

        var messages = await _mongo.GetRoomMessagesAsync(roomSlug, 0, 50);
        await Clients.Caller.SendAsync("RoomHistory", new
        {
            roomSlug,
            messages = messages.Select(MapMessage)
        });

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
        await _redis.LeaveRoomPresenceAsync(roomSlug, userId);

        await Clients.OthersInGroup(roomSlug).SendAsync("UserLeft", new
        {
            userId,
            username,
            activeCount = await _redis.GetRoomActiveCountAsync(roomSlug)
        });
    }

    // ============================================================
    //  SendMessage (Updated with Ephemeral Image / Type support)
    // ============================================================
    public async Task SendMessage(string roomSlug, string content, string type = "text", string? mediaUrl = null, string? replyToId = null)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);
        var trustScore = JwtService.GetTrustScore(Context.User!);

        // ── 1. EPHEMERAL IMAGE (VANISH MODE) LOGIC ──
        if (type == "ephemeral_image")
        {
            var tempMsgId = Guid.NewGuid().ToString("N")[..12];
            await Clients.Group(roomSlug).SendAsync("ReceiveMessage", new
            {
                id = tempMsgId,
                roomId = roomSlug,
                senderId = userId,
                senderName = username,
                senderAvatar = (string?)null,
                content = content ?? "📸 sent a photo",
                type = type,
                mediaUrl = mediaUrl, // Base64 Compressed Image
                replyTo = replyToId,
                reactions = new Dictionary<string, List<string>>(),
                modStatus = "clean", // Frontend NSFW JS ne pass kar diya hai tabhi yaha aaya
                createdAt = DateTime.UtcNow
            });

            _logger.LogInformation("Ephemeral image sent by {Username} in {Room}", username, roomSlug);
            return; // 🛑 Yahi se wapas laut jao, DB mein kuch save mat karo!
        }

        // ── 2. NORMAL TEXT MESSAGE LOGIC ──
        if (string.IsNullOrWhiteSpace(content) || content.Length > 2000)
        {
            await Clients.Caller.SendAsync("Error", "Invalid message content");
            return;
        }

        var message = new Message
        {
            RoomId = roomSlug,
            SenderId = userId,
            SenderName = username,
            SenderTrustScore = trustScore,
            Content = content.Trim(),
            Type = type,
            MediaUrl = mediaUrl,
            ReplyTo = replyToId,
            Moderation = new MessageModeration { Status = "pending" },
            CreatedAt = DateTime.UtcNow
        };

        var saved = await _mongo.InsertMessageAsync(message);
        await _mongo.IncrementRoomMessageCountAsync(roomSlug);

        // Broadcast immediately
        await Clients.Group(roomSlug).SendAsync("ReceiveMessage", MapMessage(saved));

        // Mark active day
        try { await _postgres.MarkUserActiveDayAsync(Guid.Parse(userId)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to mark active day"); }

        // Moderation + trust warning — all in background with fresh scope
        var callerClient = Clients.Caller;
        var roomClients = Clients.Group(roomSlug);
        var userIdParsed = Guid.Parse(userId);
        var messageId = saved.Id!;

        _ = Task.Run(async () =>
        {
            try
            {
                // Step 1: Moderate Text
                await _moderation.ModerateMessageAsync(
                    messageId: messageId,
                    roomId: roomSlug,
                    senderId: userId,
                    content: content,
                    roomClients: roomClients
                );

                // Step 2: Trust score warning — fresh scope
                using var scope = _scopeFactory.CreateScope();
                var scopedPostgres = scope.ServiceProvider
                    .GetRequiredService<PostgresProcService>();

                var newScore = await scopedPostgres.GetTrustScoreAsync(userIdParsed);
                if (newScore <= 60)
                {
                    var band = newScore <= 20 ? "New" :
                               newScore <= 40 ? "Restricted" :
                               newScore <= 70 ? "Normal" :
                               newScore <= 90 ? "Trusted" : "Elite";

                    await callerClient.SendAsync("TrustWarning", new { score = newScore, band });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Moderation task failed for message {Id}", messageId);
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
        await Clients.Group(roomSlug).SendAsync("MessageReaction", new { messageId, emoji, userId });
    }

    // ============================================================
    //  DIRECT MESSAGES (DMs)
    //  Realtime path — persistence + moderation are kicked off here.
    //  The REST DmsController is a fallback for cold loads / non-hub
    //  clients. Both routes converge on the same MongoDB collection
    //  and emit the same "ReceiveDm" payload.
    // ============================================================

    public async Task SendDm(string recipientId, string content)
    {
        if (JwtService.GetIsGuest(Context.User!))
        {
            await Clients.Caller.SendAsync("Error", "DMs require a registered account");
            return;
        }
        if (string.IsNullOrWhiteSpace(content) || content.Length > 2000)
        {
            await Clients.Caller.SendAsync("Error", "Invalid DM content");
            return;
        }
        if (!Guid.TryParse(recipientId, out _))
        {
            await Clients.Caller.SendAsync("Error", "Invalid recipient");
            return;
        }

        var senderId = JwtService.GetUserId(Context.User!).ToString();
        var senderName = JwtService.GetUsername(Context.User!);
        if (senderId == recipientId)
        {
            await Clients.Caller.SendAsync("Error", "You can't DM yourself");
            return;
        }

        var convId = Infrastructure.Persistence.MongoDB.MongoService.ConversationIdFor(senderId, recipientId);
        var dm = new DmMessage
        {
            ConversationId = convId,
            SenderId       = senderId,
            SenderName     = senderName,
            RecipientId    = recipientId,
            Content        = content.Trim(),
            Type           = "text",
            CreatedAt      = DateTime.UtcNow,
        };

        var saved = await _mongo.InsertDmAsync(dm);

        var payload = new
        {
            id            = saved.Id,
            conversationId = saved.ConversationId,
            senderId      = saved.SenderId,
            senderName    = saved.SenderName,
            recipientId   = saved.RecipientId,
            content       = saved.Content,
            type          = saved.Type,
            createdAt     = saved.CreatedAt,
        };

        // Send to both ends — the sender sees their own message echoed
        // back so the UI stays simple (single render path for incoming
        // and outgoing messages).
        await Clients.Users(new[] { senderId, recipientId }).SendAsync("ReceiveDm", payload);

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var moderation = scope.ServiceProvider
                    .GetRequiredService<Infrastructure.ExternalServices.OpenAI.ModerationOrchestrator>();
                await moderation.ModerateMessageAsync(
                    messageId: saved.Id ?? "",
                    roomId: convId,
                    senderId: senderId,
                    content: content,
                    roomClients: Clients.Users(new[] { senderId, recipientId })
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DM moderation failed for {Id}", saved.Id);
            }
        });
    }

    /// <summary>Lightweight typing indicator for a 1-to-1 thread.</summary>
    public async Task SendDmTyping(string recipientId)
    {
        if (!Guid.TryParse(recipientId, out _)) return;
        var senderId = JwtService.GetUserId(Context.User!).ToString();
        var senderName = JwtService.GetUsername(Context.User!);
        var convId = Infrastructure.Persistence.MongoDB.MongoService.ConversationIdFor(senderId, recipientId);
        await Clients.User(recipientId).SendAsync("DmTyping", new
        {
            conversationId = convId,
            senderId,
            senderName,
        });
    }

    /// <summary>Mark conversation read + notify the other side.</summary>
    public async Task MarkDmRead(string otherUserId)
    {
        if (!Guid.TryParse(otherUserId, out _)) return;
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var convId = Infrastructure.Persistence.MongoDB.MongoService.ConversationIdFor(meId, otherUserId);
        var n = await _mongo.MarkDmConversationReadAsync(convId, meId);
        if (n > 0)
        {
            await Clients.User(otherUserId).SendAsync("DmRead", new
            {
                conversationId = convId,
                readerId = meId,
            });
        }
    }

    // ── 🎲 AUTO-MATCHING LOGIC ──

    // ── 🎲 AUTO-MATCHING LOGIC (सुधरा हुआ) ──
    public async Task StartAutoMatch()
    {
        var currentUserId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(currentUserId)) return;

        // अगर यूज़र पहले से Queue में है, तो उसे दोबारा मत डालो
        if (_waitingUsers.Contains(currentUserId)) return;

        if (_waitingUsers.TryDequeue(out var partnerUserId))
        {
            var randomRoomId = Guid.NewGuid().ToString();

            await Clients.User(currentUserId).SendAsync("MatchFound", randomRoomId, partnerUserId, true);
            await Clients.User(partnerUserId).SendAsync("MatchFound", randomRoomId, currentUserId, false);
        }
        else
        {
            _waitingUsers.Enqueue(currentUserId);
            await Clients.Caller.SendAsync("WaitingForMatch");
        }
    }

    public async Task CancelMatch()
    {
        var currentUserId = Context.UserIdentifier;
        // ConcurrentQueue से रिमूव करने के लिए एक नई List बनाओ (सिर्फ cancellation के वक्त)
        var newQueue = new ConcurrentQueue<string>(_waitingUsers.Where(u => u != currentUserId));
        _waitingUsers.Clear();
        foreach (var user in newQueue) _waitingUsers.Enqueue(user);

        await Clients.Caller.SendAsync("MatchCancelled");
    }
    // ── 📞 WEBRTC SIGNALING METHODS ──

    // 1. Offer भेजना
    public async Task SendWebRTCOffer(string partnerId, string sdp)
    {
        await Clients.User(partnerId).SendAsync("ReceiveOffer", Context.UserIdentifier, sdp);
    }

    // 2. Answer भेजना
    public async Task SendWebRTCAnswer(string partnerId, string sdp)
    {
        await Clients.User(partnerId).SendAsync("ReceiveAnswer", Context.UserIdentifier, sdp);
    }

    // 3. ICE Candidates (नेटवर्क का रास्ता) भेजना
    public async Task SendIceCandidate(string partnerId, string candidate)
    {
        await Clients.User(partnerId).SendAsync("ReceiveIceCandidate", Context.UserIdentifier, candidate);
    }

    // 4. जब कोई "Skip" या "Disconnect" दबाए
    public async Task EndMatch(string partnerId)
    {
        await Clients.User(partnerId).SendAsync("PartnerLeft");
    }

    // ============================================================
    //  DIRECT INVITE CALLING
    //  - Caller invokes InviteToCall(targetUserId, message)
    //  - Target receives "IncomingCall" event with a per-invite id
    //  - Target invokes AcceptCall(inviteId) or DeclineCall(inviteId)
    //  - On accept, both sides receive "CallAccepted" with a shared
    //    livekit room name — they then fetch a LiveKit token from
    //    POST /api/direct-call/token?roomName=...
    //  This pairs with the LiveKit-based 2-person call flow rather than
    //  raw WebRTC P2P. Two reasons: (1) consistent infra with group
    //  calls, (2) NAT traversal handled by LiveKit's TURN.
    // ============================================================

    public async Task InviteToCall(string targetUserId, string? message)
    {
        var callerId = JwtService.GetUserId(Context.User!).ToString();
        var callerName = JwtService.GetUsername(Context.User!);

        if (callerId == targetUserId)
        {
            await Clients.Caller.SendAsync("Error", "You cannot call yourself");
            return;
        }

        // Per-invite handle so accept/decline reference the same call.
        var inviteId = Guid.NewGuid().ToString("N")[..12];
        var roomName = $"dc-{inviteId}";

        // 180s TTL — invite auto-expires if no response. 60s was too tight:
        // a cold Render container takes 30-60s to wake up, leaving little
        // headroom for the recipient to actually click Accept before the
        // invite expires. The IncomingCallModal already shows a 60-second
        // visual countdown to the user (still the soft UX limit), but the
        // backend gives an extra 2 minutes of grace so a slow accept
        // doesn't fail with "expired_or_invalid".
        await _redis.SetStringAsync(
            $"directcall:invite:{inviteId}",
            $"{callerId}:{targetUserId}:{roomName}",
            TimeSpan.FromSeconds(180));

        await Clients.User(targetUserId).SendAsync("IncomingCall", new
        {
            inviteId,
            callerId,
            callerName,
            message,
            roomName,
            expiresInSeconds = 60
        });

        await Clients.Caller.SendAsync("CallInviteSent", new { inviteId, targetUserId, roomName });
    }

    public async Task AcceptCall(string inviteId)
    {
        var accepterId = JwtService.GetUserId(Context.User!).ToString();

        var stored = await _redis.GetStringAsync($"directcall:invite:{inviteId}");
        if (stored == null)
        {
            await Clients.Caller.SendAsync("CallError", new
            {
                inviteId,
                reason = "expired_or_invalid"
            });
            return;
        }

        var parts = stored.Split(':');
        if (parts.Length != 3)
        {
            await Clients.Caller.SendAsync("CallError", new { inviteId, reason = "malformed" });
            return;
        }
        var callerId = parts[0];
        var targetId = parts[1];
        var roomName = parts[2];

        if (accepterId != targetId)
        {
            await Clients.Caller.SendAsync("CallError", new { inviteId, reason = "not_invited" });
            return;
        }

        // Burn the invite — single use.
        await _redis.DeleteKeyAsync($"directcall:invite:{inviteId}");

        // Notify both sides with the shared room name. Each side
        // fetches its own LiveKit token via /api/direct-call/token.
        await Clients.User(callerId).SendAsync("CallAccepted", new { inviteId, roomName });
        await Clients.User(targetId).SendAsync("CallAccepted", new { inviteId, roomName });
    }

    public async Task DeclineCall(string inviteId)
    {
        var declinerId = JwtService.GetUserId(Context.User!).ToString();

        var stored = await _redis.GetStringAsync($"directcall:invite:{inviteId}");
        if (stored == null) return; // already expired

        var parts = stored.Split(':');
        if (parts.Length != 3) return;
        var callerId = parts[0];

        await _redis.DeleteKeyAsync($"directcall:invite:{inviteId}");
        await Clients.User(callerId).SendAsync("CallDeclined", new { inviteId, declinerId });
    }

    // ============================================================
    //  ROLLING QUIZ — #general's always-on quiz round
    //
    //  Driven by the RollingQuizService BackgroundService. This RPC
    //  only accepts user submissions and tallies them; the question
    //  push + reveal happen entirely server-side on the service's
    //  cadence. Method name "SubmitRollingQuizAnswer" matches what
    //  useChatHub.submitRollingQuizAnswer invokes.
    // ============================================================

    private static readonly JsonSerializerOptions _rqJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task SubmitRollingQuizAnswer(string questionId, int choiceIndex)
    {
        var userId = JwtService.GetUserId(Context.User!).ToString();
        var username = JwtService.GetUsername(Context.User!);

        // 1) Load the current question. If id mismatches or the
        //    deadline has already passed, reject cleanly.
        var rawCurrent = await _redis.GetStringAsync(RedisKeys.RollingQuizCurrent);
        if (string.IsNullOrEmpty(rawCurrent))
        {
            await Clients.Caller.SendAsync("RollingQuizAck",
                new { accepted = false, reason = "No live question." });
            return;
        }

        var current = JsonSerializer.Deserialize<RollingQuizService.RollingQuizState>(rawCurrent, _rqJson);
        if (current is null || current.Id != questionId)
        {
            await Clients.Caller.SendAsync("RollingQuizAck",
                new { accepted = false, reason = "Stale question." });
            return;
        }
        if (DateTime.UtcNow > current.DeadlineUtc)
        {
            await Clients.Caller.SendAsync("RollingQuizAck",
                new { accepted = false, reason = "Deadline passed." });
            return;
        }
        if (choiceIndex < 0 || choiceIndex >= current.Options.Count)
        {
            await Clients.Caller.SendAsync("RollingQuizAck",
                new { accepted = false, reason = "Choice out of range." });
            return;
        }

        var subsKey = RedisKeys.RollingQuizSubmissions(current.Id);
        var orderKey = RedisKeys.RollingQuizCorrectOrder(current.Id);
        var statsKey = RedisKeys.RollingQuizStats(current.SessionId);

        // 2) Reject duplicates so users can't spam-tap to hunt for
        //    correctness. ONE submission per question per user.
        var rawSubs = await _redis.GetStringAsync(subsKey);
        var subs = string.IsNullOrEmpty(rawSubs)
            ? new Dictionary<string, RollingQuizService.RollingQuizSubmission>()
            : JsonSerializer.Deserialize<Dictionary<string, RollingQuizService.RollingQuizSubmission>>(rawSubs, _rqJson)
              ?? new();
        if (subs.ContainsKey(userId))
        {
            await Clients.Caller.SendAsync("RollingQuizAck",
                new { accepted = false, reason = "Already answered." });
            return;
        }

        var isCorrect = choiceIndex == current.CorrectIndex;
        subs[userId] = new RollingQuizService.RollingQuizSubmission
        {
            ChoiceIndex = choiceIndex,
            AtTicks = DateTime.UtcNow.Ticks,
            IsCorrect = isCorrect,
        };
        await _redis.SetStringAsync(subsKey, JsonSerializer.Serialize(subs, _rqJson),
            TimeSpan.FromMinutes(10));

        // 3) Personal ack — let the UI lock in the choice instantly.
        await Clients.Caller.SendAsync("RollingQuizAck",
            new { accepted = true, isCorrect, choiceIndex });

        // 4) If correct, append to the rank-order list and award
        //    rank-based points. Append is read-modify-write rather
        //    than RPUSH because we don't expose list ops via
        //    RedisService; the contention window per question is
        //    tiny (a single round = at most a few hundred subs).
        if (isCorrect)
        {
            var rawOrder = await _redis.GetStringAsync(orderKey);
            var order = string.IsNullOrEmpty(rawOrder)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(rawOrder) ?? new();

            if (!order.Contains(userId))
            {
                order.Add(userId);
                await _redis.SetStringAsync(orderKey, JsonSerializer.Serialize(order),
                    TimeSpan.FromMinutes(10));
            }

            var rank = order.IndexOf(userId) + 1;
            var points = rank switch
            {
                1 => 100,
                2 => 70,
                3 => 50,
                _ => 30,
            };

            // 5) Bump per-day leaderboard stats hash.
            var rawStats = await _redis.GetStringAsync(statsKey);
            var stats = string.IsNullOrEmpty(rawStats)
                ? new Dictionary<string, RollingQuizService.RollingQuizUserStats>()
                : JsonSerializer.Deserialize<Dictionary<string, RollingQuizService.RollingQuizUserStats>>(rawStats, _rqJson)
                  ?? new();

            if (!stats.TryGetValue(userId, out var entry))
            {
                entry = new RollingQuizService.RollingQuizUserStats
                {
                    UserId = userId,
                    Username = username,
                    Score = 0,
                    Correct = 0,
                    Attempts = 0,
                };
                stats[userId] = entry;
            }
            entry.Username = username; // update display name if changed
            entry.Score += points;
            entry.Correct += 1;
            entry.Attempts += 1;
            await _redis.SetStringAsync(statsKey, JsonSerializer.Serialize(stats, _rqJson),
                TimeSpan.FromDays(2));

            // 6) Broadcast the scoring event so the room can render
            //    "🥇 Alice +100" notifications. Includes the running
            //    total so we don't need a separate leaderboard fetch.
            await Clients.Group("general").SendAsync("RollingQuizScored",
                new RollingQuizScored(userId, username, rank, points, entry.Score));
        }
        else
        {
            // Wrong answers still count toward attempts so accuracy
            // can be displayed honestly on the leaderboard later.
            var rawStats = await _redis.GetStringAsync(statsKey);
            var stats = string.IsNullOrEmpty(rawStats)
                ? new Dictionary<string, RollingQuizService.RollingQuizUserStats>()
                : JsonSerializer.Deserialize<Dictionary<string, RollingQuizService.RollingQuizUserStats>>(rawStats, _rqJson)
                  ?? new();

            if (!stats.TryGetValue(userId, out var entry))
            {
                entry = new RollingQuizService.RollingQuizUserStats
                {
                    UserId = userId,
                    Username = username,
                };
                stats[userId] = entry;
            }
            entry.Username = username;
            entry.Attempts += 1;
            await _redis.SetStringAsync(statsKey, JsonSerializer.Serialize(stats, _rqJson),
                TimeSpan.FromDays(2));
        }
    }

    /// <summary>
    /// Called by the client on entering #general so the panel
    /// can render the current question + today's leaderboard
    /// without waiting for the next 4-min tick.
    /// </summary>
    public async Task GetRollingQuizState()
    {
        var rawCurrent = await _redis.GetStringAsync(RedisKeys.RollingQuizCurrent);
        if (!string.IsNullOrEmpty(rawCurrent))
        {
            var st = JsonSerializer.Deserialize<RollingQuizService.RollingQuizState>(rawCurrent, _rqJson);
            if (st is not null && DateTime.UtcNow < st.DeadlineUtc)
            {
                await Clients.Caller.SendAsync("RollingQuizQuestion",
                    new RollingQuizQuestion(
                        Id: st.Id,
                        Category: st.Category,
                        Difficulty: st.Difficulty,
                        Question: st.Question,
                        Options: st.Options,
                        DeadlineUtc: st.DeadlineUtc,
                        SessionId: st.SessionId));
            }
        }

        var sessionId = RollingQuizService.SessionIdForUtc(DateTime.UtcNow);
        var rawStats = await _redis.GetStringAsync(RedisKeys.RollingQuizStats(sessionId));
        var stats = string.IsNullOrEmpty(rawStats)
            ? new Dictionary<string, RollingQuizService.RollingQuizUserStats>()
            : JsonSerializer.Deserialize<Dictionary<string, RollingQuizService.RollingQuizUserStats>>(rawStats, _rqJson)
              ?? new();

        var top = stats.Values
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Correct)
            .Take(10)
            .Select(s => new RollingQuizLeaderEntry(
                s.UserId, s.Username, s.Score, s.Correct, s.Attempts))
            .ToList();

        var youUserId = JwtService.GetUserId(Context.User!).ToString();
        var youRow = stats.TryGetValue(youUserId, out var you)
            ? new RollingQuizLeaderEntry(you.UserId, you.Username, you.Score, you.Correct, you.Attempts)
            : null;

        await Clients.Caller.SendAsync("RollingQuizLeaderboard",
            new RollingQuizLeaderboard(sessionId, top, youRow));
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