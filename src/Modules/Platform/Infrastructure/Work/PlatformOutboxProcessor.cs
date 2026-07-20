using System.Text.Json;
using HeatSynQ.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HeatSynQ.Platform.Infrastructure.Work;

public sealed class PlatformOutboxProcessor(
    IDbContextFactory<PlatformDbContext> dbContextFactory,
    TimeProvider timeProvider,
    ILogger<PlatformOutboxProcessor> logger)
{
    public async Task<int> ProcessBatchAsync(
        int batchSize = 50,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var messages = await db.Outbox
            .Where(x => x.CompletedAt == null && x.NextAttemptAt <= now)
            .OrderBy(x => x.OccurredAt)
            .Take(Math.Clamp(batchSize, 1, 200))
            .ToArrayAsync(cancellationToken);
        var completed = 0;
        foreach (var message in messages)
        {
            try
            {
                await DispatchAsync(message, db, now, cancellationToken);
                message.CompletedAt = now;
                message.LastError = null;
                completed++;
            }
            catch (Exception exception)
            {
                message.AttemptCount++;
                message.LastError = exception.Message[..Math.Min(exception.Message.Length, 2000)];
                var delayMinutes = Math.Min(Math.Pow(2, message.AttemptCount), 60);
                message.NextAttemptAt = now.AddMinutes(delayMinutes);
                logger.LogError(
                    exception,
                    "Outbox message {MessageId} ({MessageType}) failed on attempt {AttemptCount}.",
                    message.Id,
                    message.MessageType,
                    message.AttemptCount);
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        return completed;
    }

    private static async Task DispatchAsync(
        OutboxRecord message,
        PlatformDbContext db,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        switch (message.MessageType)
        {
            case "platform.notification":
                if (!await db.Notifications.AnyAsync(
                        x => x.OutboxMessageId == message.Id,
                        cancellationToken))
                {
                    var payload = JsonSerializer.Deserialize<NotificationPayload>(
                        message.Payload,
                        JsonOptions) ?? throw new InvalidOperationException("Notification payload is invalid.");
                    if (string.IsNullOrWhiteSpace(payload.Recipient) ||
                        string.IsNullOrWhiteSpace(payload.Subject))
                        throw new InvalidOperationException("Notification recipient and subject are required.");
                    db.Notifications.Add(new NotificationRecord
                    {
                        Id = Guid.NewGuid(),
                        OutboxMessageId = message.Id,
                        Recipient = payload.Recipient,
                        Subject = payload.Subject,
                        Body = payload.Body ?? string.Empty,
                        CreatedAt = now
                    });
                }
                break;
            case "platform.print":
                if (!await db.PrintJobs.AnyAsync(
                        x => x.OutboxMessageId == message.Id,
                        cancellationToken))
                {
                    var payload = JsonSerializer.Deserialize<PrintPayload>(
                        message.Payload,
                        JsonOptions) ?? throw new InvalidOperationException("Print payload is invalid.");
                    if (string.IsNullOrWhiteSpace(payload.Printer) ||
                        string.IsNullOrWhiteSpace(payload.DocumentPath) ||
                        payload.Copies is < 1 or > 20)
                        throw new InvalidOperationException("Printer, document path, and 1-20 copies are required.");
                    db.PrintJobs.Add(new PrintJobRecord
                    {
                        Id = Guid.NewGuid(),
                        OutboxMessageId = message.Id,
                        Printer = payload.Printer,
                        DocumentPath = payload.DocumentPath,
                        Copies = payload.Copies,
                        CreatedAt = now
                    });
                }
                break;
            default:
                throw new NotSupportedException($"Unknown outbox message type '{message.MessageType}'.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private sealed record NotificationPayload(string Recipient, string Subject, string? Body);
    private sealed record PrintPayload(string Printer, string DocumentPath, int Copies);
}
