using System.Net;
using System.Net.Http.Json;
using HeatSynQ.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HeatSynQ.Web.Tests.Administration;

public sealed class ReviewRegressionTests
{
    [Fact]
    public async Task Controlled_number_allocation_requires_settings_edit_permission()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var administrator = factory.CreateHttpsClient();
        using var viewer = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(administrator);
        (await administrator.PutAsJsonAsync("/api/v1/platform/number-sequences/JOB", new
        {
            Prefix = "J-",
            NextValue = 1,
            Padding = 4,
            Reason = "Configure job numbering"
        })).EnsureSuccessStatusCode();
        var user = await CreateUserAsync(administrator, "viewer");
        await GrantAsync(administrator, user.Id, "platform.settings.view");
        await LoginAsync(viewer, "viewer");

        var response = await viewer.PostAsync(
            "/api/v1/platform/number-sequences/JOB/allocate",
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task User_editor_cannot_assign_roles_without_role_edit_permission()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var administrator = factory.CreateHttpsClient();
        using var delegatedEditor = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(administrator);
        var editor = await CreateUserAsync(administrator, "delegated.editor");
        await GrantAsync(administrator, editor.Id, "platform.users.edit");
        await LoginAsync(delegatedEditor, "delegated.editor");

        var response = await delegatedEditor.PostAsJsonAsync("/api/v1/platform/users", new
        {
            Username = "escalated",
            Email = "escalated@example.test",
            DisplayName = "Escalated User",
            Password = "Correct-Horse-Battery-Staple!9",
            RoleNames = new[] { "Administrator" },
            Reason = "Attempted delegated assignment"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Stale_settings_version_is_rejected()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        var initial = await client.GetFromJsonAsync<SettingsVersion>("/api/v1/platform/settings");
        var first = await client.PutAsJsonAsync("/api/v1/platform/settings", new
        {
            CompanyName = "HeatSynQ One",
            FacilityName = "Main",
            FacilityCode = "MAIN",
            TimeZoneId = "America/Chicago",
            DefaultRetentionYears = 10,
            Version = initial!.Version,
            Reason = "First edit"
        });
        first.EnsureSuccessStatusCode();

        var stale = await client.PutAsJsonAsync("/api/v1/platform/settings", new
        {
            CompanyName = "HeatSynQ Stale",
            FacilityName = "Main",
            FacilityCode = "MAIN",
            TimeZoneId = "America/Chicago",
            DefaultRetentionYears = 10,
            Version = initial.Version,
            Reason = "Stale edit"
        });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Stale_sequence_and_retention_versions_are_rejected()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        var sequenceCreate = await client.PutAsJsonAsync(
            "/api/v1/platform/number-sequences/JOB",
            new
            {
                Prefix = "J-",
                NextValue = 1001,
                Padding = 6,
                Reason = "Initial sequence"
            });
        var sequence = await sequenceCreate.Content.ReadFromJsonAsync<VersionedRecord>();
        (await client.PutAsJsonAsync("/api/v1/platform/number-sequences/JOB", new
        {
            Prefix = "J-",
            NextValue = 2001,
            Padding = 6,
            Version = sequence!.Version,
            Reason = "Current sequence edit"
        })).EnsureSuccessStatusCode();
        var staleSequence = await client.PutAsJsonAsync(
            "/api/v1/platform/number-sequences/JOB",
            new
            {
                Prefix = "J-",
                NextValue = 3001,
                Padding = 6,
                Version = sequence.Version,
                Reason = "Stale sequence edit"
            });

        var retentionCreate = await client.PutAsJsonAsync(
            "/api/v1/platform/retention-policies/quality",
            new
            {
                RetentionYears = 10,
                Reason = "Initial retention"
            });
        var retention = await retentionCreate.Content.ReadFromJsonAsync<VersionedRecord>();
        (await client.PutAsJsonAsync(
            "/api/v1/platform/retention-policies/quality",
            new
            {
                RetentionYears = 12,
                Version = retention!.Version,
                Reason = "Current retention edit"
            })).EnsureSuccessStatusCode();
        var staleRetention = await client.PutAsJsonAsync(
            "/api/v1/platform/retention-policies/quality",
            new
            {
                RetentionYears = 15,
                Version = retention.Version,
                Reason = "Stale retention edit"
            });

        Assert.Equal(HttpStatusCode.Conflict, staleSequence.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, staleRetention.StatusCode);
    }

    [Fact]
    public async Task Administrative_audit_uses_authenticated_session_id()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        var settings = await client.GetFromJsonAsync<SettingsVersion>("/api/v1/platform/settings");
        (await client.PutAsJsonAsync("/api/v1/platform/settings", new
        {
            CompanyName = "HeatSynQ",
            FacilityName = "Main",
            FacilityCode = "MAIN",
            TimeZoneId = "America/Chicago",
            DefaultRetentionYears = 10,
            Version = settings!.Version,
            Reason = "Verify session audit"
        })).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var sessionId = await db.Sessions.OrderByDescending(x => x.CreatedAt)
            .Select(x => x.Id)
            .FirstAsync();
        var auditSessionId = await db.AuditEvents
            .Where(x => x.Action == "platform.settings.updated")
            .Select(x => x.SessionId)
            .SingleAsync();

        Assert.Equal(sessionId.ToString(), auditSessionId);
    }

    [Fact]
    public async Task Audit_csv_neutralizes_spreadsheet_formulas()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        var settings = await client.GetFromJsonAsync<SettingsVersion>("/api/v1/platform/settings");
        (await client.PutAsJsonAsync("/api/v1/platform/settings", new
        {
            CompanyName = "HeatSynQ",
            FacilityName = "Main",
            FacilityCode = "MAIN",
            TimeZoneId = "America/Chicago",
            DefaultRetentionYears = 10,
            Version = settings!.Version,
            Reason = "=HYPERLINK(\"https://example.test\")"
        })).EnsureSuccessStatusCode();

        var csv = await client.GetStringAsync(
            "/api/v1/platform/audit/export?action=platform.settings.updated");

        Assert.Contains("\"'=HYPERLINK(\"\"https://example.test\"\")\"", csv);
    }

