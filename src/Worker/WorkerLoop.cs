using HeatSynQ.Platform.Infrastructure.Work;

namespace HeatSynQ.Worker;

public sealed class WorkerLoop(
    PlatformOutboxProcessor processor,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<WorkerLoop> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var heartbeatPath = Path.GetFullPath(configuration["Platform:WorkerHeartbeatPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "storage", "worker-heartbeat.txt"));
        Directory.CreateDirectory(Path.GetDirectoryName(heartbeatPath)!);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await processor.ProcessBatchAsync(cancellationToken: stoppingToken);
                await File.WriteAllTextAsync(
                    heartbeatPath,
                    timeProvider.GetUtcNow().ToString("O"),
                    stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The platform worker cycle failed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken);
        }
    }
}
