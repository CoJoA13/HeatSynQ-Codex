using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HeatSynQ.Web.Tests.PostgreSql;

public sealed class PostgreSqlAcceptanceTests
{
    [Fact]
    public async Task Migrated_PostgreSql_supports_module_one_transactional_workflow()
    {
        var connectionString = Environment.GetEnvironmentVariable("HEATSYNQ_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var factory = new PostgreSqlFactory(connectionString);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        (await client.PostAsJsonAsync("/api/v1/platform/bootstrap", new
        {
            BootstrapSecret = "postgres-acceptance-secret",
            Username = "acceptance-admin",
            Email = "acceptance-admin@example.test",
            DisplayName = "Acceptance Administrator",
            Password = "Correct-Horse-Battery-Staple!9"
        })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "acceptance-admin",
            Password = "Correct-Horse-Battery-Staple!9",
            RememberMe = false
        })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/v1/platform/settings", new
        {
            CompanyName = "HeatSynQ Acceptance",
            FacilityName = "PostgreSQL Plant",
            FacilityCode = "PG",
            TimeZoneId = "America/Chicago",
            DefaultRetentionYears = 10,
            Reason = "PostgreSQL acceptance"
        })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/platform/work", new
        {
            MessageType = "platform.notification",
            Payload = new
            {
                Recipient = "admin",
                Subject = "Acceptance",
                Body = "PostgreSQL workflow"
            },
            IdempotencyKey = $"postgres-acceptance-{Guid.NewGuid():N}"
        })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/v1/platform/number-sequences/JOB", new
        {
            Prefix = "PG-",
            NextValue = 1,
            Padding = 6,
            Reason = "Concurrent allocation acceptance"
        })).EnsureSuccessStatusCode();
        var allocationResponses = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ =>
                client.PostAsync(
                    "/api/v1/platform/number-sequences/JOB/allocate",
                    content: null)));
        var allocatedValues = new List<string>();
        foreach (var response in allocationResponses)
        {
            response.EnsureSuccessStatusCode();
            allocatedValues.Add(
                (await response.Content.ReadFromJsonAsync<AllocatedNumber>())!.Value);
        }

        var audit = await client.GetAsync(
            "/api/v1/platform/audit?action=platform.settings.updated");
        audit.EnsureSuccessStatusCode();
        Assert.Contains("platform.settings.updated", await audit.Content.ReadAsStringAsync());
        Assert.Equal(16, allocatedValues.Distinct().Count());
    }

    private sealed record AllocatedNumber(string Value);

    private sealed class PostgreSqlFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Platform", connectionString);
            builder.UseSetting("Platform:BootstrapSecret", "postgres-acceptance-secret");
            builder.UseSetting(
                "Platform:FileStoragePath",
                Path.Combine(Path.GetTempPath(), "heatsynq-postgres-acceptance"));
        }
    }
}