    [Fact]
    public async Task Oversized_audit_export_is_rejected_instead_of_truncated()
    {
        await using var factory = new PlatformWebApplicationFactory(
            new Dictionary<string, string?>
            {
                ["Platform:MaxAuditExportRows"] = "2"
            });
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var actorId = await db.Users.Select(x => x.Id).SingleAsync();
            db.AuditEvents.AddRange(Enumerable.Range(1, 3).Select(index =>
                AuditEvent.Create(
                    "review.export.limit",
                    "Review",
                    index.ToString(),
                    actorId,
                    "test-session",
                    "Export limit regression",
                    "{}",
                    "{}",
                    DateTimeOffset.UtcNow.AddSeconds(index))));
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync(
            "/api/v1/platform/audit/export?action=review.export.limit");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("narrow", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Concurrent_work_retries_return_one_accepted_item()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        var requests = Enumerable.Range(0, 12)
            .Select(_ => client.PostAsJsonAsync("/api/v1/platform/work", new
            {
                MessageType = "platform.notification",
                Payload = new
                {
                    Recipient = "quality",
                    Subject = "Calibration due",
                    Body = "Furnace 3 calibration is due."
                },
                IdempotencyKey = "concurrent-calibration-reminder"
            }))
            .ToArray();

        var responses = await Task.WhenAll(requests);
        var ids = new List<Guid>();
        foreach (var response in responses)
        {
            response.EnsureSuccessStatusCode();
            ids.Add((await response.Content.ReadFromJsonAsync<QueuedWork>())!.Id);
        }

        Assert.Single(ids.Distinct());
        var queue = await client.GetFromJsonAsync<QueuedWork[]>("/api/v1/platform/work");
        Assert.Single(queue!);
    }

    [Fact]
    public async Task Authenticated_activity_refreshes_and_returns_last_seen()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        Guid userId;
        DateTimeOffset oldValue;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var session = await db.Sessions.SingleAsync();
            userId = session.UserId;
            oldValue = DateTimeOffset.UtcNow.AddHours(-1);
            session.LastSeenAt = oldValue;
            await db.SaveChangesAsync();
        }

        (await client.GetAsync("/api/v1/auth/me")).EnsureSuccessStatusCode();
        var sessions = await client.GetFromJsonAsync<SessionSummary[]>(
            $"/api/v1/platform/users/{userId}/sessions");

        Assert.True(Assert.Single(sessions!).LastSeenAt > oldValue);
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
        await LoginAsync(client, "admin");
    }

    private static async Task LoginAsync(HttpClient client, string username)
    {
        (await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = username,
            Password = username == "admin"
                ? "Correct-Horse-Battery-Staple!7"
                : "Correct-Horse-Battery-Staple!8",
            RememberMe = false
        })).EnsureSuccessStatusCode();
    }

    private static async Task<CreatedUser> CreateUserAsync(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync("/api/v1/platform/users", new
        {
            Username = username,
            Email = $"{username}@example.test",
            DisplayName = username,
            Password = "Correct-Horse-Battery-Staple!8",
            RoleNames = Array.Empty<string>(),
            Reason = "Review regression account"
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedUser>())!;
    }

    private static async Task GrantAsync(HttpClient client, Guid userId, string permission)
    {
        (await client.PostAsJsonAsync(
            $"/api/v1/platform/users/{userId}/permission-overrides",
            new
            {
                PermissionKey = permission,
                Effect = "Allow",
                Reason = "Review regression permission"
            })).EnsureSuccessStatusCode();
    }

    private sealed record CreatedUser(Guid Id);
    private sealed record SettingsVersion(Guid Version);
    private sealed record VersionedRecord(Guid Version);
    private sealed record QueuedWork(Guid Id);
    private sealed record SessionSummary(Guid Id, DateTimeOffset LastSeenAt);
}
