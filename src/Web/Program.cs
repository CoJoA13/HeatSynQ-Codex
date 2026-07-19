using HeatSynQ.Platform.Domain.Security;
using HeatSynQ.Platform.Infrastructure.Persistence;
using HeatSynQ.Web.Components;
using HeatSynQ.Web.Endpoints;
using HeatSynQ.Web.Security;
using HeatSynQ.Web.Health;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWindowsService(options =>
    options.ServiceName = "HeatSynQ Web");
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var connectionString = builder.Configuration.GetConnectionString("Platform")
    ?? throw new InvalidOperationException("Connection string 'Platform' is required.");
var keyPath = builder.Configuration["Platform:DataProtectionKeysPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "storage", "data-protection");
Directory.CreateDirectory(keyPath);

var dataProtection = builder.Services
    .AddDataProtection()
    .SetApplicationName("HeatSynQ")
    .PersistKeysToFileSystem(new DirectoryInfo(keyPath));
if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi();
}

builder.Services.AddDbContextFactory<PlatformDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql =>
        npgsql.MigrationsHistoryTable("__migrations", "platform")));

builder.Services
    .AddIdentityCore<ApplicationUser>(options =>
    {
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.User.RequireUniqueEmail = true;
    })
    .AddRoles<ApplicationRole>()
    .AddEntityFrameworkStores<PlatformDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.Configure<PasswordHasherOptions>(options =>
    options.IterationCount = 210_000);
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
{
    options.ValidationInterval = TimeSpan.Zero;
    options.OnRefreshingPrincipal = context =>
    {
        var newPrincipal = context.NewPrincipal;
        if (newPrincipal?.Identity is not ClaimsIdentity identity)
        {
            return Task.CompletedTask;
        }
        foreach (var claimType in new[]
                 {
                     "heatsynq:session_id",
                     "heatsynq:must_change_password",
                     "amr"
                 })
        {
            var claim = context.CurrentPrincipal?.FindFirst(claimType);
            if (claim is not null &&
                !newPrincipal.HasClaim(
                    candidate => candidate.Type == claim.Type &&
                                 candidate.Value == claim.Value))
            {
                identity.AddClaim(claim);
            }
        }
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.AddCascadingAuthenticationState();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = builder.Environment.IsEnvironment("Testing")
        ? "HeatSynQ.Test"
        : "__Host-HeatSynQ";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsEnvironment("Testing")
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(20);
    options.SlidingExpiration = true;
    options.LoginPath = "/login";
    options.AccessDeniedPath = "/access-denied";
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAuthorization(options =>
{
    foreach (var permission in PlatformPermissionCatalog.All)
    {
        options.AddPolicy(
            permission.Key,
            policy => policy.AddRequirements(new PermissionRequirement(permission.Key)));
    }
});
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("authentication", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
    foreach (var configuredProxy in
             builder.Configuration.GetSection("Platform:TrustedProxies").Get<string[]>() ?? [])
    {
        if (!IPAddress.TryParse(configuredProxy, out var proxy))
        {
            throw new InvalidOperationException(
                $"Platform:TrustedProxies contains an invalid IP address: {configuredProxy}");
        }
        options.KnownProxies.Add(proxy);
    }
});
builder.Services.AddHealthChecks()
    .AddDbContextCheck<PlatformDbContext>("platform_database")
    .AddCheck<ManagedStorageHealthCheck>("managed_storage")
    .AddCheck<OutboxHealthCheck>("outbox")
    .AddCheck<BackupFreshnessHealthCheck>("backup_freshness")
    .AddCheck<WorkerHeartbeatHealthCheck>("worker_heartbeat");
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api")
        && !context.Request.Path.StartsWithSegments("/account"),
    branch => branch.UseStatusCodePagesWithReExecute(
        "/not-found",
        createScopeForStatusCodePages: true));
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (DbUpdateConcurrencyException) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "This record changed after it was loaded. Refresh and retry your change.",
            traceId = context.TraceIdentifier
        });
    }
});
app.Use(async (context, next) =>
{
    var maintenancePath = Path.GetFullPath(
        app.Configuration["Platform:MaintenanceFlagPath"]
        ?? Path.Combine(app.Environment.ContentRootPath, "storage", "maintenance.flag"));
    if (File.Exists(maintenancePath) &&
        !context.Request.Path.StartsWithSegments("/health"))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "300";
        await context.Response.WriteAsJsonAsync(new
        {
            error = "HeatSynQ is temporarily unavailable for a controlled update.",
            traceId = context.TraceIdentifier
        });
        return;
    }
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    const string sessionIdClaim = "heatsynq:session_id";
    var sessionIdText = context.User.FindFirst(sessionIdClaim)?.Value;
    if (Guid.TryParse(sessionIdText, out var sessionId))
    {
        var dbFactory = context.RequestServices
            .GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(context.RequestAborted);
        var session = await db.Sessions.SingleOrDefaultAsync(
            x => x.Id == sessionId &&
                 x.EndedAt == null &&
                 x.RevokedAt == null,
            context.RequestAborted);
        var now = context.RequestServices
            .GetRequiredService<TimeProvider>()
            .GetUtcNow();
        if (session is not null && now - session.LastSeenAt >= TimeSpan.FromMinutes(2))
        {
            session.LastSeenAt = now;
            await db.SaveChangesAsync(context.RequestAborted);
        }
    }
    await next();
});
app.Use(async (context, next) =>
{
    const string mustChangePasswordClaim = "heatsynq:must_change_password";
    if (context.User.Identity?.IsAuthenticated == true &&
        string.Equals(
            context.User.FindFirst(mustChangePasswordClaim)?.Value,
            "true",
            StringComparison.Ordinal))
    {
        var path = context.Request.Path;
        var allowed =
            path.StartsWithSegments("/account/password") ||
            path.StartsWithSegments("/account/logout") ||
            path.StartsWithSegments("/api/v1/auth/password") ||
            path.StartsWithSegments("/api/v1/auth/logout") ||
            path.StartsWithSegments("/_framework") ||
            path.StartsWithSegments("/_blazor") ||
            path.StartsWithSegments("/lib") ||
            path.StartsWithSegments("/app.css") ||
            path.StartsWithSegments("/favicon");
        if (!allowed)
        {
            if (path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "The temporary password must be changed before using HeatSynQ.",
                    passwordChangeUrl = "/account/password"
                });
            }
            else
            {
                context.Response.Redirect("/account/password");
            }
            return;
        }
    }
    await next();
});
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/health");
app.MapPlatformIdentityEndpoints();
app.MapPlatformPermissionEndpoints();
app.MapPlatformRoleEndpoints();
app.MapPlatformAdministrationEndpoints();
app.MapGet("/api/v1/platform/status", () => Results.Ok(new
{
    service = "HeatSynQ",
    module = "Platform",
    status = "running",
    apiVersion = "v1"
}));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
