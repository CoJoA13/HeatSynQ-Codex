using HeatSynQ.Platform.Domain.Work;

namespace HeatSynQ.Platform.Domain.Tests.Work;

public sealed class OutboxMessageTests
{
    [Fact]
    public void Message_can_only_be_completed_once()
    {
        var message = OutboxMessage.Create(
            "platform.user-disabled",
            """{"userId":"abc"}""",
            "user-disabled-abc",
            DateTimeOffset.Parse("2026-07-19T15:00:00-05:00"));

        message.MarkCompleted(DateTimeOffset.Parse("2026-07-19T15:01:00-05:00"));

        Assert.Throws<InvalidOperationException>(
            () => message.MarkCompleted(DateTimeOffset.Parse("2026-07-19T15:02:00-05:00")));
    }

    [Fact]
    public void Failure_is_recorded_and_schedules_a_retry()
    {
        var message = OutboxMessage.Create(
            "platform.report-requested",
            "{}",
            "report-42",
            DateTimeOffset.Parse("2026-07-19T15:00:00-05:00"));

        message.RecordFailure(
            "Printer unavailable",
            DateTimeOffset.Parse("2026-07-19T15:00:10-05:00"),
            TimeSpan.FromMinutes(2));

        Assert.Equal(1, message.AttemptCount);
        Assert.Equal("Printer unavailable", message.LastError);
        Assert.Equal(DateTimeOffset.Parse("2026-07-19T15:02:10-05:00"), message.NextAttemptAt);
    }
}
