using System.Net;
using System.Net.Http.Json;

namespace HeatSynQ.Web.Tests.Administration;

public sealed class PlatformAdministrationPageTests
{
    [Theory]
    [InlineData("/admin/facility", "Company &amp; facility")]
    [InlineData("/admin/audit", "Audit history")]
    [InlineData("/admin/system", "System health")]
    public async Task Administration_pages_require_login_and_render_for_administrator(
        string path,
        string expectedHeading)
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var anonymous = factory.CreateHttpsClient(allowAutoRedirect: false);
        var redirect = await anonymous.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/login", redirect.Headers.Location?.AbsolutePath);

        using var administrator = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(administrator);
        var response = await administrator.GetAsync(path);
        response.EnsureSuccessStatusCode();
        Assert.Contains(expectedHeading, await response.Content.ReadAsStringAsync());
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
}
