using System.Data;
using System.Text.Json;
using HeatSynQ.Platform.Domain.Security;
using HeatSynQ.Platform.Infrastructure.Persistence;
using HeatSynQ.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HeatSynQ.Web.Endpoints;

public static class PlatformIdentityEndpoints
{
    public static IEndpointRouteBuilder MapPlatformIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/platform/bootstrap", BootstrapAdministratorAsync)
            .AllowAnonymous()
            .RequireRateLimiting("authentication");

        endpoints.MapPost("/api/v1/auth/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting("authentication");

        endpoints.MapPost("/api/v1/auth/login/2fa", TwoFactorLoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting("authentication");

        endpoints.MapPost("/account/login", BrowserLoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting("authentication");

        endpoints.MapGet("/api/v1/auth/me", CurrentIdentityAsync)
            .RequireAuthorization();

        endpoints.MapPost("/api/v1/auth/logout", LogoutAsync)
            .RequireAuthorization();

        endpoints.MapPost("/api/v1/auth/revoke-sessions", RevokeOwnSessionsAsync)
            .RequireAuthorization();

        endpoints.MapPost("/api/v1/auth/mfa/authenticator", BeginAuthenticatorEnrollmentAsync)
            .RequireAuthorization();

        endpoints.MapPost(
                "/api/v1/auth/mfa/authenticator/enable",
                EnableAuthenticatorAsync)
            .RequireAuthorization();

        endpoints.MapGet("/api/v1/platform/users", ListUsersAsync)
            .RequireAuthorization(new AuthorizationPolicyBuilder()
                .AddRequirements(new PermissionRequirement("platform.users.view"))
                .Build());

        endpoints.MapPost("/api/v1/platform/users", CreateUserAsync)
            .RequireAuthorization(new AuthorizationPolicyBuilder()
                .AddRequirements(new PermissionRequirement("platform.users.edit"))
                .Build());

        endpoints.MapPut("/api/v1/platform/users/{userId:guid}/status", ChangeUserStatusAsync)
            .RequireAuthorization(new AuthorizationPolicyBuilder()
                .AddRequirements(new PermissionRequirement("platform.users.edit"))
                .Build());

        endpoints.MapPost("/api/v1/platform/users/{userId:guid}/revoke-sessions", RevokeUserSessionsAsync)
            .RequireAuthorization(new AuthorizationPolicyBuilder()
                .AddRequirements(new PermissionRequirement("platform.sessions.revoke"))
                .Build());

        return endpoints;
    }

    private static async Task<IResult> ListUsersAsync(
        UserManager<ApplicationUser> userManager,
        CancellationToken cancellationToken)
    {
        var users = await userManager.Users
            .AsNoTracking()
            .OrderBy(x => x.DisplayName)
            .ToArrayAsync(cancellationToken);
        var response = new List<object>(users.Length);

        foreach (var user in users)
        {
            response.Add(new
            {
                user.Id,
                Username = user.UserName,
                user.DisplayName,
                user.IsEnabled,
                Roles = await userManager.GetRolesAsync(user)
            });
        }

        return Results.Ok(response);
    }

    private static async Task<IResult> CreateUserAsync(
        CreateUserRequest request,
        HttpContext httpContext,
        PlatformDbContext db,
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["displayName"] = ["Display name is required."]
            });
        }

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

