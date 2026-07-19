using System.Net;
using System.Net.Http.Json;
using HeatSynQ.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HeatSynQ.Web.Tests.Administration;

public sealed class UserAdministrationPageTests
{
    [Fact]
    public async Task Users_page_renders_persisted_accounts()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        var createResponse = await client.PostAsJsonAsync("/api/v1/platform/users", new
        {
            Username = "quality.lead",
            Email = "quality.lead@example.test",
            DisplayName = "Quality Lead",
            Password = "Correct-Horse-Battery-Staple!9",
            RoleNames = Array.Empty<string>(),
            Reason = "New quality department account"
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CreatedUser>();
        var overrideResponse = await client.PostAsJsonAsync(
            $"/api/v1/platform/users/{created!.Id}/permission-overrides",
            new
            {
                PermissionKey = "platform.health.view",
                Effect = "Allow",
                Reason = "Temporary health review"
            });
        overrideResponse.EnsureSuccessStatusCode();
        var permissionOverride = await overrideResponse.Content
            .ReadFromJsonAsync<CreatedPermissionOverride>();

        var response = await client.GetAsync("/admin/users");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Quality Lead", html);
        Assert.Contains("quality.lead", html);
        Assert.Contains("Platform Administrator", html);
        Assert.Contains("id=\"add-user-dialog\"", html);
        Assert.Contains("name=\"Username\"", html);
        Assert.Contains("name=\"Password\"", html);
        Assert.Contains("name=\"Reason\"", html);
        Assert.Contains("value=\"Administrator\"", html);
        Assert.Contains(
            $"action=\"/api/v1/platform/users/{created!.Id}/status\"",
            html);
        Assert.Contains("data-api-form=\"change-user-status\"", html);
        Assert.Contains(
            $"action=\"/api/v1/platform/users/{created.Id}/revoke-sessions\"",
            html);
        Assert.Contains("data-api-form=\"revoke-user-sessions\"", html);
        Assert.Contains(
            $"action=\"/api/v1/platform/users/{created.Id}/permission-overrides\"",
            html);
        Assert.Contains("data-api-form=\"create-permission-override\"", html);
        Assert.Contains("value=\"platform.users.view\"", html);
        Assert.Contains("platform.health.view", html);
        Assert.Contains(
            $"action=\"/api/v1/platform/users/{created.Id}/permission-overrides/{permissionOverride!.Id}\"",
            html);
        Assert.Contains("data-api-form=\"revoke-permission-override\"", html);
    }

    [Fact]
    public async Task Users_page_returns_forbidden_without_view_permission()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var administratorClient = factory.CreateHttpsClient();
        using var operatorClient = factory.CreateHttpsClient(allowAutoRedirect: false);
        await BootstrapAndLoginAsync(administratorClient);
        (await administratorClient.PostAsJsonAsync("/api/v1/platform/users", new
        {
            Username = "operator",
            Email = "operator@example.test",
            DisplayName = "Operator",
            Password = "Correct-Horse-Battery-Staple!8",
            RoleNames = Array.Empty<string>(),
            Reason = "New operator"
        })).EnsureSuccessStatusCode();
        (await operatorClient.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "operator",
            Password = "Correct-Horse-Battery-Staple!8",
            RememberMe = false
        })).EnsureSuccessStatusCode();

        var response = await operatorClient.GetAsync("/admin/users");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith(
            "/access-denied",
            response.Headers.Location?.PathAndQuery,
            StringComparison.Ordinal);
        var accessDenied = await operatorClient.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, accessDenied.StatusCode);
        Assert.Contains(
            "You don’t have permission to open this page.",
            await accessDenied.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task View_only_user_does_not_see_mutation_controls()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var administrator = factory.CreateHttpsClient();
        using var viewer = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(administrator);
        var create = await administrator.PostAsJsonAsync("/api/v1/platform/users", new
        {
            Username = "user.viewer",
            Email = "user.viewer@example.test",
            DisplayName = "User Viewer",
            Password = "Correct-Horse-Battery-Staple!8",
            RoleNames = Array.Empty<string>(),
            Reason = "Read-only administrator"
        });
        var user = await create.Content.ReadFromJsonAsync<CreatedUser>();
        (await administrator.PostAsJsonAsync(
            $"/api/v1/platform/users/{user!.Id}/permission-overrides",
            new
            {
                PermissionKey = "platform.users.view",
                Effect = "Allow",
                Reason = "Read-only user directory"
            })).EnsureSuccessStatusCode();
        (await viewer.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "user.viewer",
            Password = "Correct-Horse-Battery-Staple!8",
            RememberMe = false
        })).EnsureSuccessStatusCode();

        var html = await viewer.GetStringAsync("/admin/users");

        Assert.DoesNotContain("id=\"add-user-dialog\"", html);
        Assert.DoesNotContain("data-api-form=\"change-user-status\"", html);
        Assert.DoesNotContain("data-api-form=\"revoke-user-sessions\"", html);
        Assert.DoesNotContain("data-api-form=\"create-permission-override\"", html);
        Assert.DoesNotContain("data-api-form=\"revoke-permission-override\"", html);
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

    private sealed record CreatedUser(Guid Id);

    private sealed record CreatedPermissionOverride(Guid Id);
}
