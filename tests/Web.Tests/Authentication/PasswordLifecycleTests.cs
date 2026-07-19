using System.Net;
using System.Net.Http.Json;
using HeatSynQ.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HeatSynQ.Web.Tests.Authentication;

public sealed class PasswordLifecycleTests
{
    [Fact]
    public async Task Created_user_must_replace_temporary_password_before_using_erp()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var administrator = factory.CreateHttpsClient();
        using var userClient = factory.CreateHttpsClient(allowAutoRedirect: false);
        await BootstrapAndLoginAsync(administrator);
        await CreateUserAsync(administrator, "new.user", "Correct-Horse-Battery-Staple!8");
        (await userClient.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "new.user",
            Password = "Correct-Horse-Battery-Staple!8",
            RememberMe = false
        })).EnsureSuccessStatusCode();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await userClient.GetAsync("/api/v1/auth/me")).StatusCode);
        var page = await userClient.GetAsync("/account/password");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var change = await userClient.PostAsJsonAsync("/api/v1/auth/password", new
        {
            CurrentPassword = "Correct-Horse-Battery-Staple!8",
            NewPassword = "Replacement-Horse-Battery-Staple!8"
        });

        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await userClient.GetAsync("/api/v1/auth/me")).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.False((await db.Users.SingleAsync(x => x.UserName == "new.user"))
            .MustChangePassword);
    }

    [Fact]
    public async Task Administrator_reset_revokes_sessions_and_requires_another_change()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var administrator = factory.CreateHttpsClient();
        using var userClient = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(administrator);
        var user = await CreateUserAsync(
            administrator,
            "reset.user",
            "Correct-Horse-Battery-Staple!8");
        (await userClient.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "reset.user",
            Password = "Correct-Horse-Battery-Staple!8",
            RememberMe = false
        })).EnsureSuccessStatusCode();
        (await userClient.PostAsJsonAsync("/api/v1/auth/password", new
        {
            CurrentPassword = "Correct-Horse-Battery-Staple!8",
            NewPassword = "Replacement-Horse-Battery-Staple!8"
        })).EnsureSuccessStatusCode();

        var reset = await administrator.PostAsJsonAsync(
            $"/api/v1/platform/users/{user.Id}/reset-password",
            new
            {
                TemporaryPassword = "Reset-Horse-Battery-Staple!8",
                Reason = "User reported a compromised password"
            });

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await userClient.GetAsync("/api/v1/auth/me")).StatusCode);
        using var relogin = factory.CreateHttpsClient();
        (await relogin.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "reset.user",
            Password = "Reset-Horse-Battery-Staple!8",
            RememberMe = false
        })).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await relogin.GetAsync("/api/v1/auth/me")).StatusCode);
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

    private static async Task<CreatedUser> CreateUserAsync(
        HttpClient client,
        string username,
        string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/platform/users", new
        {
            Username = username,
            Email = $"{username}@example.test",
            DisplayName = username,
            Password = password,
            RoleNames = Array.Empty<string>(),
            Reason = "Password lifecycle test"
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedUser>())!;
    }

    private sealed record CreatedUser(Guid Id);
}
