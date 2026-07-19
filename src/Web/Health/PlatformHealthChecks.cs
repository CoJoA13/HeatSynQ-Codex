using HeatSynQ.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HeatSynQ.Web.Health;

public sealed class ManagedStorageHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(configuration["Platform:FileStoragePath"]
            ?? Path.Combine(AppContext.BaseDirectory, "storage", "files"));
        try
        {
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, $".health-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probe, "ok", cancellationToken);
            File.Delete(probe);
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            var data = new Dictionary<string, object>
            {
                ["availableBytes"] = drive.AvailableFreeSpace
            };
            return drive.AvailableFreeSpace < 1024L * 1024 * 1024
                ? HealthCheckResult.Degraded("Managed storage has less than 1 GB free.", data: data)
                : HealthCheckResult.Healthy("Managed storage is writable.", data);
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Managed storage is unavailable.", exception);
        }
    }
}

public sealed class OutboxHealthCheck(
    IDbContextFactory<PlatformDbContext> dbContextFactory,
    TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var waiting = await db.Outbox.CountAsync(x => x.CompletedAt == null, cancellationToken);
        var failed = await db.Outbox.CountAsync(
            x => x.CompletedAt == null && x.AttemptCount >= 5,
            cancellationToken);
        var overdue = await db.Outbox.CountAsync(
            x => x.CompletedAt == null && x.OccurredAt < now.AddMinutes(-15),
            cancellationToken);
        var data = new Dictionary<string, object>
        {
            ["waiting"] = waiting,
            ["failed"] = failed,
            ["overdue"] = overdue
        };
        if (failed > 0)
            return HealthCheckResult.Unhealthy("Outbox has repeatedly failing messages.", data: data);
        return overdue > 0
            ? HealthCheckResult.Degraded("Outbox has messages waiting longer than 15 minutes.", data: data)
            : HealthCheckResult.Healthy("Outbox is processing normally.", data);
    }
}

public abstract class FreshnessFileHealthCheck(
    IConfiguration configuration,
    TimeProvider timeProvider,
    string configurationKey,
    string defaultFileName,
    TimeSpan maximumAge,
    string label) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(configuration[configurationKey]
            ?? Path.Combine(AppContext.BaseDirectory, "storage", defaultFileName));
        if (!File.Exists(path))
            return Task.FromResult(HealthCheckResult.Degraded($"{label} has not been recorded."));
        var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        var age = timeProvider.GetUtcNow() - modified;
        var data = new Dictionary<string, object>
        {
            ["lastRecordedAt"] = modified,
            ["ageHours"] = Math.Round(age.TotalHours, 2)
        };
        return Task.FromResult(age > maximumAge
            ? HealthCheckResult.Unhealthy($"{label} is stale.", data: data)
            : HealthCheckResult.Healthy($"{label} is current.", data));
    }
}

public sealed class BackupFreshnessHealthCheck(
    IConfiguration configuration,
    TimeProvider timeProvider)
    : FreshnessFileHealthCheck(
        configuration,
        timeProvider,
        "Platform:BackupStatusPath",
        "last-successful-backup.txt",
        TimeSpan.FromHours(26),
        "Successful backup");

public sealed class WorkerHeartbeatHealthCheck(
    IConfiguration configuration,
    TimeProvider timeProvider)
    : FreshnessFileHealthCheck(
        configuration,
        timeProvider,
        "Platform:WorkerHeartbeatPath",
        "worker-heartbeat.txt",
        TimeSpan.FromMinutes(2),
        "Worker heartbeat");
