using System.Data;
using System.Security.Claims;
using System.Text.Json;
using HeatSynQ.Platform.Domain.Security;
using HeatSynQ.Platform.Infrastructure.Persistence;
using HeatSynQ.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HeatSynQ.Web.Endpoints;

public static class PlatformRoleEndpoints
{
    private const string SessionIdClaim = "heatsynq:session_id";
    public static IEndpointRouteBuilder MapPlatformRoleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/platform/roles", ListRolesAsync)
            .RequireAuthorization(new AuthorizationPolicyBuilder()
                .AddRequirements(new PermissionRequirement("platform.roles.view"))
                .Build());

        endpoints.MapPost("/api/v1/platform/roles", CreateRoleAsync)
            .RequireAuthorization(new AuthorizationPolicyBuilder()
                .AddRequirements(new PermissionRequirement("platform.roles.edit"))
                .Build());

        endpoints.MapPut(
                "/api/v1/platform/roles/{roleId:guid}/permissions",
                ReplaceRolePermissionsAsync)
            .RequireAuthorization(new AuthorizationPolicyBuilder()
                .AddRequirements(new PermissionRequirement("platform.roles.edit"))
                .Build());

        return endpoints;
    }

    private static async Task<IResult> ListRolesAsync(
        PlatformDbContext db,
        CancellationToken cancellationToken)
    {
        var roles = await db.Roles
            .AsNoTracking()
            .OrderByDescending(x => x.IsSystemRole)
            .ThenBy(x => x.Name)
            .ToArrayAsync(cancellationToken);
        var response = new List<object>(roles.Length);

        foreach (var role in roles)
        {
            var permissionKeys = await db.RolePermissions
                .Where(x => x.RoleId == role.Id)
                .Select(x => x.PermissionKey)
                .OrderBy(x => x)
                .ToArrayAsync(cancellationToken);
            var userCount = await db.UserRoles
                .CountAsync(x => x.RoleId == role.Id, cancellationToken);
            response.Add(new
            {
                role.Id,
                role.Name,
                role.Description,
                role.IsSystemRole,
                UserCount = userCount,
                PermissionKeys = permissionKeys
            });
        }

        return Results.Ok(response);
    }

    private static async Task<IResult> ReplaceRolePermissionsAsync(
        Guid roleId,
        ReplaceRolePermissionsRequest request,
        HttpContext httpContext,
        PlatformDbContext db,
        UserManager<ApplicationUser> userManager,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["reason"] = ["A reason is required for administrative changes."]
            });
        }

        var permissionKeys = (request.PermissionKeys ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var unknownKeys = FindUnknownPermissionKeys(permissionKeys);
        if (unknownKeys.Length > 0)
        {
            return UnknownPermissionKeys(unknownKeys);
        }

        var actor = await userManager.GetUserAsync(httpContext.User);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        var role = await db.Roles.SingleOrDefaultAsync(x => x.Id == roleId, cancellationToken);
        if (role is null)
        {
            return Results.NotFound();
        }

        if (role.IsSystemRole)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["roleId"] = ["System role permissions cannot be changed."]
            });
        }

        var existingGrants = await db.RolePermissions
            .Where(x => x.RoleId == roleId)
            .ToArrayAsync(cancellationToken);
        var beforeKeys = existingGrants
            .Select(x => x.PermissionKey)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        db.RolePermissions.RemoveRange(existingGrants);
        db.RolePermissions.AddRange(permissionKeys.Select(key => new RolePermission
        {
            RoleId = roleId,
            PermissionKey = key
        }));
        db.AuditEvents.Add(AuditEvent.Create(
            "platform.role.permissions_changed",
            "Role",
            role.Id.ToString(),
            actor.Id,
            AuditSessionId(httpContext),
            request.Reason.Trim(),
            JsonSerializer.Serialize(new { PermissionKeys = beforeKeys }),
            JsonSerializer.Serialize(new { PermissionKeys = permissionKeys }),
            DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateRoleAsync(
        CreateRoleRequest request,
        HttpContext httpContext,
        PlatformDbContext db,
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var actor = await userManager.GetUserAsync(httpContext.User);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        var permissionKeys = (request.PermissionKeys ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var unknownKeys = FindUnknownPermissionKeys(permissionKeys);
        if (unknownKeys.Length > 0)
        {
            return UnknownPermissionKeys(unknownKeys);
        }

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var role = new ApplicationRole
        {
            Id = Guid.NewGuid(),
            Name = request.Name!.Trim(),
            Description = request.Description!.Trim(),
            IsSystemRole = false
        };
        var createResult = await roleManager.CreateAsync(role);
        if (!createResult.Succeeded)
        {
            return IdentityErrors(createResult);
        }

        db.RolePermissions.AddRange(permissionKeys.Select(key => new RolePermission
        {
            RoleId = role.Id,
            PermissionKey = key
        }));
        db.AuditEvents.Add(AuditEvent.Create(
            "platform.role.created",
            "Role",
            role.Id.ToString(),
            actor.Id,
            AuditSessionId(httpContext),
            request.Reason!.Trim(),
            "{}",
            JsonSerializer.Serialize(new
            {
                role.Name,
                role.Description,
                PermissionKeys = permissionKeys
            }),
            DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.Created(
            $"/api/v1/platform/roles/{role.Id}",
            new { role.Id, role.Name, role.Description, PermissionKeys = permissionKeys });
    }

    private static Dictionary<string, string[]> Validate(CreateRoleRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Role name is required."];
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            errors["description"] = ["Role description is required."];
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            errors["reason"] = ["A reason is required for administrative changes."];
        }

        return errors;
    }

    private static string[] FindUnknownPermissionKeys(IEnumerable<string> permissionKeys)
    {
        var knownKeys = PlatformPermissionCatalog.All
            .Select(x => x.Key)
            .ToHashSet(StringComparer.Ordinal);
        return permissionKeys.Where(x => !knownKeys.Contains(x)).ToArray();
    }

    private static IResult UnknownPermissionKeys(IEnumerable<string> unknownKeys) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["permissionKeys"] = unknownKeys
                .Select(x => $"Unknown permission key: {x}")
                .ToArray()
        });

    private static IResult IdentityErrors(IdentityResult result)
    {
        var errors = result.Errors
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Code) ? "identity" : x.Code)
            .ToDictionary(x => x.Key, x => x.Select(error => error.Description).ToArray());
        return Results.ValidationProblem(errors);
    }

    private static string AuditSessionId(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(SessionIdClaim) ?? httpContext.TraceIdentifier;

    private sealed record CreateRoleRequest(
        string? Name,
        string? Description,
        string[]? PermissionKeys,
        string? Reason);

    private sealed record ReplaceRolePermissionsRequest(
        string[]? PermissionKeys,
        string? Reason);
}
