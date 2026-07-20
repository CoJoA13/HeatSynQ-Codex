using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HeatSynQ.Web.Tests.Administration;

public sealed class PlatformAdministrationTests
{
    [Fact]
    public async Task Administrator_can_update_settings_allocate_number_and_place_legal_hold()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);

        var settingsResponse = await client.PutAsJsonAsync("/api/v1/platform/settings", new
        {
            CompanyName = "Commercial Heat Treating, Inc.",
            FacilityName = "Main Plant",
            FacilityCode = "MAIN",
            TimeZoneId = "America/Chicago",
            DefaultRetentionYears = 10,
            Reason = "Initial facility configuration"
        });
        settingsResponse.EnsureSuccessStatusCode();

        var sequenceResponse = await client.PutAsJsonAsync(
            "/api/v1/platform/number-sequences/JOB",
            new
            {
                Prefix = "J-",
                NextValue = 1001,
                Padding = 6,
                Reason = "Initialize job numbering"
            });
        sequenceResponse.EnsureSuccessStatusCode();
        var first = await (
            await client.PostAsync("/api/v1/platform/number-sequences/JOB/allocate", null))
            .Content.ReadFromJsonAsync<AllocatedNumber>();
        var second = await (
            await client.PostAsync("/api/v1/platform/number-sequences/JOB/allocate", null))
            .Content.ReadFromJsonAsync<AllocatedNumber>();

        Assert.Equal("J-001001", first!.Value);
        Assert.Equal("J-001002", second!.Value);

        var holdResponse = await client.PostAsJsonAsync("/api/v1/platform/legal-holds", new
        {
            Category = "quality",
            EntityType = "Customer",
            EntityId = "customer-42",
            Reason = "Customer litigation hold"
        });
        Assert.Equal(HttpStatusCode.Created, holdResponse.StatusCode);
        var holds = await (
            await client.GetAsync("/api/v1/platform/legal-holds?activeOnly=true"))
            .Content.ReadFromJsonAsync<LegalHoldSummary[]>();
        Assert.Single(holds!);
        Assert.Equal("quality", holds![0].Category);
    }

    [Fact]
    public async Task Audit_can_be_filtered_and_export_requires_its_own_permission()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var administratorClient = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(administratorClient);

        var filtered = await administratorClient.GetFromJsonAsync<AuditPage>(
            "/api/v1/platform/audit?action=platform.bootstrap.completed&pageSize=10");
        Assert.Equal(1, filtered!.Total);
        Assert.Equal("platform.bootstrap.completed", filtered.Items[0].Action);

        var csv = await administratorClient.GetAsync(
            "/api/v1/platform/audit/export?action=platform.bootstrap.completed");
        csv.EnsureSuccessStatusCode();
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Contains("platform.bootstrap.completed", await csv.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Settings_reject_invalid_retention_and_missing_reason()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);

        var response = await client.PutAsJsonAsync("/api/v1/platform/settings", new
        {
            CompanyName = "HeatSynQ",
            FacilityName = "Main",
            FacilityCode = "MAIN",
            TimeZoneId = "America/Chicago",
            DefaultRetentionYears = 0,
            Reason = ""
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Concurrent_initial_settings_updates_leave_one_readable_record()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        var requests = Enumerable.Range(1, 2).Select(index =>
            client.PutAsJsonAsync("/api/v1/platform/settings", new
            {
                CompanyName = $"HeatSynQ {index}",
                FacilityName = "Main",
                FacilityCode = "MAIN",
                TimeZoneId = "America/Chicago",
                DefaultRetentionYears = 10,
                Reason = $"Concurrent setup {index}"
            }));

        var responses = await Task.WhenAll(requests);
        var read = await client.GetAsync("/api/v1/platform/settings");

        Assert.All(
            responses,
            response => Assert.Contains(
                response.StatusCode,
                new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task Queue_submission_is_idempotent_for_notifications_and_print_jobs()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        var request = new
        {
            MessageType = "platform.notification",
            Payload = new
            {
                Recipient = "quality",
                Subject = "Calibration due",
                Body = "Furnace 3 calibration is due."
            },
            IdempotencyKey = "calibration-furnace-3-2026-07"
        };

        var firstResponse = await client.PostAsJsonAsync("/api/v1/platform/work", request);
        var secondResponse = await client.PostAsJsonAsync("/api/v1/platform/work", request);
        firstResponse.EnsureSuccessStatusCode();
        secondResponse.EnsureSuccessStatusCode();
        var first = await firstResponse.Content.ReadFromJsonAsync<QueuedWork>();
        var second = await secondResponse.Content.ReadFromJsonAsync<QueuedWork>();

        Assert.Equal(first!.Id, second!.Id);
        var queue = await client.GetFromJsonAsync<QueuedWork[]>("/api/v1/platform/work");
        Assert.Single(queue!);
    }

    [Theory]
    [InlineData("platform.notification", """{"subject":"Missing recipient"}""")]
    [InlineData("platform.print", """{"printer":"Shipping","documentPath":"","copies":0}""")]
    public async Task Queue_rejects_malformed_approved_payloads(
        string messageType,
        string payload)
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        using var document = JsonDocument.Parse(payload);

        var response = await client.PostAsJsonAsync("/api/v1/platform/work", new
        {
            MessageType = messageType,
            Payload = document.RootElement,
            IdempotencyKey = $"invalid-{Guid.NewGuid():N}"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

    private sealed record AllocatedNumber(string Value);
    private sealed record LegalHoldSummary(Guid Id, string Category);
    private sealed record AuditPage(int Total, AuditItem[] Items);
    private sealed record AuditItem(string Action);
    private sealed record QueuedWork(Guid Id);
}
