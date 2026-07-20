using System.Net;
using System.Net.Http.Json;

namespace HeatSynQ.Web.Tests.Bootstrap;

public sealed class AdministratorBootstrapEndpointTests
{
    [Fact]
    public async Task Creates_only_the_first_administrator()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        var request = new
        {
            BootstrapSecret = "test-bootstrap-secret",
            Username = "admin",
            Email = "admin@example.test",
            DisplayName = "Platform Administrator",
            Password = "Correct-Horse-Battery-Staple!7"
        };

        var first = await client.PostAsJsonAsync("/api/v1/platform/bootstrap", request);
        var second = await client.PostAsJsonAsync("/api/v1/platform/bootstrap", request);

        Assert.True(
            first.StatusCode == HttpStatusCode.Created,
            $"Expected Created but received {first.StatusCode}: {await first.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Rejects_an_incorrect_bootstrap_secret()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();

        var response = await client.PostAsJsonAsync("/api/v1/platform/bootstrap", new
        {
            BootstrapSecret = "wrong",
            Username = "admin",
            Email = "admin@example.test",
            DisplayName = "Platform Administrator",
            Password = "Correct-Horse-Battery-Staple!7"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
