using HeatSynQ.Platform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HeatSynQ.Web.Tests;

public sealed class PlatformWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"platform-tests-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Platform:BootstrapSecret", "test-bootstrap-secret");
        builder.UseSetting(
            "Platform:FileStoragePath",
            Path.Combine(Path.GetTempPath(), "heatsynq-tests", _databaseName));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<PlatformDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<PlatformDbContext>>();
            services.RemoveAll<IDatabaseProvider>();
            services.RemoveAll<IDbContextFactory<PlatformDbContext>>();
            services.AddDbContextFactory<PlatformDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));
        });
    }

    public HttpClient CreateHttpsClient(bool allowAutoRedirect = true) =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = allowAutoRedirect
        });
}
