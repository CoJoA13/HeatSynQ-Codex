using System.Net;
using System.Net.Http.Json;

namespace HeatSynQ.Web.Tests.Authentication;

public sealed class SessionManagementTests
{
    [Fact]
    public async Task Administrator_can_list_session_and_logout_marks_it_ended()
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
                Reason = "New operator"
            });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CreatedUser>();
        operatorClient.DefaultRequestHeaders.UserAgent.ParseAdd("HeatSynQ-Test-Workstation/1.0");
        operatorClient.DefaultRequestHeaders.Add("X-HeatSynQ-Workstation", "FURNACE-01");
        (await operatorClient.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "operator",
            Password = "Correct-Horse-Battery-Staple!8",
            RememberMe = false
        })).EnsureSuccessStatusCode();

        var activeResponse = await administratorClient.GetAsync(
            $"/api/v1/platform/users/{created!.Id}/sessions");
        var activeSessions = await activeResponse.Content
            .ReadFromJsonAsync<SessionSummary[]>();

        Assert.Equal(HttpStatusCode.OK, activeResponse.StatusCode);
        var active = Assert.Single(activeSessions!);
        Assert.Equal("FURNACE-01", active.Workstation);
        Assert.Contains("HeatSynQ-Test-Workstation", active.UserAgent);
        Assert.Null(active.EndedAt);

        (await operatorClient.PostAsync("/api/v1/auth/logout", content: null))
            .EnsureSuccessStatusCode();
        var endedSessions = await (
            await administratorClient.GetAsync(
                $"/api/v1/platform/users/{created.Id}/sessions"))
            .Content.ReadFromJsonAsync<SessionSummary[]>();

        var ended = Assert.Single(endedSessions!);
        Assert.NotNull(ended.EndedAt);
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

    private sealed record SessionSummary(
        Guid Id,
        string Workstation,
        string UserAgent,
        DateTimeOffset CreatedAt,
        DateTimeOffset? EndedAt,
        DateTimeOffset? RevokedAt,
        string? RevokeReason);
}
