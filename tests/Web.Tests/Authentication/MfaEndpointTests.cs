using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using HeatSynQ.Platform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HeatSynQ.Web.Tests.Authentication;

public sealed class MfaEndpointTests
{
    [Fact]
    public async Task User_can_enroll_authenticator_and_complete_challenged_login()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);

        var enrollmentResponse = await client.PostAsync(
            "/api/v1/auth/mfa/authenticator",
            content: null);
        var enrollment = await enrollmentResponse.Content
            .ReadFromJsonAsync<AuthenticatorEnrollment>();

        Assert.Equal(HttpStatusCode.OK, enrollmentResponse.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(enrollment!.SharedKey));
        Assert.StartsWith("otpauth://totp/", enrollment.AuthenticatorUri);
        var enrollmentCode = await GenerateAuthenticatorCodeAsync(factory.Services, "admin");
        Assert.False(string.IsNullOrWhiteSpace(enrollmentCode));
        var enableResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/mfa/authenticator/enable",
            new { Code = enrollmentCode });

        Assert.True(
            enableResponse.StatusCode == HttpStatusCode.OK,
            $"Expected OK but received {enableResponse.StatusCode}: " +
            await enableResponse.Content.ReadAsStringAsync());
        var enabled = await enableResponse.Content.ReadFromJsonAsync<MfaEnabled>();
        Assert.Equal(10, enabled!.RecoveryCodes.Length);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            Assert.True((await db.Users.SingleAsync(x => x.UserName == "admin")).TwoFactorEnabled);
            Assert.Contains(
                await db.AuditEvents.ToArrayAsync(),
                x => x.Action == "platform.mfa.enabled");
        }

        (await client.PostAsync("/api/v1/auth/logout", content: null))
            .EnsureSuccessStatusCode();
        var passwordResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "admin",
            Password = "Correct-Horse-Battery-Staple!7",
            RememberMe = false
        });
        var challenge = await passwordResponse.Content.ReadFromJsonAsync<MfaChallenge>();

        Assert.Equal(HttpStatusCode.Unauthorized, passwordResponse.StatusCode);
        Assert.True(challenge!.RequiresTwoFactor);
        var loginCode = await GenerateAuthenticatorCodeAsync(factory.Services, "admin");
        var twoFactorResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/login/2fa",
            new
            {
                Code = loginCode,
                RememberMe = false,
                RememberClient = false
            });

        Assert.Equal(HttpStatusCode.NoContent, twoFactorResponse.StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    private static async Task<string> GenerateAuthenticatorCodeAsync(
        IServiceProvider services,
        string username)
    {
        await using var scope = services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByNameAsync(username);
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

    private sealed record AuthenticatorEnrollment(string SharedKey, string AuthenticatorUri);

    private sealed record MfaEnabled(string[] RecoveryCodes);

    private sealed record MfaChallenge(bool RequiresTwoFactor);
}
