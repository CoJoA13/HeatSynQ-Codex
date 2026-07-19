using HeatSynQ.Platform.Domain.Security;

namespace HeatSynQ.Platform.Domain.Tests.Security;

public sealed class AdministratorBootstrapPolicyTests
{
    [Fact]
    public void Allows_first_administrator_when_secret_matches_and_no_users_exist()
    {
        var decision = AdministratorBootstrapPolicy.Evaluate(
            existingUserCount: 0,
            configuredSecret: "correct-horse-battery-staple",
            suppliedSecret: "correct-horse-battery-staple");

        Assert.Equal(AdministratorBootstrapDecision.Allowed, decision);
    }

    [Fact]
    public void Refuses_bootstrap_after_any_user_exists()
    {
        var decision = AdministratorBootstrapPolicy.Evaluate(
            existingUserCount: 1,
            configuredSecret: "correct-horse-battery-staple",
            suppliedSecret: "correct-horse-battery-staple");

        Assert.Equal(AdministratorBootstrapDecision.AlreadyProvisioned, decision);
    }

    [Fact]
    public void Refuses_missing_configuration()
    {
        var decision = AdministratorBootstrapPolicy.Evaluate(
            existingUserCount: 0,
            configuredSecret: null,
            suppliedSecret: "correct-horse-battery-staple");

        Assert.Equal(AdministratorBootstrapDecision.NotConfigured, decision);
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong")]
    [InlineData("correct-horse-battery-staplE")]
    public void Refuses_incorrect_secret(string suppliedSecret)
    {
        var decision = AdministratorBootstrapPolicy.Evaluate(
            existingUserCount: 0,
            configuredSecret: "correct-horse-battery-staple",
            suppliedSecret: suppliedSecret);

        Assert.Equal(AdministratorBootstrapDecision.InvalidSecret, decision);
    }
}
