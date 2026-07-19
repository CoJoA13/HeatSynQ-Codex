using System.Text.Json;
using HeatSynQ.Platform.Domain.Security;
using HeatSynQ.Platform.Infrastructure.Persistence;
using HeatSynQ.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HeatSynQ.Web.Endpoints;

public static class PlatformPermissionEndpoints
{
    public static IEndpointRouteBuilder MapPlatformPermissionEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/v1/platform/permissions",
                () => Results.Ok(PlatformPermissionCatalog.All))
            .RequireAuthorization(new AuthorizationPolicyBuilder()
                .AddRequirements(new PermissionRequirement("platform.roles.view"))
                .Build());

        endpoints.MapPost(
                "/api/v1/platform/users/{userId:guid}/permission-overrides",
                CreateOverrideAsync)
            .RequireAuthorization(new AuthorizationPolicyBuilder()
                .AddRequirements(new PermissionRequirement("platform.permissions.override"))
                .Build());

        endpoints.MapDelete(
                "/api/v1/platform/users/{userId:guid}/permission-overrides/{overrideId:guid}",
                RevokeOverrideAsync)
            .RequireAuthorization(new AuthorizationPolicyBuilder()
                .AddRequirements(new PermissionRequirement("platform.permissions.override"))
                .Build());

        return endpoints;
    }

    private static async Task<IResult> RevokeOverrideAsync(
        Guid userId,
        Guid overrideId,
        [FromBody] AdministrativeReasonRequest request,
        HttpContext httpContext,
        PlatformDbContext db,
        UserManager<ApplicationUser> userManager,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["reason"] = ["A reason is required for administrative changes."]
            });
        }

        var actor = await userManager.GetUserAsync(httpContext.User);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        var record = await db.UserPermissionOverrides
            .SingleOrDefaultAsync(
                x => x.Id == overrideId && x.UserId == userId,
                cancellationToken);
        if (record is null)
        {
            return Results.NotFound();
        }

        var beforeJson = JsonSerializer.Serialize(new
        {
            record.UserId,
            record.PermissionKey,
            Effect = record.IsAllowed ? "Allow" : "Deny",
            record.ExpiresAt
        });
        db.UserPermissionOverrides.Remove(record);
        db.AuditEvents.Add(AuditEvent.Create(
            "platform.permission_override.revoked",
            "UserPermissionOverride",
            record.Id.ToString(),
            actor.Id,
            httpContext.TraceIdentifier,
            request.Reason.Trim(),
            beforeJson,
            "{}",
            timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateOverrideAsync(
        Guid userId,
        CreatePermissionOverrideRequest request,
        HttpContext httpContext,
        PlatformDbContext db,
        UserManager<ApplicationUser> userManager,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["reason"] = ["A reason is required for administrative changes."]
            });
        }

        var permission = PlatformPermissionCatalog.All
            .SingleOrDefault(x => x.Key == request.PermissionKey);
        if (permission is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["permissionKey"] = ["A known permission key is required."]
            });
        }

        if (!Enum.TryParse<PermissionOverrideEffect>(
                request.Effect,
                ignoreCase: true,
                out var effect))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["effect"] = ["Effect must be Allow or Deny."]
            });
        }

        var now = timeProvider.GetUtcNow();
        if (request.ExpiresAt is not null && request.ExpiresAt <= now)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["expiresAt"] = ["Expiration must be in the future."]
            });
        }

        var actor = await userManager.GetUserAsync(httpContext.User);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        if (!await db.Users.AnyAsync(x => x.Id == userId, cancellationToken))
        {
            return Results.NotFound();
        }

        var record = new UserPermissionOverrideRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PermissionKey = permission.Key,
            IsAllowed = effect == PermissionOverrideEffect.Allow,
            ExpiresAt = request.ExpiresAt,
            Reason = request.Reason.Trim()
        };
        db.UserPermissionOverrides.Add(record);
        db.AuditEvents.Add(AuditEvent.Create(
            "platform.permission_override.created",
            "UserPermissionOverride",
            record.Id.ToString(),
            actor.Id,
            httpContext.TraceIdentifier,
            record.Reason,
            "{}",
            JsonSerializer.Serialize(new
            {
                record.UserId,
                record.PermissionKey,
                Effect = effect.ToString(),
                record.ExpiresAt
            }),
            now));
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"/api/v1/platform/users/{userId}/permission-overrides/{record.Id}",
            new
            {
                record.Id,
                record.UserId,
                record.PermissionKey,
                Effect = effect.ToString(),
                record.ExpiresAt,
                record.Reason
            });
    }

    private sealed record CreatePermissionOverrideRequest(
        string? PermissionKey,
        string? Effect,
        DateTimeOffset? ExpiresAt,
        string? Reason);

    private sealed record AdministrativeReasonRequest(string? Reason);
}
