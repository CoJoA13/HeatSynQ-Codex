using System.Net;
using System.Net.Http.Json;
using HeatSynQ.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HeatSynQ.Web.Tests.Administration;

public sealed class UserAdministrationMutationTests
{
    [Fact]
    public async Task Administrator_can_create_user_and_action_is_audited()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/platform/users", new
        {
            Username = "quality.lead",
            Email = "quality.lead@example.test",
            DisplayName = "Quality Lead",
            Password = "Correct-Horse-Battery-Staple!9",
            Reason = "New quality department account"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var createdUser = await db.Users.SingleAsync(x => x.UserName == "quality.lead");
        Assert.True(createdUser.LockoutEnabled);
        var auditEvent = await db.AuditEvents
            .SingleAsync(x => x.Action == "platform.user.created"
                && x.EntityId == createdUser.Id.ToString());
        Assert.Equal("New quality department account", auditEvent.Reason);
    }

    [Fact]
    public async Task Disabling_user_immediately_revokes_existing_session_and_is_audited()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var administratorClient = factory.CreateHttpsClient();
        using var operatorClient = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(administratorClient);
        var createResponse = await administratorClient.PostAsJsonAsync(
            "/api/v1/platform/users",
            new
            {
                Username = "operator",
                Email = "operator@example.test",
                DisplayName = "Operator",
                Password = "Correct-Horse-Battery-Staple!8",
                Reason = "New production operator"
            });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CreatedUser>();
        var operatorLogin = await operatorClient.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "operator",
            Password = "Correct-Horse-Battery-Staple!8",
            RememberMe = false
        });
        operatorLogin.EnsureSuccessStatusCode();

        var disableResponse = await administratorClient.PutAsJsonAsync(
            $"/api/v1/platform/users/{created!.Id}/status",
            new
            {
                IsEnabled = false,
                Reason = "Employment ended"
            });
        var operatorIdentity = await operatorClient.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.NoContent, disableResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, operatorIdentity.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var disabledUser = await db.Users.SingleAsync(x => x.Id == created.Id);
        Assert.False(disabledUser.IsEnabled);
        Assert.Equal("Employment ended", disabledUser.DisabledReason);
        Assert.NotNull(disabledUser.DisabledAt);
        Assert.Contains(
            await db.AuditEvents.ToArrayAsync(),
            x => x.Action == "platform.user.disabled"
                && x.EntityId == created.Id.ToString()
                && x.Reason == "Employment ended");
    }

    [Fact]
    public async Task Restoring_user_clears_disabled_state_and_preserves_audit_history()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var administratorClient = factory.CreateHttpsClient();
        using var restoredUserClient = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(administratorClient);
        var createResponse = await administratorClient.PostAsJsonAsync(
            "/api/v1/platform/users",
            new
            {
                Username = "shipping",
                Email = "shipping@example.test",
                DisplayName = "Shipping",
                Password = "Correct-Horse-Battery-Staple!6",
                Reason = "New shipping account"
            });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CreatedUser>();
        var disableResponse = await administratorClient.PutAsJsonAsync(
            $"/api/v1/platform/users/{created!.Id}/status",
            new { IsEnabled = false, Reason = "Seasonal layoff" });
        disableResponse.EnsureSuccessStatusCode();

        var restoreResponse = await administratorClient.PutAsJsonAsync(
            $"/api/v1/platform/users/{created.Id}/status",
            new { IsEnabled = true, Reason = "Returned to work" });
        var loginResponse = await restoredUserClient.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "shipping",
            Password = "Correct-Horse-Battery-Staple!6",
            RememberMe = false
        });

        Assert.Equal(HttpStatusCode.NoContent, restoreResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, loginResponse.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var restoredUser = await db.Users.SingleAsync(x => x.Id == created.Id);
        Assert.True(restoredUser.IsEnabled);
        Assert.Null(restoredUser.DisabledAt);
        Assert.Null(restoredUser.DisabledReason);
        var actions = await db.AuditEvents
            .Where(x => x.EntityId == created.Id.ToString())
            .Select(x => x.Action)
            .ToArrayAsync();
        Assert.Contains("platform.user.disabled", actions);
        Assert.Contains("platform.user.restored", actions);
    }

    [Fact]
    public async Task Administrator_can_revoke_another_users_sessions_without_disabling_account()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var administratorClient = factory.CreateHttpsClient();
        using var operatorClient = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(administratorClient);
        var createResponse = await administratorClient.PostAsJsonAsync(
            "/api/v1/platform/users",
            new
            {
                Username = "furnace.operator",
                Email = "furnace.operator@example.test",
                DisplayName = "Furnace Operator",
                Password = "Correct-Horse-Battery-Staple!5",
                Reason = "New furnace operator"
            });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CreatedUser>();
        var loginResponse = await operatorClient.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "furnace.operator",
            Password = "Correct-Horse-Battery-Staple!5",
            RememberMe = false
        });
        loginResponse.EnsureSuccessStatusCode();

        var revokeResponse = await administratorClient.PostAsJsonAsync(
            $"/api/v1/platform/users/{created!.Id}/revoke-sessions",
            new { Reason = "Lost shop-floor tablet" });
        var operatorIdentity = await operatorClient.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, operatorIdentity.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.True((await db.Users.SingleAsync(x => x.Id == created.Id)).IsEnabled);
        Assert.Contains(
            await db.AuditEvents.ToArrayAsync(),
            x => x.Action == "platform.sessions.revoked"
                && x.EntityId == created.Id.ToString()
                && x.Reason == "Lost shop-floor tablet");
    }

    [Fact]
    public async Task Administrator_can_assign_multiple_roles_when_creating_user()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        foreach (var roleName in new[] { "Receiving", "Quality" })
        {
            var roleResponse = await client.PostAsJsonAsync("/api/v1/platform/roles", new
            {
                Name = roleName,
                Description = $"{roleName} department access.",
                PermissionKeys = Array.Empty<string>(),
                Reason = $"Create {roleName} role"
            });
            roleResponse.EnsureSuccessStatusCode();
        }

        var createResponse = await client.PostAsJsonAsync("/api/v1/platform/users", new
        {
            Username = "receiving.quality",
            Email = "receiving.quality@example.test",
            DisplayName = "Receiving Quality",
            Password = "Correct-Horse-Battery-Staple!4",
            RoleNames = new[] { "Receiving", "Quality" },
            Reason = "Cross-trained employee"
        });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var user = await db.Users.SingleAsync(x => x.UserName == "receiving.quality");
        var roleNames = await (
            from userRole in db.UserRoles
            join role in db.Roles on userRole.RoleId equals role.Id
            where userRole.UserId == user.Id
            orderby role.Name
            select role.Name).ToArrayAsync();
        Assert.Equal(["Quality", "Receiving"], roleNames);
    }

    [Fact]
    public async Task Administrator_can_replace_roles_for_an_existing_user()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        (await client.PostAsJsonAsync("/api/v1/platform/roles", new
        {
            Name = "Operator",
            Description = "Shop-floor operator.",
            PermissionKeys = Array.Empty<string>(),
            Reason = "Create operator role"
        })).EnsureSuccessStatusCode();
        var create = await client.PostAsJsonAsync("/api/v1/platform/users", new
        {
            Username = "future.operator",
            Email = "future.operator@example.test",
            DisplayName = "Future Operator",
            Password = "Correct-Horse-Battery-Staple!8",
            RoleNames = Array.Empty<string>(),
            Reason = "Create before assignment"
        });
        var user = (await create.Content.ReadFromJsonAsync<CreatedUser>())!;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var version = await db.Users
            .Where(x => x.Id == user.Id)
            .Select(x => x.ConcurrencyStamp)
            .SingleAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/v1/platform/users/{user.Id}/roles",
            new
            {
                RoleNames = new[] { "Operator" },
                Version = version,
                Reason = "Employee moved to production"
            });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(
            await db.AuditEvents.ToArrayAsync(),
            x => x.Action == "platform.user.roles_changed" &&
                 x.EntityId == user.Id.ToString());
    }

    private static async Task BootstrapAndLoginAsync(HttpClient client)
    {
        var bootstrap = await client.PostAsJsonAsync("/api/v1/platform/bootstrap", new
        {
            BootstrapSecret = "test-bootstrap-secret",
            Username = "admin",
            Email = "admin@example.test",
            DisplayName = "Platform Administrator",
            Password = "Correct-Horse-Battery-Staple!7"
        });
        bootstrap.EnsureSuccessStatusCode();

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "admin",
            Password = "Correct-Horse-Battery-Staple!7",
            RememberMe = false
        });
        login.EnsureSuccessStatusCode();
    }

    private sealed record CreatedUser(Guid Id);
}
