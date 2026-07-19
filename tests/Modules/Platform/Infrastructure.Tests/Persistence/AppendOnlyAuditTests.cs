using HeatSynQ.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeatSynQ.Platform.Infrastructure.Tests.Persistence;

public sealed class AppendOnlyAuditTests
{
    [Fact]
    public async Task Existing_audit_event_cannot_be_modified()
    {
        await using var db = CreateDatabase();
        var audit = AuditEvent.Create(
            "platform.user.disabled",
            "User",
            Guid.NewGuid().ToString(),
            Guid.NewGuid(),
            "Session-1",
            "Employment ended",
            """{"enabled":true}""",
            """{"enabled":false}""",
            DateTimeOffset.Parse("2026-07-19T15:00:00-05:00"));

        db.AuditEvents.Add(audit);
        await db.SaveChangesAsync();

        audit.Reason = "Changed after the fact";

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Existing_audit_event_cannot_be_deleted()
    {
        await using var db = CreateDatabase();
        var audit = AuditEvent.Create(
            "platform.role.updated",
            "Role",
            Guid.NewGuid().ToString(),
            Guid.NewGuid(),
            "Session-2",
            "Role maintenance",
            "{}",
            "{}",
            DateTimeOffset.Parse("2026-07-19T15:00:00-05:00"));

        db.AuditEvents.Add(audit);
        await db.SaveChangesAsync();
        db.AuditEvents.Remove(audit);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    private static PlatformDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new PlatformDbContext(options);
    }
}
