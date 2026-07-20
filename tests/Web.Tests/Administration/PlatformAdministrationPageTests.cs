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

    [Fact]
    public async Task Facility_page_exposes_release_action_for_active_legal_hold()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        var response = await client.PostAsJsonAsync("/api/v1/platform/legal-holds", new
        {
            Category = "quality",
            EntityType = "Job",
            EntityId = "J-1001",
            Reason = "Investigation"
        });
        var hold = await response.Content.ReadFromJsonAsync<LegalHold>();

        var html = await client.GetStringAsync("/admin/facility");

        Assert.Contains(
            $"action=\"/api/v1/platform/legal-holds/{hold!.Id}/release\"",
            html);
        Assert.Contains("data-api-form=\"release-legal-hold\"", html);
    }

    [Fact]
    public async Task Facility_editors_are_populated_from_persisted_values()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        (await client.PutAsJsonAsync("/api/v1/platform/number-sequences/JOB", new
        {
            Prefix = "HT-",
            NextValue = 9876,
            Padding = 8,
            Reason = "Custom job numbering"
        })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/v1/platform/retention-policies/quality", new
        {
            RetentionYears = 25,
            Reason = "Customer retention requirement"
        })).EnsureSuccessStatusCode();

        var html = await client.GetStringAsync("/admin/facility");

        Assert.Contains("name=\"Prefix\" value=\"HT-\"", html);
        Assert.Contains("name=\"NextValue\" type=\"number\" min=\"1\" value=\"9876\"", html);
        Assert.Contains("name=\"Padding\" type=\"number\" min=\"1\" max=\"20\" value=\"8\"", html);
        Assert.Contains("name=\"RetentionYears\" type=\"number\" min=\"1\" max=\"100\" value=\"25\"", html);
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

    private sealed record LegalHold(Guid Id);
}
