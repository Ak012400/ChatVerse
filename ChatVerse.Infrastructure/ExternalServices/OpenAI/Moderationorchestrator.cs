using ChatVerse.Domain.Entities;
using ChatVerse.Domain.Enums;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using ChatVerse.Infrastructure.Services.UserState;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatVerse.Infrastructure.ExternalServices.OpenAI;

public class ModerationOrchestrator
{
    private readonly OpenAIModerationService _openAI;
    private readonly IServiceScopeFactory _scopeFactory;  // ← fix
    private readonly ILogger<ModerationOrchestrator> _logger;

    public ModerationOrchestrator(
        OpenAIModerationService openAI,
        IServiceScopeFactory scopeFactory,
        ILogger<ModerationOrchestrator> logger)
    {
        _openAI = openAI;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task ModerateMessageAsync(
        string messageId,
        string roomId,
        string senderId,
        string content,
        IClientProxy roomClients)
    {
        try
        {
            var result = await _openAI.CheckTextAsync(content);

            // ── New scope — avoids ObjectDisposedException ────
            using var scope = _scopeFactory.CreateScope();
            var mongo = scope.ServiceProvider.GetRequiredService<MongoService>();
            var postgres = scope.ServiceProvider.GetRequiredService<PostgresProcService>();

            // Update MongoDB moderation status
            await mongo.UpdateModerationStatusAsync(
                messageId,
                result.Action,
                result.FlagReason,
                result.IsFlagged ? result.Confidence : null);

            if (!result.IsFlagged)
            {
                _logger.LogDebug("Message {Id} passed moderation", messageId);
                return;
            }

            // Save moderation log
            await mongo.InsertModerationLogAsync(new ModerationLog
            {
                MessageId = messageId,
                RoomId = roomId,
                SenderId = senderId,
                OriginalContent = content,
                Action = result.Action,
                Source = "groq",
                OpenaiResponse = result.RawResponse,
                TrustDelta = result.TrustDelta,
                CreatedAt = DateTime.UtcNow
            });

            // Apply trust delta via new scope (safe)
            if (result.TrustDelta != 0)
            {
                var eventType = result.Action == "blocked"
                    ? TrustEventType.MsgBlocked
                    : TrustEventType.MsgFlagged;

                await postgres.ApplyTrustEventAsync(
                    userId: Guid.Parse(senderId),
                    eventType: eventType,
                    delta: (short)result.TrustDelta,
                    reason: $"Message {result.Action}: {result.FlagReason}",
                    refSource: "mongo_messages"
                );

                // Invalidate user-state cache so the next gate check
                // (room join / video queue / group call) sees the fresh
                // trust score instead of the stale cached value.
                var userState = scope.ServiceProvider.GetService<UserStateService>();
                if (userState != null)
                    await userState.InvalidateAsync(Guid.Parse(senderId));
            }

            // Notify room
            if (result.Action == "blocked")
            {
                await roomClients.SendAsync("MessageBlocked", new
                {
                    messageId,
                    reason = "Message removed by moderation"
                });
                _logger.LogWarning("Message {Id} BLOCKED — reason: {Reason}",
                    messageId, result.FlagReason);
            }
            else
            {
                await roomClients.SendAsync("MessageFlagged", new
                {
                    messageId,
                    reason = result.FlagReason
                });
                _logger.LogInformation("Message {Id} FLAGGED — confidence: {Conf:P0}",
                    messageId, result.Confidence);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Moderation failed for message {Id} — defaulting to clean", messageId);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var mongo = scope.ServiceProvider.GetRequiredService<MongoService>();
                await mongo.UpdateModerationStatusAsync(messageId, "clean", null, null);
            }
            catch { /* ignore secondary failure */ }
        }
    }
}