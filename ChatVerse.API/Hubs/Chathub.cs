using ChatVerse.API.Extensions;
using ChatVerse.API.Models.Games;
using ChatVerse.API.Services;
using ChatVerse.Domain.Constants;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.ExternalServices.OpenAI;
using ChatVerse.Infrastructure.ExternalServices.Spotify;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using ChatVerse.Infrastructure.Persistence.Redis;
using ChatVerse.Infrastructure.Services.UserState;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly UserStateService _userState;
    private readonly SpotifyOEmbedService _spotifyOEmbed;
    private readonly ILogger<ChatHub> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public ChatHub(
        MongoService mongo,
        RedisService redis,
        PostgresProcService postgres,
        ModerationOrchestrator moderation,
        UserStateService userState,
        SpotifyOEmbedService spotifyOEmbed,
        ILogger<ChatHub> logger,
        IServiceScopeFactory scopeFactory)
    {
        _mongo = mongo;
        _redis = redis;
        _postgres = postgres;
        _moderation = moderation;
        _userState = userState;
        _spotifyOEmbed = spotifyOEmbed;
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

        // Distributed cleanup — works across multiple API instances.
        // Replaces the static ConcurrentQueue that broke under horizontal scale.
        await _redis.RemoveFromChatMatchQueueAsync(userId);
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
        var userGuid = JwtService.GetUserId(Context.User!);
        var userId = userGuid.ToString();
        var username = JwtService.GetUsername(Context.User!);

        // Fresh trust + age — picks up bans / verifications within cache
        // TTL (~30s) instead of waiting for the 24h JWT to expire.
        var trustScore = await _userState.GetTrustScoreAsync(userGuid);
        var ageVerified = await _userState.GetAgeVerifiedAsync(userGuid);

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
        var userGuid = JwtService.GetUserId(Context.User!);
        var userId = userGuid.ToString();
        var username = JwtService.GetUsername(Context.User!);

        // Fresh trust score — penalised users can't keep posting just
        // because their JWT is still warm.
        var trustScore = await _userState.GetTrustScoreAsync(userGuid);

        // ── 1. EPHEMERAL IMAGE (VANISH MODE) LOGIC ──
        if (type == "ephemeral_image")
        {
            // Trust gate — keeps brand-new / penalised accounts out of the
            // vanish path entirely. One of the easier abuse vectors to seal.
            if (trustScore < EphemeralImage.MinTrustScore)
            {
                await Clients.Caller.SendAsync("Error",
                    "Your trust score is too low to send ephemeral images.");
                return;
            }

            // Defence-in-depth payload guard. SignalR already caps the
            // transport message, but base64 data URLs blow up fast and
            // this is the cleanest place to reject oversized uploads.
            if (!string.IsNullOrEmpty(mediaUrl) && mediaUrl.Length > EphemeralImage.MaxBase64Bytes)
            {
                await Clients.Caller.SendAsync("Error", "Image too large.");
                return;
            }

            var tempMsgId = Guid.NewGuid().ToString("N")[..12];

            // Server NEVER trusts a client-supplied moderation status.
            // Until vision moderation is wired up, we broadcast as
            // "pending" so clients can blur/queue rather than treating
            // an attacker-set "clean" flag as the truth.
            await Clients.Group(roomSlug).SendAsync("ReceiveMessage", new
            {
                id = tempMsgId,
                roomId = roomSlug,
                senderId = userId,
                senderName = username,
                senderAvatar = (string?)null,
                content = content ?? "sent a photo",
                type = type,
                mediaUrl = mediaUrl,
                replyTo = replyToId,
                reactions = new Dictionary<string, List<string>>(),
                modStatus = "pending",
                createdAt = DateTime.UtcNow
            });

            // Audit trail — image bytes stay ephemeral but we keep the
            // who/where/when so abuse reports can be correlated even
            // after the photo has vanished from the room.
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var mongo = scope.ServiceProvider.GetRequiredService<MongoService>();
                    await mongo.InsertModerationLogAsync(new ModerationLog
                    {
                        // ObjectId required by the moderation_logs schema —
                        // ephemeral messages don't have one, so we mint one.
                        MessageId = MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
                        RoomId = roomSlug,
                        SenderId = userId,
                        OriginalContent = $"[ephemeral_image: {content ?? "(no caption)"}]",
                        Action = "pending",
                        Source = "audit_ephemeral",
                        CreatedAt = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Ephemeral audit log failed");
                }
            });

            _logger.LogInformation("Ephemeral image sent by {Username} in {Room}", username, roomSlug);
            return;
        }

        // ── 2. NORMAL TEXT MESSAGE LOGIC ──
        if (string.IsNullOrWhiteSpace(content) || content.Length > 2000)
        {
            await Clients.Caller.SendAsync("Error", "Invalid message content");
            return;
        }

        // Server-side Spotify link detection. If the message body contains
        // a recognisable open.spotify.com / spotify: URL we attach a ready-
        // to-iframe embed URL to the persisted message. The frontend just
        // renders `message.spotify.embedUrl` in an iframe when present.
        var spotifyEmbed = SpotifyLinkExtractor.Extract(content);
        // Best-effort oEmbed enrichment so the panel shows a real title
        // + cover instead of generic "Spotify track". Cache hits in
        // SpotifyOEmbedService make this nearly free on repeat shares.
        if (spotifyEmbed != null)
        {
            var (title, thumb) = await _spotifyOEmbed.FetchAsync(spotifyEmbed.WebUrl);
            spotifyEmbed.Title = title;
            spotifyEmbed.ThumbnailUrl = thumb;
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
            Spotify = spotifyEmbed,
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

                // Step 2: Trust score warning — fresh scope, fresh cached score
                using var scope = _scopeFactory.CreateScope();
                var scopedUserState = scope.ServiceProvider
                    .GetRequiredService<UserStateService>();

                var newScore = await scopedUserState.GetTrustScoreAsync(userIdParsed);
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

        // Persist the toggle so reactions survive a reload and the Music
        // Lounge panel can show counts that match the chat bubble. Toggle
        // semantics match Slack/Discord — second click from the same user
        // removes their reaction.
        Dictionary<string, List<string>> updated;
        try
        {
            updated = await _mongo.ToggleReactionAsync(messageId, emoji, userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persist reaction failed — broadcasting transient state");
            updated = new Dictionary<string, List<string>> { [emoji] = new() { userId } };
        }

        // Broadcast the authoritative server state so every client renders
        // the same counts. Older clients that only listen for the legacy
        // single-user payload still get the messageId+emoji+userId fields.
        await Clients.Group(roomSlug).SendAsync("MessageReaction", new
        {
            messageId,
            emoji,
            userId,
            reactions = updated,
        });
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

        // Mirror room messages: detect Spotify links server-side so DM
        // payloads and history both carry a ready-to-iframe embed URL.
        var dmSpotify = SpotifyLinkExtractor.Extract(content);
        if (dmSpotify != null)
        {
            var (title, thumb) = await _spotifyOEmbed.FetchAsync(dmSpotify.WebUrl);
            dmSpotify.Title = title;
            dmSpotify.ThumbnailUrl = thumb;
        }

        var dm = new DmMessage
        {
            ConversationId = convId,
            SenderId       = senderId,
            SenderName     = senderName,
            RecipientId    = recipientId,
            Content        = content.Trim(),
            Type           = "text",
            Spotify        = dmSpotify,
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
            spotify       = saved.Spotify == null ? null : new
            {
                kind      = saved.Spotify.Kind,
                spotifyId = saved.Spotify.SpotifyId,
                embedUrl  = saved.Spotify.EmbedUrl,
                webUrl    = saved.Spotify.WebUrl,
                title     = saved.Spotify.Title,
                thumbnailUrl = saved.Spotify.ThumbnailUrl,
            },
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
        // Redis-backed match queue — safe across multiple API replicas.
        // Replaces the legacy static ConcurrentQueue which silently
        // failed to pair users that landed on different instances behind
        // a load balancer.
        if (await _redis.IsInChatMatchQueueAsync(currentUserId))
        {
            await Clients.Caller.SendAsync("WaitingForMatch");
            return;
        }

        var partnerUserId = await _redis.DequeueForChatMatchAsync();
        if (partnerUserId != null && partnerUserId != currentUserId)
        {
            var randomRoomId = Guid.NewGuid().ToString();

            // Register session participants so VideoHub partner lookups
            // resolve identically for chat-hub matches and the dedicated
            // video-hub flow.
            await _redis.SetStringAsync(
                $"video:session:{randomRoomId}",
                $"{currentUserId},{partnerUserId}",
                TimeSpan.FromHours(4));

            await Clients.User(currentUserId).SendAsync("MatchFound", randomRoomId, partnerUserId, true);
            await Clients.User(partnerUserId).SendAsync("MatchFound", randomRoomId, currentUserId, false);
        }
        else
        {
            await _redis.EnqueueForChatMatchAsync(currentUserId);
            await Clients.Caller.SendAsync("WaitingForMatch");
        }
    }

    public async Task CancelMatch()
    {
        var currentUserId = Context.UserIdentifier;
        // ConcurrentQueue से रिमूव करने के लिए एक नई List बनाओ (सिर्फ cancellation के वक्त)
        if (!string.IsNullOrEmpty(currentUserId))
            await _redis.RemoveFromChatMatchQueueAsync(currentUserId);
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
    //  LIVE VOICE CAPTIONS + TRANSLATION
    //
    //  Architecture (Phase A — captions only, no TTS):
    //   1. Each participant's browser runs Web Speech API locally to
    //      transcribe their OWN microphone — zero audio uploads.
    //   2. As soon as a phrase is recognised (interim or final), the
    //      speaker invokes BroadcastCaption(roomName, text, sourceLang, isFinal).
    //   3. We fan out "IncomingCaption" to everyone else in a SignalR
    //      group whose name mirrors the LiveKit room (the voice rooms
    //      live in LiveKit, but SignalR carries the side-channel text).
    //   4. Each receiver's frontend POSTs to /api/translate if the
    //      source language differs from their preferred language —
    //      with Redis caching, repeated phrases ("haan", "okay")
    //      translate exactly once across the whole platform.
    //
    //  Why a SignalR group instead of Clients.User(id) per listener?
    //   • A 4-person group call means 3 fanouts per phrase. Group
    //     send is a single server-side broadcast — much cheaper.
    //   • Participants come and go; group membership auto-cleans
    //     on disconnect, no manual bookkeeping.
    //
    //  The group naming is `caption:{liveKitRoomName}` so even if a
    //  user has multiple tabs in different rooms, captions stay
    //  scoped to the room they belong to.
    // ============================================================

    private static string CaptionGroupFor(string roomName) => $"caption:{roomName}";

    public async Task JoinCaptionRoom(string roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return;
        await Groups.AddToGroupAsync(Context.ConnectionId, CaptionGroupFor(roomName));
    }

    public async Task LeaveCaptionRoom(string roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, CaptionGroupFor(roomName));
    }

    /// <summary>
    /// Fire-and-forget broadcast of a transcribed phrase to every other
    /// participant in the same caption group. Sent as both interim and
    /// final passes so receivers can render a "typing-in-progress" line
    /// before the final replaces it.
    /// </summary>
    public async Task BroadcastCaption(string roomName, string text, string sourceLang, bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(roomName) || string.IsNullOrWhiteSpace(text)) return;
        // Defensive cap — Web Speech occasionally returns a runaway
        // string when audio dropouts confuse the recogniser.
        if (text.Length > 1000) text = text[..1000];

        var speakerId = JwtService.GetUserId(Context.User!).ToString();
        var speakerName = JwtService.GetUsername(Context.User!);

        await Clients.GroupExcept(CaptionGroupFor(roomName), Context.ConnectionId)
            .SendAsync("IncomingCaption", new
            {
                speakerId,
                speakerName,
                text,
                sourceLang = string.IsNullOrWhiteSpace(sourceLang) ? "en" : sourceLang.ToLowerInvariant(),
                isFinal,
                at = DateTime.UtcNow,
            });
    }

    // ============================================================
    //  THEATER URL SYNC
    //
    //  Replaces the old screen-share theater. The host picks a URL
    //  (YouTube, Vimeo, Twitch, direct video, or any iframe-friendly
    //  source) and we broadcast it to everyone else in the same
    //  theater room. Each viewer loads the URL in their OWN iframe —
    //  no pixel streaming, no server-side browser, just URL fanout.
    //
    //  Late-joiner support: we also stash the latest URL in Redis
    //  keyed by `theater:url:{roomName}` (15 min TTL) so anyone who
    //  joins after the host already picked a video gets it on join.
    //
    //  Trust model: the theater room is private and invite-only. Per
    //  product spec, no moderation runs here — the host who picked
    //  the URL is responsible for whatever appears in the iframe.
    //  We DO keep the disclaimer banner on the client so users know.
    // ============================================================

    private static string TheaterGroupFor(string roomName) => $"theater:{roomName}";
    private static string TheaterStateCacheKey(string roomName) => $"theater:state:{roomName}";

    // Mode names the client + server agree on.
    //   "cobrowse"    → URL-sync iframe path (YouTube / direct video etc.)
    //   "screenshare" → LiveKit screen-share path (Netflix-style sites)
    private const string TheaterModeCobrowse = "cobrowse";
    private const string TheaterModeScreenShare = "screenshare";

    public async Task JoinTheaterRoom(string roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return;
        await Groups.AddToGroupAsync(Context.ConnectionId, TheaterGroupFor(roomName));

        // Replay the current state (if any) so a joiner sees whatever
        // mode + URL the host already picked, instead of dropping into
        // a blank room.
        var json = await _redis.GetStringAsync(TheaterStateCacheKey(roomName));
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var state = JsonSerializer.Deserialize<TheaterStateDto>(json);
                if (state != null)
                {
                    await Clients.Caller.SendAsync("TheaterStateChanged", new
                    {
                        mode = state.Mode,
                        url = state.Url,
                        hostId = (string?)null,
                        hostName = state.HostName,
                        at = DateTime.UtcNow,
                        replay = true,
                    });
                }
            }
            catch
            {
                // Bad cache entry — drop it so next write can repopulate.
                await _redis.DeleteKeyAsync(TheaterStateCacheKey(roomName));
            }
        }
    }

    public async Task LeaveTheaterRoom(string roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, TheaterGroupFor(roomName));
    }

    /// <summary>
    /// Single broadcast for both mode and URL — keeps the wire model
    /// simple and means viewers can never observe an inconsistent state
    /// (mode says iframe but url is null, etc.). For screen-share mode
    /// the url is ignored (LiveKit carries the video itself).
    /// </summary>
    public async Task BroadcastTheaterState(string roomName, string mode, string? url)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return;

        var normalisedMode = (mode ?? "").Trim().ToLowerInvariant();
        if (normalisedMode != TheaterModeCobrowse && normalisedMode != TheaterModeScreenShare) return;

        // URL only matters in co-browse mode; trim it down in screen-share.
        string? normalisedUrl = null;
        if (normalisedMode == TheaterModeCobrowse && !string.IsNullOrWhiteSpace(url))
        {
            if (url.Length > 2048) return;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            normalisedUrl = url;
        }

        var hostId = JwtService.GetUserId(Context.User!).ToString();
        var hostName = JwtService.GetUsername(Context.User!);

        // Persist for late joiners — 15 minutes covers a typical movie
        // before the host updates it.
        var stateJson = JsonSerializer.Serialize(new TheaterStateDto
        {
            Mode = normalisedMode,
            Url = normalisedUrl,
            HostName = hostName,
        });
        await _redis.SetStringAsync(TheaterStateCacheKey(roomName), stateJson, TimeSpan.FromMinutes(15));

        await Clients.Group(TheaterGroupFor(roomName))
            .SendAsync("TheaterStateChanged", new
            {
                mode = normalisedMode,
                url = normalisedUrl,
                hostId,
                hostName,
                at = DateTime.UtcNow,
                replay = false,
            });
    }

    /// <summary>
    /// Compact DTO for the Redis-cached theater state. Kept private to
    /// the hub — no consumer outside this file deserialises this shape.
    /// </summary>
    private sealed class TheaterStateDto
    {
        [JsonPropertyName("mode")] public string Mode { get; set; } = TheaterModeCobrowse;
        [JsonPropertyName("url")]  public string? Url { get; set; }
        [JsonPropertyName("hostName")] public string? HostName { get; set; }
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

        // Block check: if the target has blocked the caller, we don't
        // ring them — only fire a silent "CallAttempt" notification so
        // the target knows someone they blocked tried to reach them.
        // The caller is told "CallInviteSent" anyway (mirroring Instagram-
        // style opaque-block UX — they shouldn't be able to confirm
        // they were blocked).
        var isBlocked = await _mongo.IsBlockedAsync(targetUserId, callerId);
        if (isBlocked)
        {
            await Clients.User(targetUserId).SendAsync("BlockedCallAttempt", new
            {
                callerId,
                callerName,
                attemptedAt = DateTime.UtcNow,
                message,
            });
            // Lie to the caller — same "sent" envelope, but no inviteId
            // that the server would honor on Accept. Their UI shows a
            // ringing state until 60s expires.
            var fakeInviteId = Guid.NewGuid().ToString("N")[..12];
            await Clients.Caller.SendAsync("CallInviteSent", new
            {
                inviteId = fakeInviteId,
                targetUserId,
                roomName = $"dc-{fakeInviteId}",
            });
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
    //  Includes the optional Spotify embed so room history loads
    //  with embeds intact (not just live broadcasts).
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
        spotify = m.Spotify == null ? null : new
        {
            kind = m.Spotify.Kind,
            spotifyId = m.Spotify.SpotifyId,
            embedUrl = m.Spotify.EmbedUrl,
            webUrl = m.Spotify.WebUrl,
            title = m.Spotify.Title,
            thumbnailUrl = m.Spotify.ThumbnailUrl,
        },
        editedAt = m.EditedAt,
        createdAt = m.CreatedAt
    };
}