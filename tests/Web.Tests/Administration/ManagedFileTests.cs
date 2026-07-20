using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace HeatSynQ.Web.Tests.Administration;

public sealed class ManagedFileTests
{
    [Fact]
    public async Task Authenticated_upload_is_checksummed_revisioned_and_downloadable()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        var payload = "controlled traveler content"u8.ToArray();

        var first = await UploadAsync(client, payload);
        var second = await UploadAsync(client, payload);

        Assert.Equal(1, first.Revision);
        Assert.Equal(2, second.Revision);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), first.ChecksumSha256);
        var download = await client.GetAsync($"/api/v1/platform/files/{first.Id}/content");
        download.EnsureSuccessStatusCode();
        Assert.Equal(payload, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Anonymous_user_cannot_download_managed_file()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var administrator = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(administrator);
        var uploaded = await UploadAsync(administrator, "private"u8.ToArray());
        using var anonymous = factory.CreateHttpsClient();

        var response = await anonymous.GetAsync($"/api/v1/platform/files/{uploaded.Id}/content");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<StoredFileSummary> UploadAsync(HttpClient client, byte[] payload)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("production"), "category");
        content.Add(new StringContent("Job"), "entityType");
        content.Add(new StringContent("JOB-1001"), "entityId");
        content.Add(new StringContent("10"), "retentionYears");
        content.Add(new StringContent("Controlled traveler upload"), "reason");
        var file = new ByteArrayContent(payload);
        file.Headers.ContentType = new("application/pdf");
        content.Add(file, "file", "traveler.pdf");
        client.DefaultRequestHeaders.Remove("X-HeatSynQ-Request");
        client.DefaultRequestHeaders.Add("X-HeatSynQ-Request", "managed-file-upload");
        var response = await client.PostAsync("/api/v1/platform/files", content);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StoredFileSummary>())!;
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

    private sealed record StoredFileSummary(
        Guid Id,
        int Revision,
        string ChecksumSha256);
}
