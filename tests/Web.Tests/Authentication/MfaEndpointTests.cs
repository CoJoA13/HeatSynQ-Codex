using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
        var statusResponse = await client.GetAsync("/api/v1/auth/mfa");
        var status = await statusResponse.Content.ReadFromJsonAsync<MfaStatus>();
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        Assert.True(status!.IsEnabled);
        Assert.Equal(10, status.RecoveryCodesRemaining);
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

    [Fact]
    public async Task Recovery_code_is_consumed_after_successful_login()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        (await client.PostAsync("/api/v1/auth/mfa/authenticator", content: null))
            .EnsureSuccessStatusCode();
        var enrollmentCode = await GenerateAuthenticatorCodeAsync(factory.Services, "admin");
        var enableResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/mfa/authenticator/enable",
            new { Code = enrollmentCode });
        enableResponse.EnsureSuccessStatusCode();
        var enabled = await enableResponse.Content.ReadFromJsonAsync<MfaEnabled>();
        var recoveryCode = enabled!.RecoveryCodes[0];
        (await client.PostAsync("/api/v1/auth/logout", content: null))
            .EnsureSuccessStatusCode();
        var challengeResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "admin",
            Password = "Correct-Horse-Battery-Staple!7",
            RememberMe = false
        });
        Assert.Equal(HttpStatusCode.Unauthorized, challengeResponse.StatusCode);

        var firstUse = await client.PostAsJsonAsync(
            "/api/v1/auth/login/2fa/recovery",
            new { RecoveryCode = recoveryCode });
        var secondUse = await client.PostAsJsonAsync(
            "/api/v1/auth/login/2fa/recovery",
            new { RecoveryCode = recoveryCode });

        Assert.Equal(HttpStatusCode.NoContent, firstUse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, secondUse.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByNameAsync("admin");
        Assert.Equal(9, await userManager.CountRecoveryCodesAsync(user!));
    }

    [Fact]
    public async Task Disabling_mfa_requires_current_password_and_restores_password_login()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        (await client.PostAsync("/api/v1/auth/mfa/authenticator", content: null))
            .EnsureSuccessStatusCode();
        var enrollmentCode = await GenerateAuthenticatorCodeAsync(factory.Services, "admin");
        (await client.PostAsJsonAsync(
            "/api/v1/auth/mfa/authenticator/enable",
            new { Code = enrollmentCode })).EnsureSuccessStatusCode();

        var wrongPassword = await client.PostAsJsonAsync(
            "/api/v1/auth/mfa/disable",
            new { CurrentPassword = "not-the-password" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongPassword.StatusCode);
        var disableResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/mfa/disable",
            new { CurrentPassword = "Correct-Horse-Battery-Staple!7" });

        Assert.Equal(HttpStatusCode.NoContent, disableResponse.StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            Assert.False((await db.Users.SingleAsync(x => x.UserName == "admin")).TwoFactorEnabled);
            Assert.Contains(
                await db.AuditEvents.ToArrayAsync(),
                x => x.Action == "platform.mfa.disabled");
        }

        (await client.PostAsync("/api/v1/auth/logout", content: null))
            .EnsureSuccessStatusCode();
        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "admin",
            Password = "Correct-Horse-Battery-Staple!7",
            RememberMe = false
        });
        Assert.Equal(HttpStatusCode.NoContent, loginResponse.StatusCode);
    }

    [Fact]
    public async Task Enabled_mfa_secret_cannot_be_exposed_by_restarting_enrollment()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        (await client.PostAsync("/api/v1/auth/mfa/authenticator", content: null))
            .EnsureSuccessStatusCode();
        var enrollmentCode = await GenerateAuthenticatorCodeAsync(factory.Services, "admin");
        (await client.PostAsJsonAsync(
            "/api/v1/auth/mfa/authenticator/enable",
            new { Code = enrollmentCode })).EnsureSuccessStatusCode();

        var response = await client.PostAsync(
            "/api/v1/auth/mfa/authenticator",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("sharedKey", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Browser_login_redirects_to_mfa_form_and_accepts_authenticator_code()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient(allowAutoRedirect: false);
        await BootstrapAndLoginAsync(client);
        (await client.PostAsync("/api/v1/auth/mfa/authenticator", content: null))
            .EnsureSuccessStatusCode();
        var enrollmentCode = await GenerateAuthenticatorCodeAsync(factory.Services, "admin");
        (await client.PostAsJsonAsync(
            "/api/v1/auth/mfa/authenticator/enable",
            new { Code = enrollmentCode })).EnsureSuccessStatusCode();
        (await client.PostAsync("/api/v1/auth/logout", content: null))
            .EnsureSuccessStatusCode();

        var passwordResponse = await PostBrowserFormAsync(
            client,
            "/login",
            "/account/login",
            new Dictionary<string, string>
            {
                ["Username"] = "admin",
                ["Password"] = "Correct-Horse-Battery-Staple!7"
            });

        Assert.Equal(HttpStatusCode.Redirect, passwordResponse.StatusCode);
        Assert.Equal("/login/2fa", passwordResponse.Headers.Location?.OriginalString);
        var loginCode = await GenerateAuthenticatorCodeAsync(factory.Services, "admin");
        var twoFactorResponse = await PostBrowserFormAsync(
            client,
            "/login/2fa",
            "/account/login/2fa",
            new Dictionary<string, string>
            {
                ["Code"] = loginCode
            });

        Assert.Equal(HttpStatusCode.Redirect, twoFactorResponse.StatusCode);
        Assert.Equal("/", twoFactorResponse.Headers.Location?.OriginalString);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Browser_mfa_challenge_supports_recovery_code_login()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient(allowAutoRedirect: false);
        await BootstrapAndLoginAsync(client);
        (await client.PostAsync("/api/v1/auth/mfa/authenticator", content: null))
            .EnsureSuccessStatusCode();
        var enrollmentCode = await GenerateAuthenticatorCodeAsync(factory.Services, "admin");
        var enabledResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/mfa/authenticator/enable",
            new { Code = enrollmentCode });
        var enabled = await enabledResponse.Content.ReadFromJsonAsync<MfaEnabled>();
        (await client.PostAsync("/api/v1/auth/logout", content: null))
            .EnsureSuccessStatusCode();
        await PostBrowserFormAsync(
            client,
            "/login",
            "/account/login",
            new Dictionary<string, string>
            {
                ["Username"] = "admin",
                ["Password"] = "Correct-Horse-Battery-Staple!7"
            });

        var recoveryPage = await client.GetAsync("/login/2fa/recovery");
        var html = await recoveryPage.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, recoveryPage.StatusCode);
        Assert.Contains("action=\"/account/login/2fa/recovery\"", html);
        var response = await PostBrowserFormAsync(
            client,
            "/login/2fa/recovery",
            "/account/login/2fa/recovery",
            new Dictionary<string, string>
            {
                ["RecoveryCode"] = enabled!.RecoveryCodes[0]
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Browser_mfa_preserves_persistent_sign_in_choice()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient(allowAutoRedirect: false);
        await BootstrapAndLoginAsync(client);
        (await client.PostAsync("/api/v1/auth/mfa/authenticator", content: null))
            .EnsureSuccessStatusCode();
        var enrollmentCode = await GenerateAuthenticatorCodeAsync(factory.Services, "admin");
        (await client.PostAsJsonAsync(
            "/api/v1/auth/mfa/authenticator/enable",
            new { Code = enrollmentCode })).EnsureSuccessStatusCode();
        (await client.PostAsync("/api/v1/auth/logout", content: null))
            .EnsureSuccessStatusCode();

        var challenge = await PostBrowserFormAsync(
            client,
            "/login",
            "/account/login",
            new Dictionary<string, string>
            {
                ["Username"] = "admin",
                ["Password"] = "Correct-Horse-Battery-Staple!7",
                ["RememberMe"] = "true"
            });

        Assert.Equal("/login/2fa?rememberMe=true", challenge.Headers.Location?.OriginalString);
        var page = await client.GetStringAsync(challenge.Headers.Location);
        Assert.Contains("name=\"RememberMe\" value=\"true\"", page);
        var loginCode = await GenerateAuthenticatorCodeAsync(factory.Services, "admin");
        var response = await PostBrowserFormAsync(
            client,
            challenge.Headers.Location!.OriginalString,
            "/account/login/2fa",
            new Dictionary<string, string>
            {
                ["Code"] = loginCode,
                ["RememberMe"] = "true"
            });

        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            value => value.Contains("expires=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Failed_browser_mfa_attempt_preserves_persistent_sign_in_choice()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient(allowAutoRedirect: false);
        await BootstrapAndLoginAsync(client);
        (await client.PostAsync("/api/v1/auth/mfa/authenticator", null)).EnsureSuccessStatusCode();
        var code = await GenerateAuthenticatorCodeAsync(factory.Services, "admin");
        (await client.PostAsJsonAsync(
            "/api/v1/auth/mfa/authenticator/enable",
            new { Code = code })).EnsureSuccessStatusCode();
        (await client.PostAsync("/api/v1/auth/logout", null)).EnsureSuccessStatusCode();
        await PostBrowserFormAsync(client, "/login", "/account/login",
            new Dictionary<string, string>
            {
                ["Username"] = "admin",
                ["Password"] = "Correct-Horse-Battery-Staple!7",
                ["RememberMe"] = "true"
            });

        var failed = await PostBrowserFormAsync(
            client,
            "/login/2fa?rememberMe=true",
            "/account/login/2fa",
            new Dictionary<string, string>
            {
                ["Code"] = "000000",
                ["RememberMe"] = "true"
            });

        Assert.Equal(
            "/login/2fa?error=invalid&rememberMe=true",
            failed.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Mfa_mutations_preserve_the_authenticated_session_claim()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await BootstrapAndLoginAsync(client);
        (await client.PostAsync("/api/v1/auth/mfa/authenticator", null)).EnsureSuccessStatusCode();
        var code = await GenerateAuthenticatorCodeAsync(factory.Services, "admin");
        (await client.PostAsJsonAsync(
            "/api/v1/auth/mfa/authenticator/enable",
            new { Code = code })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(
            "/api/v1/auth/mfa/disable",
            new { CurrentPassword = "Correct-Horse-Battery-Staple!7" })).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var sessionId = (await db.Sessions.SingleAsync()).Id.ToString();
        var disabled = await db.AuditEvents.SingleAsync(
            x => x.Action == "platform.mfa.disabled");
        Assert.Equal(sessionId, disabled.SessionId);
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

    private static async Task<HttpResponseMessage> PostBrowserFormAsync(
        HttpClient client,
        string formPage,
        string action,
        Dictionary<string, string> values)
    {
        var page = await client.GetAsync(formPage);
        page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant).Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(token), html);
        values["__RequestVerificationToken"] = token;
        using var request = new HttpRequestMessage(HttpMethod.Post, action)
        {
            Content = new FormUrlEncodedContent(values)
        };
        if (page.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            var antiforgeryCookie = setCookies
                .Single(value => value.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase))
                .Split(';', 2)[0];
            request.Headers.Add("Cookie", antiforgeryCookie);
        }

        return await client.SendAsync(request);
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

    private sealed record MfaStatus(bool IsEnabled, int RecoveryCodesRemaining);
}
