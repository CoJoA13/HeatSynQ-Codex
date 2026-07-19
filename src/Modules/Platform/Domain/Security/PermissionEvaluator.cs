namespace HeatSynQ.Platform.Domain.Security;

public enum PermissionOverrideEffect
{
    Allow,
    Deny
}

public enum PermissionDecision
{
    DeniedByDefault,
    AllowedByRole,
    AllowedByUserOverride,
    DeniedByUserOverride
}

public sealed record UserPermissionOverride(
    string PermissionKey,
    PermissionOverrideEffect Effect,
    DateTimeOffset? ExpiresAt = null);

public static class PermissionEvaluator
{
    public static PermissionDecision Evaluate(
        string permissionKey,
        IReadOnlyCollection<string> roleGrants,
        IReadOnlyCollection<UserPermissionOverride> userGrants,
        DateTimeOffset now)
    {
        var activeOverrides = userGrants
            .Where(x => x.PermissionKey == permissionKey)
            .Where(x => x.ExpiresAt is null || x.ExpiresAt > now)
            .ToArray();

        if (activeOverrides.Any(x => x.Effect == PermissionOverrideEffect.Deny))
        {
            return PermissionDecision.DeniedByUserOverride;
        }

        if (activeOverrides.Any(x => x.Effect == PermissionOverrideEffect.Allow))
        {
            return PermissionDecision.AllowedByUserOverride;
        }

        return roleGrants.Contains(permissionKey)
            ? PermissionDecision.AllowedByRole
            : PermissionDecision.DeniedByDefault;
    }
}
