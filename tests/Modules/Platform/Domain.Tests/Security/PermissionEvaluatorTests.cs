using HeatSynQ.Platform.Domain.Security;

namespace HeatSynQ.Platform.Domain.Tests.Security;

public sealed class PermissionEvaluatorTests
{
    private const string Permission = "platform.users.edit";

    [Fact]
    public void Explicit_user_deny_wins_over_user_allow_and_role_grant()
    {
        var decision = PermissionEvaluator.Evaluate(
            Permission,
            roleGrants: [Permission],
            userGrants:
            [
                new UserPermissionOverride(Permission, PermissionOverrideEffect.Allow),
                new UserPermissionOverride(Permission, PermissionOverrideEffect.Deny)
            ],
            now: DateTimeOffset.Parse("2026-07-19T15:00:00-05:00"));

        Assert.Equal(PermissionDecision.DeniedByUserOverride, decision);
    }

    [Fact]
    public void Explicit_user_allow_wins_when_no_active_deny_exists()
    {
        var decision = PermissionEvaluator.Evaluate(
            Permission,
            roleGrants: [],
            userGrants: [new UserPermissionOverride(Permission, PermissionOverrideEffect.Allow)],
            now: DateTimeOffset.Parse("2026-07-19T15:00:00-05:00"));

        Assert.Equal(PermissionDecision.AllowedByUserOverride, decision);
    }

    [Fact]
    public void Role_grant_allows_when_no_user_override_exists()
    {
        var decision = PermissionEvaluator.Evaluate(
            Permission,
            roleGrants: [Permission],
            userGrants: [],
            now: DateTimeOffset.Parse("2026-07-19T15:00:00-05:00"));

        Assert.Equal(PermissionDecision.AllowedByRole, decision);
    }

    [Fact]
    public void Default_is_deny()
    {
        var decision = PermissionEvaluator.Evaluate(
            Permission,
            roleGrants: [],
            userGrants: [],
            now: DateTimeOffset.Parse("2026-07-19T15:00:00-05:00"));

        Assert.Equal(PermissionDecision.DeniedByDefault, decision);
    }

    [Fact]
    public void Expired_override_is_ignored()
    {
        var decision = PermissionEvaluator.Evaluate(
            Permission,
            roleGrants: [Permission],
            userGrants:
            [
                new UserPermissionOverride(
                    Permission,
                    PermissionOverrideEffect.Deny,
                    ExpiresAt: DateTimeOffset.Parse("2026-07-19T14:59:59-05:00"))
            ],
            now: DateTimeOffset.Parse("2026-07-19T15:00:00-05:00"));

        Assert.Equal(PermissionDecision.AllowedByRole, decision);
    }
}
