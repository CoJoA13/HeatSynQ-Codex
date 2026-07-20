using System.Net;
using System.Net.Http.Json;
using HeatSynQ.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HeatSynQ.Web.Tests.Authentication;

public sealed class LoginEndpointTests
{
    [Fact]
    public async Task Valid_credentials_create_an_authenticated_session()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAsync(client);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "admin",
            Password = "Correct-Horse-Battery-Staple!7",
            RememberMe = false
        });
        var me = await client.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var identity = await me.Content.ReadFromJsonAsync<CurrentIdentity>();
        Assert.Equal("admin", identity?.Username);
        Assert.Contains("Administrator", identity?.Roles ?? []);
    }

    [Fact]
    public async Task Invalid_credentials_return_the_same_generic_error()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "admin",
            Password = "wrong",
            RememberMe = false
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Invalid username or password", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Me_requires_an_authenticated_session()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();

        var response = await client.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Revoking_sessions_invalidates_the_current_cookie_immediately()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAsync(client);
        await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "admin",
            Password = "Correct-Horse-Battery-Staple!7",
            RememberMe = false
        });

        var revoke = await client.PostAsync("/api/v1/auth/revoke-sessions", content: null);
        var me = await client.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Forwarded_clients_receive_independent_authentication_rate_limits()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        using (var bootstrap = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/platform/bootstrap"))
        {
            bootstrap.Headers.Add("X-Forwarded-For", "192.0.2.10");
            bootstrap.Content = JsonContent.Create(new
            {
                BootstrapSecret = "test-bootstrap-secret",
                Username = "admin",
                Email = "admin@example.test",
                DisplayName = "Platform Administrator",
                Password = "Correct-Horse-Battery-Staple!7"
            });
            (await client.SendAsync(bootstrap)).EnsureSuccessStatusCode();
        }
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var invalid = LoginRequest(
                "nonexistent",
                "wrong-password",
                "192.0.2.20");
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await client.SendAsync(invalid)).StatusCode);
        }

        using var valid = LoginRequest(
            "admin",
            "Correct-Horse-Battery-Staple!7",
            "192.0.2.30");
        var response = await client.SendAsync(valid);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_identity_is_locked_after_repeated_password_failures()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAsync(client);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var invalid = LoginRequest(
                "admin",
                "wrong-password",
                $"192.0.2.{40 + attempt}");
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await client.SendAsync(invalid)).StatusCode);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var user = await db.Users.SingleAsync(x => x.UserName == "admin");
        Assert.True(user.LockoutEnabled);
        Assert.NotNull(user.LockoutEnd);
    }

    private static HttpRequestMessage LoginRequest(
        string username,
        string password,
        string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new
            {
                Username = username,
                Password = password,
                RememberMe = false
            })
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return request;
    }

    private static Task<HttpResponseMessage> BootstrapAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/v1/platform/bootstrap", new
        {
            BootstrapSecret = "test-bootstrap-secret",
            Username = "admin",
            Email = "admin@example.test",
            DisplayName = "Platform Administrator",
            Password = "Correct-Horse-Battery-Staple!7"
        });

    private sealed record CurrentIdentity(string Username, string DisplayName, string[] Roles);
}
