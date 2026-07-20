using System.Net;
using System.Net.Http.Json;
using HeatSynQ.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HeatSynQ.Web.Tests.Administration;

public sealed class RoleAdministrationTests
{
    [Fact]
    public async Task Administrator_can_create_role_with_selected_permission_grants()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/platform/roles", new
        {
            Name = "Receiving",
            Description = "Receive customer material and review incoming orders.",
            PermissionKeys = new[]
            {
                "platform.users.view",
                "platform.health.view"
            },
            Reason = "Initial receiving role"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var role = await db.Roles.SingleAsync(x => x.Name == "Receiving");
        var grants = await db.RolePermissions
            .Where(x => x.RoleId == role.Id)
            .Select(x => x.PermissionKey)
            .OrderBy(x => x)
            .ToArrayAsync();
        Assert.Equal(["platform.health.view", "platform.users.view"], grants);
        Assert.Contains(
            await db.AuditEvents.ToArrayAsync(),
            x => x.Action == "platform.role.created"
                && x.EntityId == role.Id.ToString()
                && x.Reason == "Initial receiving role");
    }

    [Fact]
    public async Task Administrator_can_replace_custom_role_permission_toggles()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        var createResponse = await client.PostAsJsonAsync("/api/v1/platform/roles", new
        {
            Name = "Quality",
            Description = "Quality review and release.",
            PermissionKeys = new[]
            {
                "platform.users.view",
                "platform.health.view"
            },
            Reason = "Initial quality role"
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CreatedRole>();
        await using var versionScope = factory.Services.CreateAsyncScope();
        var versionDb = versionScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var version = await versionDb.Roles
            .Where(x => x.Id == created!.Id)
            .Select(x => x.ConcurrencyStamp)
            .SingleAsync();

        var updateResponse = await client.PutAsJsonAsync(
            $"/api/v1/platform/roles/{created!.Id}/permissions",
            new
            {
                PermissionKeys = new[]
                {
                    "platform.users.view",
                    "platform.audit.view"
                },
                Version = version,
                Reason = "Quality now reviews security audit"
            });

        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var grants = await db.RolePermissions
            .Where(x => x.RoleId == created.Id)
            .Select(x => x.PermissionKey)
            .OrderBy(x => x)
            .ToArrayAsync();
        Assert.Equal(["platform.audit.view", "platform.users.view"], grants);
        Assert.Contains(
            await db.AuditEvents.ToArrayAsync(),
            x => x.Action == "platform.role.permissions_changed"
                && x.EntityId == created.Id.ToString()
                && x.Reason == "Quality now reviews security audit");
    }

    [Fact]
    public async Task Stale_role_permission_replacement_is_rejected()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        var create = await client.PostAsJsonAsync("/api/v1/platform/roles", new
        {
            Name = "Scheduler",
            Description = "Scheduling access.",
            PermissionKeys = new[] { "platform.health.view" },
            Reason = "Create scheduler role"
        });
        var role = (await create.Content.ReadFromJsonAsync<CreatedRole>())!;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var staleVersion = await db.Roles
            .Where(x => x.Id == role.Id)
            .Select(x => x.ConcurrencyStamp)
            .SingleAsync();
        (await client.PutAsJsonAsync(
            $"/api/v1/platform/roles/{role.Id}/permissions",
            new
            {
                PermissionKeys = new[] { "platform.users.view" },
                Version = staleVersion,
                Reason = "First editor"
            })).EnsureSuccessStatusCode();

        var stale = await client.PutAsJsonAsync(
            $"/api/v1/platform/roles/{role.Id}/permissions",
            new
            {
                PermissionKeys = new[] { "platform.audit.view" },
                Version = staleVersion,
                Reason = "Stale editor"
            });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Concurrent_role_permission_replacements_return_a_conflict()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        var create = await client.PostAsJsonAsync("/api/v1/platform/roles", new
        {
            Name = "Concurrent Scheduler",
            Description = "Concurrent scheduling test.",
            PermissionKeys = new[] { "platform.health.view" },
            Reason = "Create concurrency test role"
        });
        var role = (await create.Content.ReadFromJsonAsync<CreatedRole>())!;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var version = await db.Roles
            .Where(x => x.Id == role.Id)
            .Select(x => x.ConcurrencyStamp)
            .SingleAsync();

        var responses = await Task.WhenAll(
            client.PutAsJsonAsync(
                $"/api/v1/platform/roles/{role.Id}/permissions",
                new
                {
                    PermissionKeys = new[] { "platform.users.view" },
                    Version = version,
                    Reason = "Concurrent editor one"
                }),
            client.PutAsJsonAsync(
                $"/api/v1/platform/roles/{role.Id}/permissions",
                new
                {
                    PermissionKeys = new[] { "platform.audit.view" },
                    Version = version,
                    Reason = "Concurrent editor two"
                }));

        Assert.Contains(responses, x => x.StatusCode == HttpStatusCode.NoContent);
        Assert.Contains(responses, x => x.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Administrator_can_list_roles_with_selected_permission_keys()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        (await client.PostAsJsonAsync("/api/v1/platform/roles", new
        {
            Name = "Operator",
            Description = "Execute released shop-floor work.",
            PermissionKeys = new[] { "platform.health.view" },
            Reason = "Initial operator role"
        })).EnsureSuccessStatusCode();

        var response = await client.GetAsync("/api/v1/platform/roles");
        var roles = await response.Content.ReadFromJsonAsync<RoleSummary[]>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            roles!,
            x => x.Name == "Administrator"
                && x.IsSystemRole
                && x.PermissionKeys.Contains("platform.roles.edit"));
        Assert.Contains(
            roles!,
            x => x.Name == "Operator"
                && !x.IsSystemRole
                && x.PermissionKeys.SequenceEqual(["platform.health.view"]));
    }

    [Fact]
    public async Task Administrator_can_list_self_describing_permission_catalog()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);

        var response = await client.GetAsync("/api/v1/platform/permissions");
        var permissions = await response.Content.ReadFromJsonAsync<PermissionSummary[]>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            permissions!,
            x => x.Key == "platform.permissions.override"
                && x.Module == "Platform"
                && x.Action == "Override permissions"
                && !string.IsNullOrWhiteSpace(x.Description));
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

    private sealed record CreatedRole(Guid Id);

    private sealed record RoleSummary(
        Guid Id,
        string Name,
        string Description,
        bool IsSystemRole,
        int UserCount,
        string[] PermissionKeys);

    private sealed record PermissionSummary(
        string Key,
        string Module,
        string Action,
        string Description);
}
