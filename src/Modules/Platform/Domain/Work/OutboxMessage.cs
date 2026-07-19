namespace HeatSynQ.Platform.Domain.Work;

public sealed class OutboxMessage
{
    private OutboxMessage(
        string messageType,
        string payload,
        string idempotencyKey,
        DateTimeOffset occurredAt)
    {
        Id = Guid.NewGuid();
        MessageType = messageType;
        Payload = payload;
        IdempotencyKey = idempotencyKey;
        OccurredAt = occurredAt;
        NextAttemptAt = occurredAt;
    }

    public Guid Id { get; }
    public string MessageType { get; }
    public string Payload { get; }
    public string IdempotencyKey { get; }
    public DateTimeOffset OccurredAt { get; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public int AttemptCount { get; private set; }
    public string? LastError { get; private set; }

    public static OutboxMessage Create(
        string messageType,
        string payload,
        string idempotencyKey,
        DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return new OutboxMessage(messageType, payload, idempotencyKey, occurredAt);
    }

    public void MarkCompleted(DateTimeOffset completedAt)
    {
        if (CompletedAt is not null)
        {
            throw new InvalidOperationException("The outbox message is already complete.");
        }

        CompletedAt = completedAt;
        LastError = null;
    }

    public void RecordFailure(string error, DateTimeOffset attemptedAt, TimeSpan retryDelay)
    {
        if (CompletedAt is not null)
        {
            throw new InvalidOperationException("A completed outbox message cannot be retried.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        AttemptCount++;
        LastError = error.Trim();
        NextAttemptAt = attemptedAt.Add(retryDelay);
    }
}
