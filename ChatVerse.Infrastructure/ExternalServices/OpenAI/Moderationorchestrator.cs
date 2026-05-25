using ChatVerse.Domain.Constants;
using ChatVerse.Domain.Entities;
using ChatVerse.Domain.Enums;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ChatVerse.Infrastructure.ExternalServices.OpenAI;

/// <summary>
/// Moderation orchestrator — called from ChatHub after message broadcast.
/// Flow:
///   1. Message delivered to room instantly (no delay)
///   2. OpenAI checks content async
///   3. Flagged → update MongoDB + trust delta
///   4. Blocked → notify room to hide message
/// </summary>
public class ModerationOrchestrator
{
    private readonly OpenAIModerationService _openAI;
    private readonly MongoService _mongo;
    private readonly PostgresProcService _postgres;
    private readonly ILogger<ModerationOrchestrator> _logger;

    public ModerationOrchestrator(
        OpenAIModerationService openAI,
        MongoService mongo,
        PostgresProcService postgres,
        ILogger<ModerationOrchestrator> logger)
    {
        _openAI = openAI;
        _mongo = mongo;
        _postgres = postgres;
        _logger = logger;
    }

    // ============================================================
    //  ModerateMessageAsync
    //  Fire-and-forget from ChatHub — never blocks delivery
    // ============================================================
    public async Task ModerateMessageAsync(
        string messageId,
        string roomId,
        string senderId,
        string content,
        IClientProxy roomClients)
    {
        try
        {
            // Call OpenAI
            var result = await _openAI.CheckTextAsync(content);

            // Update MongoDB moderation status
            await _mongo.UpdateModerationStatusAsync(
                messageId,
                result.Action,
                result.FlagReason,
                result.IsFlagged ? result.Confidence : null
            );

            if (!result.IsFlagged)
            {
                _logger.LogDebug("Message {MessageId} passed moderation", messageId);
                return;
            }

            // Save moderation log to MongoDB
            await _mongo.InsertModerationLogAsync(new ModerationLog
            {
                MessageId = messageId,
                RoomId = roomId,
                SenderId = senderId,
                OriginalContent = content,
                Action = result.Action,
                Source = "openai",
                OpenaiResponse = result.RawResponse,
                TrustDelta = result.TrustDelta,
                CreatedAt = DateTime.UtcNow
            });

            // Apply trust delta to sender in PostgreSQL
            if (result.TrustDelta != 0)
            {
                var eventType = result.Action == "blocked"
                    ? TrustEventType.MsgBlocked
                    : TrustEventType.MsgFlagged;

                await _postgres.ApplyTrustEventAsync(
                    userId: Guid.Parse(senderId),
                    eventType: eventType,
                    delta: (short)result.TrustDelta,
                    reason: $"Message {result.Action}: {result.FlagReason}",
                    refSource: "mongo_messages"
                );
            }

            // Notify room based on action
            if (result.Action == "blocked")
            {
                // Hide message from room
                await roomClients.SendAsync("MessageBlocked", new
                {
                    messageId,
                    reason = "Message removed by moderation"
                });

                _logger.LogWarning(
                    "Message {MessageId} BLOCKED — room: {Room}, sender: {Sender}, reason: {Reason}",
                    messageId, roomId, senderId, result.FlagReason);
            }
            else
            {
                // Flag message visually — stays visible
                await roomClients.SendAsync("MessageFlagged", new
                {
                    messageId,
                    reason = result.FlagReason
                });

                _logger.LogInformation(
                    "Message {MessageId} FLAGGED — confidence: {Confidence:P0}, reason: {Reason}",
                    messageId, result.Confidence, result.FlagReason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Moderation failed for message {MessageId} — defaulting to clean", messageId);

            // On any error — mark clean so message stays visible
            try
            {
                await _mongo.UpdateModerationStatusAsync(messageId, "clean", null, null);
            }
            catch { /* ignore secondary failure */ }
        }
    }
}