using System.Net;
using System.Net.Http.Json;
using HeatSynQ.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HeatSynQ.Web.Tests.Administration;

public sealed class UserAdministrationAuthorizationTests
{
    [Fact]
    public async Task Administrator_can_list_users()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAsync(client);
        await LoginAsync(client, "admin", "Correct-Horse-Battery-Staple!7");

        var response = await client.GetAsync("/api/v1/platform/users");
        var users = await response.Content.ReadFromJsonAsync<UserSummary[]>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var administrator = Assert.Single(users!);
        Assert.Equal("admin", administrator.Username);
        Assert.Equal(["Administrator"], administrator.Roles);
    }

    [Fact]
    public async Task Authenticated_user_without_permission_cannot_list_users()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAsync(client);
        await CreateUserWithoutRolesAsync(factory.Services);
        await LoginAsync(client, "operator", "Correct-Horse-Battery-Staple!8");

        var response = await client.GetAsync("/api/v1/platform/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Temporary_user_allow_override_grants_access_and_is_audited()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var administratorClient = factory.CreateHttpsClient();
        using var operatorClient = factory.CreateHttpsClient();
        await BootstrapAsync(administratorClient);
        await LoginAsync(administratorClient, "admin", "Correct-Horse-Battery-Staple!7");
        var createResponse = await administratorClient.PostAsJsonAsync(
            "/api/v1/platform/users",
            new
            {
                Username = "temporary.operator",
                Email = "temporary.operator@example.test",
                DisplayName = "Temporary Operator",
                Password = "Correct-Horse-Battery-Staple!3",
                Reason = "Temporary employee"
            });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CreatedUser>();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(8);

        var overrideResponse = await administratorClient.PostAsJsonAsync(
            $"/api/v1/platform/users/{created!.Id}/permission-overrides",
            new
            {
                PermissionKey = "platform.users.view",
                Effect = "Allow",
                ExpiresAt = expiresAt,
                Reason = "Covering receiving desk"
            });
        await LoginAsync(
            operatorClient,
            "temporary.operator",
            "Correct-Horse-Battery-Staple!3");
        var listResponse = await operatorClient.GetAsync("/api/v1/platform/users");

        Assert.Equal(HttpStatusCode.Created, overrideResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var record = await db.UserPermissionOverrides.SingleAsync(x => x.UserId == created.Id);
        Assert.True(record.IsAllowed);
        Assert.Equal("Covering receiving desk", record.Reason);
        Assert.Equal(expiresAt, record.ExpiresAt);
        Assert.Contains(
            await db.AuditEvents.ToArrayAsync(),
            x => x.Action == "platform.permission_override.created"
                && x.EntityId == record.Id.ToString());
    }

    [Fact]
    public async Task Explicit_user_deny_overrides_administrator_role_grant()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAsync(client);
        await LoginAsync(client, "admin", "Correct-Horse-Battery-Staple!7");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var administratorId = await db.Users
            .Where(x => x.UserName == "admin")
            .Select(x => x.Id)
            .SingleAsync();
        var overrideResponse = await client.PostAsJsonAsync(
            $"/api/v1/platform/users/{administratorId}/permission-overrides",
            new
            {
                PermissionKey = "platform.users.view",
                Effect = "Deny",
                Reason = "Temporary separation of duties"
            });
        overrideResponse.EnsureSuccessStatusCode();

        var response = await client.GetAsync("/api/v1/platform/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Revoking_override_restores_default_denial_and_is_audited()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var administratorClient = factory.CreateHttpsClient();
        using var operatorClient = factory.CreateHttpsClient();
        await BootstrapAsync(administratorClient);
        await LoginAsync(administratorClient, "admin", "Correct-Horse-Battery-Staple!7");
        var createResponse = await administratorClient.PostAsJsonAsync(
            "/api/v1/platform/users",
            new
            {
                Username = "override.operator",
                Email = "override.operator@example.test",
                DisplayName = "Override Operator",
                Password = "Correct-Horse-Battery-Staple!2",
                Reason = "Temporary operator"
            });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CreatedUser>();
        var overrideResponse = await administratorClient.PostAsJsonAsync(
            $"/api/v1/platform/users/{created!.Id}/permission-overrides",
            new
            {
                PermissionKey = "platform.users.view",
                Effect = "Allow",
                Reason = "Temporary user review"
            });
        overrideResponse.EnsureSuccessStatusCode();
        var permissionOverride = await overrideResponse.Content
            .ReadFromJsonAsync<CreatedPermissionOverride>();
        await LoginAsync(
            operatorClient,
            "override.operator",
            "Correct-Horse-Battery-Staple!2");
        Assert.Equal(
            HttpStatusCode.OK,
            (await operatorClient.GetAsync("/api/v1/platform/users")).StatusCode);
        using var revokeRequest = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/v1/platform/users/{created.Id}/permission-overrides/{permissionOverride!.Id}")
        {
            Content = JsonContent.Create(new { Reason = "Temporary coverage ended" })
        };

        var revokeResponse = await administratorClient.SendAsync(revokeRequest);
        var deniedResponse = await operatorClient.GetAsync("/api/v1/platform/users");

        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.False(await db.UserPermissionOverrides.AnyAsync(x => x.Id == permissionOverride.Id));
        Assert.Contains(
            await db.AuditEvents.ToArrayAsync(),
            x => x.Action == "platform.permission_override.revoked"
                && x.EntityId == permissionOverride.Id.ToString()
                && x.Reason == "Temporary coverage ended");
    }

    private static async Task BootstrapAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/platform/bootstrap", new
        {
            BootstrapSecret = "test-bootstrap-secret",
            Username = "admin",
            Email = "admin@example.test",
            DisplayName = "Platform Administrator",
            Password = "Correct-Horse-Battery-Staple!7"
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task LoginAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = username,
            Password = password,
            RememberMe = false
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task CreateUserWithoutRolesAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var result = await userManager.CreateAsync(
            new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = "operator",
                Email = "operator@example.test",
                DisplayName = "Operator",
                EmailConfirmed = true,
                IsEnabled = true
            },
            "Correct-Horse-Battery-Staple!8");
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(x => x.Description)));
    }

    private sealed record UserSummary(
        Guid Id,
        string Username,
        string DisplayName,
        bool IsEnabled,
        string[] Roles);

    private sealed record CreatedUser(Guid Id);

    private sealed record CreatedPermissionOverride(Guid Id);
}
