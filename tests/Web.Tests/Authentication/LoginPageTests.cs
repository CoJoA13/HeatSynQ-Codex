using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace HeatSynQ.Web.Tests.Authentication;

public sealed class LoginPageTests
{
    [Fact]
    public async Task Signed_in_user_chip_links_to_account_security()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        await client.PostAsJsonAsync("/api/v1/platform/bootstrap", new
        {
            BootstrapSecret = "test-bootstrap-secret",
            Username = "admin",
            Email = "admin@example.test",
            DisplayName = "Platform Administrator",
            Password = "Correct-Horse-Battery-Staple!7"
        });
        (await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Username = "admin",
            Password = "Correct-Horse-Battery-Staple!7",
            RememberMe = false
        })).EnsureSuccessStatusCode();

        var html = await client.GetStringAsync("/");

        Assert.Contains("class=\"user-chip\" href=\"/account/security\"", html);
    }

    [Fact]
    public async Task Browser_form_uses_antiforgery_and_redirects_after_login()
    {
        await using var factory = new PlatformWebApplicationFactory();
        using var client = factory.CreateHttpsClient(allowAutoRedirect: false);
        await client.PostAsJsonAsync("/api/v1/platform/bootstrap", new
        {
            BootstrapSecret = "test-bootstrap-secret",
            Username = "admin",
            Email = "admin@example.test",
            DisplayName = "Platform Administrator",
            Password = "Correct-Horse-Battery-Staple!7"
        });
        var loginPage = await client.GetAsync("/login");
        var html = await loginPage.Content.ReadAsStringAsync();
        var token = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant).Groups[1].Value;

        Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(token), html);
        var antiforgeryCookie = loginPage.Headers
            .GetValues("Set-Cookie")
            .Single(value => value.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase))
            .Split(';', 2)[0];

        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/account/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Username"] = "admin",
                ["Password"] = "Correct-Horse-Battery-Staple!7",
                ["__RequestVerificationToken"] = token
            })
        };
        loginRequest.Headers.Add("Cookie", antiforgeryCookie);
        var response = await client.SendAsync(loginRequest);

        Assert.True(
            response.StatusCode == HttpStatusCode.Redirect,
            $"Expected Redirect but received {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }
}
