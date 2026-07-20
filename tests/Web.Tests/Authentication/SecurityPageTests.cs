using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Buffers.Binary;
using HeatSynQ.Platform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HeatSynQ.Web.Tests.Authentication;

public sealed class SecurityPageTests
{
    [Fact]
    public async Task Security_page_renders_mfa_setup_and_enabled_states()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);

        var disabledPage = await client.GetAsync("/account/security");
        var disabledHtml = await disabledPage.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, disabledPage.StatusCode);
        Assert.Contains("href=\"/account/password\"", disabledHtml);
        Assert.Contains("Change password", disabledHtml);
        Assert.Contains("data-mfa-begin", disabledHtml);
        Assert.Contains("data-api-form=\"enable-mfa\"", disabledHtml);

        (await client.PostAsync("/api/v1/auth/mfa/authenticator", content: null))
            .EnsureSuccessStatusCode();
        var code = await GenerateAuthenticatorCodeAsync(factory.Services);
        (await client.PostAsJsonAsync(
            "/api/v1/auth/mfa/authenticator/enable",
            new { Code = code })).EnsureSuccessStatusCode();

        var enabledPage = await client.GetAsync("/account/security");
        var enabledHtml = await enabledPage.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, enabledPage.StatusCode);
        Assert.Contains("Authenticator enabled", enabledHtml);
        Assert.Contains("10 recovery codes remaining", enabledHtml);
        Assert.Contains("data-api-form=\"disable-mfa\"", enabledHtml);
    }

    private static async Task<string> GenerateAuthenticatorCodeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByNameAsync("admin");
        var key = await userManager.GetAuthenticatorKeyAsync(user!);
        var keyBytes = DecodeBase32(key!);
        Span<byte> counterBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(
            counterBytes,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(keyBytes, counterBytes);
        var offset = hash[^1] & 0x0F;
        var binaryCode =
            ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);
        return (binaryCode % 1_000_000).ToString("D6");
    }

    private static byte[] DecodeBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        var buffer = 0;
        var bitsInBuffer = 0;
        foreach (var character in value.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character);
            bitsInBuffer += 5;
            if (bitsInBuffer >= 8)
            {
                output.Add((byte)(buffer >> (bitsInBuffer - 8)));
                bitsInBuffer -= 8;
            }
        }

        return output.ToArray();
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
