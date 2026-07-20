using HeatSynQ.Platform.Infrastructure.Persistence;
using HeatSynQ.Platform.Infrastructure.Work;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeatSynQ.Platform.Infrastructure.Tests.Work;

public sealed class PlatformOutboxProcessorTests
{
    [Fact]
    public async Task Processor_completes_notification_and_print_messages_idempotently()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase($"outbox-{Guid.NewGuid()}")
            .Options;
        var factory = new TestDbContextFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Outbox.AddRange(
                NewMessage(
                    "platform.notification",
                    """{"recipient":"quality","subject":"Hold","body":"Job is held."}""",
                    "notify-hold-1"),
                NewMessage(
                    "platform.print",
                    """{"printer":"Shipping","documentPath":"issued/shipper-1.pdf","copies":2}""",
                    "print-shipper-1"));
            await db.SaveChangesAsync();
        }
        var processor = new PlatformOutboxProcessor(
            factory,
            TimeProvider.System,
            NullLogger<PlatformOutboxProcessor>.Instance);

        Assert.Equal(2, await processor.ProcessBatchAsync());
        Assert.Equal(0, await processor.ProcessBatchAsync());

        await using var assertionDb = await factory.CreateDbContextAsync();
        Assert.Single(assertionDb.Notifications);
        Assert.Single(assertionDb.PrintJobs);
        Assert.All(assertionDb.Outbox, x => Assert.NotNull(x.CompletedAt));
    }

    [Fact]
    public async Task Processor_records_retry_for_unknown_message_type()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase($"outbox-{Guid.NewGuid()}")
            .Options;
        var factory = new TestDbContextFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Outbox.Add(NewMessage("unknown", "{}", "unknown-1"));
            await db.SaveChangesAsync();
        }
        var processor = new PlatformOutboxProcessor(
            factory,
            TimeProvider.System,
            NullLogger<PlatformOutboxProcessor>.Instance);

        Assert.Equal(0, await processor.ProcessBatchAsync());

        await using var assertionDb = await factory.CreateDbContextAsync();
        var message = Assert.Single(assertionDb.Outbox);
        Assert.Equal(1, message.AttemptCount);
        Assert.NotNull(message.LastError);
        Assert.True(message.NextAttemptAt > DateTimeOffset.UtcNow);
    }

    private static OutboxRecord NewMessage(string type, string payload, string key) =>
        new()
        {
            Id = Guid.NewGuid(),
            MessageType = type,
            Payload = payload,
            IdempotencyKey = key,
            OccurredAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow
        };

    private sealed class TestDbContextFactory(
        DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
        public Task<PlatformDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
