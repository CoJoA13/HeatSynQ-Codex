using HeatSynQ.Platform.Domain.Security;
using HeatSynQ.Platform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HeatSynQ.Web.Security;

public sealed record PermissionRequirement(string PermissionKey) : IAuthorizationRequirement;

public sealed class PermissionAuthorizationHandler(
    PlatformDbContext db,
    UserManager<ApplicationUser> userManager,
    TimeProvider timeProvider)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var userIdText = userManager.GetUserId(context.User);
        if (!Guid.TryParse(userIdText, out var userId))
        {
            return;
        }

        var user = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == userId);
        if (user is null || !user.IsEnabled)
        {
            return;
        }

        var roleIds = db.UserRoles
            .Where(x => x.UserId == userId)
            .Select(x => x.RoleId);
        var roleGrants = await db.RolePermissions
            .Where(x => roleIds.Contains(x.RoleId))
            .Where(x => x.PermissionKey == requirement.PermissionKey)
            .Select(x => x.PermissionKey)
            .ToArrayAsync();
        var overrides = await db.UserPermissionOverrides
            .Where(x => x.UserId == userId)
            .Where(x => x.PermissionKey == requirement.PermissionKey)
            .Select(x => new UserPermissionOverride(
                x.PermissionKey,
                x.IsAllowed ? PermissionOverrideEffect.Allow : PermissionOverrideEffect.Deny,
                x.ExpiresAt))
            .ToArrayAsync();

        var decision = PermissionEvaluator.Evaluate(
            requirement.PermissionKey,
            roleGrants,
            overrides,
            timeProvider.GetUtcNow());

        if (decision is PermissionDecision.AllowedByRole
            or PermissionDecision.AllowedByUserOverride)
        {
            context.Succeed(requirement);
        }
    }
}
