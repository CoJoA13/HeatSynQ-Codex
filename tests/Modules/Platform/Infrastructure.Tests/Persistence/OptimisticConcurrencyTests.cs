using HeatSynQ.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeatSynQ.Platform.Infrastructure.Tests.Persistence;

public sealed class OptimisticConcurrencyTests
{
    [Fact]
    public async Task Concurrent_facility_settings_update_is_rejected()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase($"concurrency-{Guid.NewGuid()}")
            .Options;
        await using (var seed = new PlatformDbContext(options))
        {
            seed.FacilitySettings.Add(new FacilitySettings
            {
                Id = Guid.NewGuid(),
                CompanyName = "HeatSynQ",
                FacilityName = "Main",
                FacilityCode = "MAIN",
                Version = Guid.NewGuid()
            });
            await seed.SaveChangesAsync();
        }
        await using var first = new PlatformDbContext(options);
        await using var second = new PlatformDbContext(options);
        var firstRecord = await first.FacilitySettings.SingleAsync();
        var secondRecord = await second.FacilitySettings.SingleAsync();
        firstRecord.CompanyName = "First update";
        firstRecord.Version = Guid.NewGuid();
        await first.SaveChangesAsync();
        secondRecord.CompanyName = "Stale update";
        secondRecord.Version = Guid.NewGuid();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => second.SaveChangesAsync());
    }
}