        var roleNames = (request.RoleNames ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unknownRoles = new List<string>();
        foreach (var roleName in roleNames)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                unknownRoles.Add(roleName);
            }
        }

        if (unknownRoles.Count > 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["roleNames"] = unknownRoles
                    .Select(x => $"Unknown role: {x}")
                    .ToArray()
            });
        }

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = request.Username?.Trim(),
            Email = request.Email?.Trim(),
            DisplayName = request.DisplayName.Trim(),
            IsEnabled = true
        };
        var createResult = await userManager.CreateAsync(user, request.Password ?? string.Empty);
        if (!createResult.Succeeded)
        {
            return IdentityErrors(createResult);
        }

        if (roleNames.Length > 0)
        {
            var assignmentResult = await userManager.AddToRolesAsync(user, roleNames);
            if (!assignmentResult.Succeeded)
            {
                return IdentityErrors(assignmentResult);
            }
        }

        db.AuditEvents.Add(AuditEvent.Create(
            "platform.user.created",
            "User",
            user.Id.ToString(),
            actor.Id,
            httpContext.TraceIdentifier,
            request.Reason.Trim(),
            "{}",
            JsonSerializer.Serialize(new
            {
                user.UserName,
                user.Email,
                user.DisplayName,
                user.IsEnabled,
                RoleNames = roleNames
            }),
            DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.Created(
            $"/api/v1/platform/users/{user.Id}",
            new
            {
                user.Id,
                user.UserName,
                user.Email,
                user.DisplayName,
                user.IsEnabled,
                RoleNames = roleNames
            });
    }

    private static async Task<IResult> ChangeUserStatusAsync(
        Guid userId,
        ChangeUserStatusRequest request,
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

        if (!request.IsEnabled && actor.Id == userId)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["userId"] = ["Administrators cannot disable their own account."]
            });
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return Results.NotFound();
        }

        if (user.IsEnabled == request.IsEnabled)
        {
            return Results.NoContent();
        }

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var beforeJson = JsonSerializer.Serialize(new
        {
            user.IsEnabled,
            user.DisabledAt,
            user.DisabledReason,
            user.SessionVersion
        });
        user.IsEnabled = request.IsEnabled;
        user.DisabledAt = request.IsEnabled ? null : timeProvider.GetUtcNow();
        user.DisabledReason = request.IsEnabled ? null : request.Reason.Trim();
        user.SessionVersion = Guid.NewGuid();
        var updateResult = await userManager.UpdateSecurityStampAsync(user);
        if (!updateResult.Succeeded)
        {
            return IdentityErrors(updateResult);
        }

        db.AuditEvents.Add(AuditEvent.Create(
            request.IsEnabled ? "platform.user.restored" : "platform.user.disabled",
            "User",
            user.Id.ToString(),
            actor.Id,
            httpContext.TraceIdentifier,
            request.Reason.Trim(),
            beforeJson,
            JsonSerializer.Serialize(new
            {
                user.IsEnabled,
                user.DisabledAt,
                user.DisabledReason,
                user.SessionVersion
            }),
            timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> RevokeUserSessionsAsync(
        Guid userId,
        AdministrativeReasonRequest request,
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

        var actor = await userManager.GetUserAsync(httpContext.User);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return Results.NotFound();
        }

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var previousSessionVersion = user.SessionVersion;
        user.SessionVersion = Guid.NewGuid();
        var updateResult = await userManager.UpdateSecurityStampAsync(user);
        if (!updateResult.Succeeded)
        {
            return IdentityErrors(updateResult);
        }

        db.AuditEvents.Add(AuditEvent.Create(
            "platform.sessions.revoked",
            "User",
            user.Id.ToString(),
            actor.Id,
            httpContext.TraceIdentifier,
            request.Reason.Trim(),
            JsonSerializer.Serialize(new { SessionVersion = previousSessionVersion }),
            JsonSerializer.Serialize(new { user.SessionVersion }),
            DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        var user = string.IsNullOrWhiteSpace(request.Username)
            ? null
            : await userManager.FindByNameAsync(request.Username.Trim());

        if (user is null || !user.IsEnabled)
        {
            return InvalidCredentials();
        }

        var result = await signInManager.PasswordSignInAsync(
            user,
            request.Password ?? string.Empty,
            request.RememberMe,
            lockoutOnFailure: true);

        if (result.Succeeded)
        {
            return Results.NoContent();
        }

        if (result.RequiresTwoFactor)
        {
            return Results.Json(
                new
                {
                    error = "A two-factor authentication code is required.",
                    requiresTwoFactor = true
                },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return InvalidCredentials();
    }

    private static async Task<IResult> TwoFactorLoginAsync(
        TwoFactorLoginRequest request,
        SignInManager<ApplicationUser> signInManager)
    {
        var code = NormalizeAuthenticatorCode(request.Code);
        var result = await signInManager.TwoFactorAuthenticatorSignInAsync(
            code,
            request.RememberMe,
            request.RememberClient);

        return result.Succeeded
            ? Results.NoContent()
            : Results.Json(
                new { error = "Invalid two-factor authentication code." },
                statusCode: StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> BrowserLoginAsync(
        [FromForm] string? username,
        [FromForm] string? password,
        [FromForm] bool? rememberMe,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        var user = string.IsNullOrWhiteSpace(username)
            ? null
            : await userManager.FindByNameAsync(username.Trim());

        if (user is null || !user.IsEnabled)
        {
            return Results.LocalRedirect("/login?error=invalid");
        }

        var result = await signInManager.PasswordSignInAsync(
            user,
            password ?? string.Empty,
            rememberMe ?? false,
            lockoutOnFailure: true);

        return result.Succeeded
            ? Results.LocalRedirect("/")
            : Results.LocalRedirect("/login?error=invalid");
    }

    private static async Task<IResult> CurrentIdentityAsync(
        HttpContext httpContext,
        UserManager<ApplicationUser> userManager)
    {
        var user = await userManager.GetUserAsync(httpContext.User);
        if (user is null || !user.IsEnabled)
        {
            return Results.Json(
                new { error = "Authentication is required." },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var roles = await userManager.GetRolesAsync(user);
        return Results.Ok(new
        {
            Username = user.UserName,
            user.DisplayName,
            Roles = roles
        });
    }

    private static async Task<IResult> BeginAuthenticatorEnrollmentAsync(
        HttpContext httpContext,
        PlatformDbContext db,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IConfiguration configuration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(httpContext.User);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var sharedKey = await userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrWhiteSpace(sharedKey))
        {
            var resetResult = await userManager.ResetAuthenticatorKeyAsync(user);
            if (!resetResult.Succeeded)
            {
                return IdentityErrors(resetResult);
            }

            sharedKey = await userManager.GetAuthenticatorKeyAsync(user);
            await signInManager.RefreshSignInAsync(user);
        }

        var issuer = configuration["Platform:MfaIssuer"] ?? "HeatSynQ";
        var accountName = user.UserName ?? user.Email ?? user.Id.ToString();
        var authenticatorUri =
            $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountName)}" +
            $"?secret={Uri.EscapeDataString(sharedKey!)}" +
            $"&issuer={Uri.EscapeDataString(issuer)}&digits=6";
        db.AuditEvents.Add(AuditEvent.Create(
            "platform.mfa.enrollment_started",
            "User",
            user.Id.ToString(),
            user.Id,
            httpContext.TraceIdentifier,
            "User started authenticator enrollment",
            "{}",
            """{"authenticator":"pending"}""",
            timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new
        {
            SharedKey = sharedKey,
            AuthenticatorUri = authenticatorUri
        });
    }

    private static async Task<IResult> EnableAuthenticatorAsync(
        EnableAuthenticatorRequest request,
        HttpContext httpContext,
        PlatformDbContext db,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(httpContext.User);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var code = NormalizeAuthenticatorCode(request.Code);
        var isValid = await userManager.VerifyTwoFactorTokenAsync(
            user,
            TokenOptions.DefaultAuthenticatorProvider,
            code);
        if (!isValid)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["code"] = ["The authenticator code is invalid."]
            });
        }

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var enableResult = await userManager.SetTwoFactorEnabledAsync(user, true);
        if (!enableResult.Succeeded)
        {
            return IdentityErrors(enableResult);
        }

        var recoveryCodes = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))
            ?.ToArray() ?? [];
        await signInManager.RefreshSignInAsync(user);
        db.AuditEvents.Add(AuditEvent.Create(
            "platform.mfa.enabled",
            "User",
            user.Id.ToString(),
            user.Id,
            httpContext.TraceIdentifier,
            "User enabled authenticator MFA",
            """{"twoFactorEnabled":false}""",
            """{"twoFactorEnabled":true}""",
            timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.Ok(new { RecoveryCodes = recoveryCodes });
    }

    private static string NormalizeAuthenticatorCode(string? code) =>
        (code ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty);

    private static async Task<IResult> LogoutAsync(SignInManager<ApplicationUser> signInManager)
    {
        await signInManager.SignOutAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> RevokeOwnSessionsAsync(
        HttpContext httpContext,
        UserManager<ApplicationUser> userManager,
        PlatformDbContext db,
        CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(httpContext.User);
        if (user is null)
        {
            return Results.Json(
                new { error = "Authentication is required." },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var result = await userManager.UpdateSecurityStampAsync(user);
        if (!result.Succeeded)
        {
            return IdentityErrors(result);
        }

        db.AuditEvents.Add(AuditEvent.Create(
            "platform.sessions.revoked",
            "User",
            user.Id.ToString(),
            user.Id,
            httpContext.TraceIdentifier,
            "User revoked all sessions",
            "{}",
            """{"sessions":"revoked"}""",
            DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static IResult InvalidCredentials() =>
        Results.Json(
            new { error = "Invalid username or password." },
            statusCode: StatusCodes.Status401Unauthorized);

    private static async Task<IResult> BootstrapAdministratorAsync(
        AdministratorBootstrapRequest request,
        PlatformDbContext db,
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var existingUserCount = await userManager.Users.CountAsync(cancellationToken);
        var decision = AdministratorBootstrapPolicy.Evaluate(
            existingUserCount,
            configuration["Platform:BootstrapSecret"],
            request.BootstrapSecret);

        if (decision == AdministratorBootstrapDecision.AlreadyProvisioned)
        {
            return Results.Conflict(new { error = "Platform administration is already provisioned." });
        }

        if (decision == AdministratorBootstrapDecision.NotConfigured)
        {
            return Results.Problem(
                "The one-time bootstrap secret is not configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (decision == AdministratorBootstrapDecision.InvalidSecret)
        {
            return Results.Json(
                new { error = "Invalid bootstrap credentials." },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["displayName"] = ["Display name is required."]
            });
        }

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        if (await userManager.Users.AnyAsync(cancellationToken))
        {
            return Results.Conflict(new { error = "Platform administration is already provisioned." });
        }

        const string administratorRoleName = "Administrator";
        var administratorRole = await roleManager.FindByNameAsync(administratorRoleName);
        if (administratorRole is null)
        {
            administratorRole = new ApplicationRole
            {
                Id = Guid.NewGuid(),
                Name = administratorRoleName,
                Description = "Full platform administration.",
                IsSystemRole = true
            };
            var roleResult = await roleManager.CreateAsync(administratorRole);
            if (!roleResult.Succeeded)
            {
                return IdentityErrors(roleResult);
            }
        }

        foreach (var permission in PlatformPermissionCatalog.All)
        {
            if (!await db.PermissionDefinitions.AnyAsync(x => x.Key == permission.Key, cancellationToken))
            {
                db.PermissionDefinitions.Add(new PermissionDefinition
                {
                    Key = permission.Key,
                    Module = permission.Module,
                    Action = permission.Action,
                    Description = permission.Description
                });
            }

            if (!await db.RolePermissions.AnyAsync(
                    x => x.RoleId == administratorRole.Id && x.PermissionKey == permission.Key,
                    cancellationToken))
            {
                db.RolePermissions.Add(new RolePermission
                {
                    RoleId = administratorRole.Id,
                    PermissionKey = permission.Key
                });
            }
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = request.Username?.Trim(),
            Email = request.Email?.Trim(),
            DisplayName = request.DisplayName.Trim(),
            EmailConfirmed = true,
            IsEnabled = true
        };

        var createResult = await userManager.CreateAsync(user, request.Password ?? string.Empty);
        if (!createResult.Succeeded)
        {
            return IdentityErrors(createResult);
        }

        var assignmentResult = await userManager.AddToRoleAsync(user, administratorRoleName);
        if (!assignmentResult.Succeeded)
        {
            return IdentityErrors(assignmentResult);
        }

        db.AuditEvents.Add(AuditEvent.Create(
            "platform.bootstrap.completed",
            "User",
            user.Id.ToString(),
            user.Id,
            "bootstrap",
            "Initial platform provisioning",
            "{}",
            """{"role":"Administrator","enabled":true}""",
            DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.Created(
            $"/api/v1/platform/users/{user.Id}",
            new { user.Id, user.UserName, user.Email, user.DisplayName, role = administratorRoleName });
    }

    private static IResult IdentityErrors(IdentityResult result)
    {
        var errors = result.Errors
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Code) ? "identity" : x.Code)
            .ToDictionary(x => x.Key, x => x.Select(error => error.Description).ToArray());
        return Results.ValidationProblem(errors);
    }

    private sealed record AdministratorBootstrapRequest(
        string? BootstrapSecret,
        string? Username,
        string? Email,
        string? DisplayName,
        string? Password);

    private sealed record LoginRequest(
        string? Username,
        string? Password,
        bool RememberMe);

    private sealed record TwoFactorLoginRequest(
        string? Code,
        bool RememberMe,
        bool RememberClient);

    private sealed record EnableAuthenticatorRequest(string? Code);

    private sealed record CreateUserRequest(
        string? Username,
        string? Email,
        string? DisplayName,
        string? Password,
        string[]? RoleNames,
        string? Reason);

    private sealed record ChangeUserStatusRequest(
        bool IsEnabled,
        string? Reason);

    private sealed record AdministrativeReasonRequest(string? Reason);

}
