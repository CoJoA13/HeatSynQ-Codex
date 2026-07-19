using HeatSynQ.Platform.Infrastructure.Persistence;
using HeatSynQ.Platform.Infrastructure.Work;
using HeatSynQ.Worker;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
    options.ServiceName = "HeatSynQ Background Worker");
var connectionString = builder.Configuration.GetConnectionString("Platform")
    ?? throw new InvalidOperationException("Connection string 'Platform' is required.");
builder.Services.AddDbContextFactory<PlatformDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql =>
        npgsql.MigrationsHistoryTable("__migrations", "platform")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PlatformOutboxProcessor>();
builder.Services.AddHostedService<WorkerLoop>();
await builder.Build().RunAsync();
