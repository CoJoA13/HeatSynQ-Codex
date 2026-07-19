using System.Net;
using System.Net.Http.Json;

namespace HeatSynQ.Web.Tests.Administration;

public sealed class RoleAdministrationPageTests
{
    [Fact]
    public async Task Roles_page_renders_persisted_roles_and_permission_catalog()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        var createResponse = await client.PostAsJsonAsync("/api/v1/platform/roles", new
        {
            Name = "Metallurgy",
            Description = "Review heat-treatment specifications and process controls.",
            PermissionKeys = new[]
            {
                "platform.audit.view",
                "platform.health.view"
            },
            Reason = "Initial metallurgy role"
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CreatedRole>();

        var response = await client.GetAsync($"/admin/roles?role={created!.Id}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Metallurgy", html);
        Assert.Contains("Review heat-treatment specifications", html);
        Assert.Contains("platform.audit.view", html);
        Assert.Contains("View audit", html);
        Assert.Contains("id=\"add-role-dialog\"", html);
        Assert.Contains("data-api-form=\"update-role\"", html);
        Assert.Contains($"action=\"/api/v1/platform/roles/{created.Id}/permissions\"", html);
        Assert.Contains("name=\"PermissionKeys\"", html);
        Assert.Contains("name=\"Reason\"", html);
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
}
