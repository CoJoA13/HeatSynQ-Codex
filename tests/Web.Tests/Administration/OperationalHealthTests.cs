using System.Net;
using System.Net.Http.Json;

namespace HeatSynQ.Web.Tests.Administration;

public sealed class OperationalHealthTests
{
    [Fact]
    public async Task Detailed_health_requires_permission_and_reports_all_platform_components()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var anonymous = factory.CreateHttpsClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/platform/health")).StatusCode);
        using var administrator = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(administrator);

        var response = await administrator.GetAsync("/api/v1/platform/health");
        response.EnsureSuccessStatusCode();
        var health = await response.Content.ReadFromJsonAsync<HealthSummary>();

        Assert.Contains(health!.Checks, x => x.Name == "platform_database");
        Assert.Contains(health.Checks, x => x.Name == "managed_storage");
        Assert.Contains(health.Checks, x => x.Name == "outbox");
        Assert.Contains(health.Checks, x => x.Name == "backup_freshness");
        Assert.Contains(health.Checks, x => x.Name == "worker_heartbeat");
    }

    private static async Task BootstrapAndLoginAsync(HttpClient client)
    {
        (await client.PostAsJsonAsync("/api/v1/platform/bootstrap", new
        {
            BootstrapSecret = "test-bootstrap-secret",
            Username = "admin",
            Email = "admin@example.test",
            DisplayName = "Platform Administrator",
            Password = "Correct-Horse-Battery-Staple!7"
        })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "admin",
            Password = "Correct-Horse-Battery-Staple!7",
            RememberMe = false
        })).EnsureSuccessStatusCode();
    }

    private sealed record HealthSummary(string Status, HealthCheckSummary[] Checks);
    private sealed record HealthCheckSummary(string Name, string Status, string? Description);
}
