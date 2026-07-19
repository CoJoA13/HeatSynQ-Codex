namespace HeatSynQ.Platform.Domain.Security;

public sealed record PlatformPermission(
    string Key,
    string Module,
    string Action,
    string Description);

public static class PlatformPermissionCatalog
{
    public static readonly IReadOnlyList<PlatformPermission> All =
    [
        new("platform.users.view", "Platform", "View users", "View user accounts and active sessions."),
        new("platform.users.edit", "Platform", "Edit users", "Create, update, disable, and restore user accounts."),
        new("platform.sessions.revoke", "Platform", "Revoke sessions", "Immediately revoke another user's sessions."),
        new("platform.roles.view", "Platform", "View roles", "View roles and permission grants."),
        new("platform.roles.edit", "Platform", "Edit roles", "Create and update roles and grants."),
        new("platform.permissions.override", "Platform", "Override permissions", "Create expiring or permanent per-user permission overrides."),
        new("platform.audit.view", "Platform", "View audit", "Search security and administration audit history."),
        new("platform.audit.export", "Platform", "Export audit", "Export security audit history."),
        new("platform.settings.view", "Platform", "View settings", "View company, facility, numbering, and platform settings."),
        new("platform.settings.edit", "Platform", "Edit settings", "Change company, facility, numbering, and platform settings."),
        new("platform.health.view", "Platform", "View health", "View database, queue, storage, and backup health."),
        new("platform.support.override", "Platform", "Support override", "Perform audited support-only corrective actions.")
    ];
}
