using System.Net;
using System.Net.Http.Json;

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
