using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeatSynQ.Platform.Infrastructure.Persistence;
using HeatSynQ.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HeatSynQ.Web.Endpoints;

public static class PlatformAdministrationEndpoints
{
    private const string SessionIdClaim = "heatsynq:session_id";
    private static readonly SemaphoreSlim[] WorkSubmissionLocks =
        Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public static IEndpointRouteBuilder MapPlatformAdministrationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/platform/audit", SearchAuditAsync)
            .RequireAuthorization(Policy("platform.audit.view"));
        endpoints.MapGet("/api/v1/platform/audit/export", ExportAuditAsync)
            .RequireAuthorization(Policy("platform.audit.export"));
        endpoints.MapGet("/api/v1/platform/settings", GetSettingsAsync)
            .RequireAuthorization(Policy("platform.settings.view"));
        endpoints.MapPut("/api/v1/platform/settings", UpdateSettingsAsync)
            .RequireAuthorization(Policy("platform.settings.edit"));
        endpoints.MapGet("/api/v1/platform/number-sequences", ListSequencesAsync)
            .RequireAuthorization(Policy("platform.settings.view"));
        endpoints.MapPut("/api/v1/platform/number-sequences/{key}", UpdateSequenceAsync)
            .RequireAuthorization(Policy("platform.settings.edit"));
        endpoints.MapPost("/api/v1/platform/number-sequences/{key}/allocate", AllocateNumberAsync)
            .RequireAuthorization(Policy("platform.settings.edit"));
        endpoints.MapGet("/api/v1/platform/retention-policies", ListRetentionPoliciesAsync)
            .RequireAuthorization(Policy("platform.settings.view"));
        endpoints.MapPut("/api/v1/platform/retention-policies/{category}", UpdateRetentionPolicyAsync)
            .RequireAuthorization(Policy("platform.settings.edit"));
        endpoints.MapGet("/api/v1/platform/legal-holds", ListLegalHoldsAsync)
            .RequireAuthorization(Policy("platform.settings.view"));
        endpoints.MapPost("/api/v1/platform/legal-holds", PlaceLegalHoldAsync)
            .RequireAuthorization(Policy("platform.settings.edit"));
        endpoints.MapPost("/api/v1/platform/legal-holds/{id:guid}/release", ReleaseLegalHoldAsync)
            .RequireAuthorization(Policy("platform.settings.edit"));
        endpoints.MapGet("/api/v1/platform/files", ListFilesAsync)
            .RequireAuthorization(Policy("platform.files.view"));
        endpoints.MapPost("/api/v1/platform/files", UploadFileAsync)
            .DisableAntiforgery()
            .RequireAuthorization(Policy("platform.files.edit"));
        endpoints.MapGet("/api/v1/platform/files/{id:guid}/content", DownloadFileAsync)
            .RequireAuthorization(Policy("platform.files.view"));
        endpoints.MapGet("/api/v1/platform/health", GetDetailedHealthAsync)
            .RequireAuthorization(Policy("platform.health.view"));
        endpoints.MapGet("/api/v1/platform/work", ListWorkAsync)
            .RequireAuthorization(Policy("platform.work.view"));
        endpoints.MapPost("/api/v1/platform/work", SubmitWorkAsync)
            .RequireAuthorization(Policy("platform.work.submit"));
        return endpoints;
    }

    private static AuthorizationPolicy Policy(string permission) =>
        new AuthorizationPolicyBuilder()
            .AddRequirements(new PermissionRequirement(permission))
            .Build();

    private static IQueryable<AuditEvent> FilterAudit(
        PlatformDbContext db,
        string? action,
        Guid? actorUserId,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        var query = db.AuditEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(x => x.Action == action.Trim());
        }
        if (actorUserId is not null)
        {
            query = query.Where(x => x.ActorUserId == actorUserId);
        }
        if (from is not null)
        {
            query = query.Where(x => x.OccurredAt >= from);
        }
        if (to is not null)
        {
            query = query.Where(x => x.OccurredAt <= to);
        }
        return query;
    }

    private static async Task<IResult> SearchAuditAsync(
        PlatformDbContext db,
        string? action,
        Guid? actorUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = FilterAudit(db, action, actorUserId, from, to);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(x => x.OccurredAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.Action,
                x.EntityType,
                x.EntityId,
                x.ActorUserId,
                x.SessionId,
                x.Reason,
                x.BeforeJson,
                x.AfterJson,
                x.OccurredAt
            })
            .ToArrayAsync(cancellationToken);
        return Results.Ok(new { Total = total, Page = page, PageSize = pageSize, Items = items });
    }

    private static async Task<IResult> ExportAuditAsync(
        HttpContext httpContext,
        PlatformDbContext db,
        UserManager<ApplicationUser> userManager,
        TimeProvider timeProvider,
        IConfiguration configuration,
        string? action,
        Guid? actorUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        var maximumRows = Math.Clamp(
            configuration.GetValue("Platform:MaxAuditExportRows", 50_000),
            1,
            1_000_000);
        var rows = await FilterAudit(db, action, actorUserId, from, to)
            .OrderByDescending(x => x.OccurredAt)
            .Take(maximumRows + 1)
            .ToArrayAsync(cancellationToken);
        if (rows.Length > maximumRows)
        {
            return Results.Json(
                new
                {
                    error = $"The export contains more than {maximumRows:N0} rows; narrow the date range or filters and retry."
                },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        var csv = new StringBuilder("OccurredAt,Action,EntityType,EntityId,ActorUserId,SessionId,Reason\r\n");
        foreach (var row in rows)
        {
            csv.AppendJoin(',',
                Csv(row.OccurredAt.ToString("O", CultureInfo.InvariantCulture)),
                Csv(row.Action),
                Csv(row.EntityType),
                Csv(row.EntityId),
                Csv(row.ActorUserId.ToString()),
                Csv(row.SessionId),
                Csv(row.Reason));
            csv.Append("\r\n");
        }

        var actor = await userManager.GetUserAsync(httpContext.User);
        if (actor is not null)
        {
            db.AuditEvents.Add(AuditEvent.Create(
                "platform.audit.exported", "AuditEvent", "*", actor.Id,
                AuditSessionId(httpContext), "Authorized audit export", "{}",
                JsonSerializer.Serialize(new { RowCount = rows.Length, action, actorUserId, from, to }),
                timeProvider.GetUtcNow()));
            await db.SaveChangesAsync(cancellationToken);
        }
        return Results.File(
            Encoding.UTF8.GetBytes(csv.ToString()),
            "text/csv",
            $"heatsynq-audit-{timeProvider.GetUtcNow():yyyyMMddHHmmss}.csv");
    }

    private static string Csv(string value)
    {
        var safeValue = value.Length > 0 && value[0] is '=' or '+' or '-' or '@'
            ? $"'{value}"
            : value;
        return $"\"{safeValue.Replace("\"", "\"\"")}\"";
    }

    private static async Task<IResult> GetSettingsAsync(
        PlatformDbContext db,
        CancellationToken cancellationToken) =>
        Results.Ok(await db.FacilitySettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken)
            ?? new FacilitySettings
            {
                Id = Guid.Empty,
                CompanyName = "HeatSynQ",
                FacilityName = "Main Facility",
                FacilityCode = "MAIN",
                DefaultRetentionYears = 10
            });

    private static async Task<IResult> UpdateSettingsAsync(
        FacilitySettingsRequest request,
        HttpContext httpContext,
        PlatformDbContext db,
        UserManager<ApplicationUser> userManager,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var errors = ValidateSettings(request);
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        var actor = await userManager.GetUserAsync(httpContext.User);
        if (actor is null) return Results.Unauthorized();
        var record = await db.FacilitySettings.SingleOrDefaultAsync(cancellationToken);
        if (record is not null && request.Version != record.Version)
            return Results.Conflict(new
            {
                error = "This record changed after it was loaded. Refresh and retry your change."
            });
        var before = record is null ? "{}" : JsonSerializer.Serialize(record);
        record ??= new FacilitySettings { Id = Guid.NewGuid() };
        if (db.Entry(record).State == EntityState.Detached) db.FacilitySettings.Add(record);
        record.CompanyName = request.CompanyName!.Trim();
        record.FacilityName = request.FacilityName!.Trim();
        record.FacilityCode = request.FacilityCode!.Trim().ToUpperInvariant();
        record.TimeZoneId = request.TimeZoneId!.Trim();
        record.DefaultRetentionYears = request.DefaultRetentionYears;
        record.Version = Guid.NewGuid();
        db.AuditEvents.Add(AuditEvent.Create(
            "platform.settings.updated", "FacilitySettings", record.Id.ToString(),
            actor.Id, AuditSessionId(httpContext), request.Reason!.Trim(), before,
            JsonSerializer.Serialize(record), timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(record);
    }

    private static Dictionary<string, string[]> ValidateSettings(FacilitySettingsRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.CompanyName)) errors["companyName"] = ["Company name is required."];
        if (string.IsNullOrWhiteSpace(request.FacilityName)) errors["facilityName"] = ["Facility name is required."];
        if (string.IsNullOrWhiteSpace(request.FacilityCode)) errors["facilityCode"] = ["Facility code is required."];
        if (string.IsNullOrWhiteSpace(request.TimeZoneId) ||
            !TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZoneId, out _))
            errors["timeZoneId"] = ["A valid time zone is required."];
        if (request.DefaultRetentionYears is < 1 or > 100)
            errors["defaultRetentionYears"] = ["Retention must be between 1 and 100 years."];
        if (string.IsNullOrWhiteSpace(request.Reason)) errors["reason"] = ["A reason is required."];
        return errors;
    }

    private static async Task<IResult> ListSequencesAsync(
        PlatformDbContext db,
        CancellationToken cancellationToken) =>
        Results.Ok(await db.NumberSequences.AsNoTracking().OrderBy(x => x.Key).ToArrayAsync(cancellationToken));

    private static async Task<IResult> UpdateSequenceAsync(
        string key,
        NumberSequenceRequest request,
        HttpContext httpContext,
        PlatformDbContext db,
        UserManager<ApplicationUser> userManager,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || request.NextValue < 1 ||
            request.Padding is < 1 or > 20 || string.IsNullOrWhiteSpace(request.Reason))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["sequence"] = ["Key, positive next value, padding 1-20, and reason are required."]
            });
        var actor = await userManager.GetUserAsync(httpContext.User);
        if (actor is null) return Results.Unauthorized();
        key = key.Trim().ToUpperInvariant();
        var record = await db.NumberSequences.FindAsync([key], cancellationToken);
        if (record is not null && request.Version != record.Version)
            return Results.Conflict(new
            {
                error = "This sequence changed after it was loaded. Refresh and retry your change."
            });
        var before = record is null ? "{}" : JsonSerializer.Serialize(record);
        record ??= new NumberSequenceRecord { Key = key };
        if (db.Entry(record).State == EntityState.Detached) db.NumberSequences.Add(record);
        record.Prefix = request.Prefix?.Trim() ?? string.Empty;
        record.NextValue = request.NextValue;
        record.Padding = request.Padding;
        record.Version = Guid.NewGuid();
        db.AuditEvents.Add(AuditEvent.Create(
            "platform.number_sequence.updated", "NumberSequence", key, actor.Id,
            AuditSessionId(httpContext), request.Reason.Trim(), before,
            JsonSerializer.Serialize(record), timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(record);
    }

    private static async Task<IResult> AllocateNumberAsync(
        string key,
        PlatformDbContext db,
        CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        key = key.Trim().ToUpperInvariant();
        var record = await db.NumberSequences.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (record is null) return Results.NotFound();
        var value = $"{record.Prefix}{record.NextValue.ToString($"D{record.Padding}", CultureInfo.InvariantCulture)}";
        record.NextValue++;
        record.Version = Guid.NewGuid();
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return Results.Ok(new { Value = value });
    }

    private static async Task<IResult> ListRetentionPoliciesAsync(
        PlatformDbContext db,
        CancellationToken cancellationToken) =>
        Results.Ok(await db.RetentionPolicies.AsNoTracking().OrderBy(x => x.Category).ToArrayAsync(cancellationToken));

    private static async Task<IResult> UpdateRetentionPolicyAsync(
        string category,
        RetentionPolicyRequest request,
        HttpContext httpContext,
        PlatformDbContext db,
        UserManager<ApplicationUser> userManager,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(category) || request.RetentionYears is < 1 or > 100 ||
            string.IsNullOrWhiteSpace(request.Reason))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["retentionPolicy"] = ["Category, retention of 1-100 years, and reason are required."]
            });
        var actor = await userManager.GetUserAsync(httpContext.User);
        if (actor is null) return Results.Unauthorized();
        category = category.Trim().ToLowerInvariant();
        var record = await db.RetentionPolicies.FindAsync([category], cancellationToken);
        if (record is not null && request.Version != record.Version)
            return Results.Conflict(new
            {
                error = "This retention policy changed after it was loaded. Refresh and retry your change."
            });
        var before = record is null ? "{}" : JsonSerializer.Serialize(record);
        record ??= new RetentionPolicyRecord { Category = category };
        if (db.Entry(record).State == EntityState.Detached) db.RetentionPolicies.Add(record);
        record.RetentionYears = request.RetentionYears;
        record.Version = Guid.NewGuid();
        db.AuditEvents.Add(AuditEvent.Create(
            "platform.retention_policy.updated", "RetentionPolicy", category, actor.Id,
            AuditSessionId(httpContext), request.Reason.Trim(), before,
            JsonSerializer.Serialize(record), timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(record);
    }

    private static async Task<IResult> ListLegalHoldsAsync(
        bool activeOnly,
        PlatformDbContext db,
        CancellationToken cancellationToken)
    {
        var query = db.LegalHolds.AsNoTracking();
        if (activeOnly) query = query.Where(x => x.ReleasedAt == null);
        return Results.Ok(await query.OrderByDescending(x => x.PlacedAt).ToArrayAsync(cancellationToken));
    }

    private static async Task<IResult> PlaceLegalHoldAsync(
        LegalHoldRequest request,
        HttpContext httpContext,
        PlatformDbContext db,
        UserManager<ApplicationUser> userManager,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Category) || string.IsNullOrWhiteSpace(request.Reason))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["legalHold"] = ["Category and reason are required."]
            });
        var actor = await userManager.GetUserAsync(httpContext.User);
        if (actor is null) return Results.Unauthorized();
        var record = new LegalHoldRecord
        {
            Id = Guid.NewGuid(),
            Category = request.Category.Trim().ToLowerInvariant(),
            EntityType = request.EntityType?.Trim(),
            EntityId = request.EntityId?.Trim(),
            Reason = request.Reason.Trim(),
            PlacedByUserId = actor.Id,
            PlacedAt = timeProvider.GetUtcNow()
        };
        db.LegalHolds.Add(record);
        db.AuditEvents.Add(AuditEvent.Create(
            "platform.legal_hold.placed", "LegalHold", record.Id.ToString(), actor.Id,
            AuditSessionId(httpContext), record.Reason, "{}",
            JsonSerializer.Serialize(record), record.PlacedAt));
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/platform/legal-holds/{record.Id}", record);
    }

    private static async Task<IResult> ReleaseLegalHoldAsync(
        Guid id,
        AdministrativeReasonRequest request,
        HttpContext httpContext,
        PlatformDbContext db,
        UserManager<ApplicationUser> userManager,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["A reason is required."] });
        var actor = await userManager.GetUserAsync(httpContext.User);
        if (actor is null) return Results.Unauthorized();
        var record = await db.LegalHolds.FindAsync([id], cancellationToken);
        if (record is null) return Results.NotFound();
        if (record.ReleasedAt is not null) return Results.Conflict(new { error = "Legal hold is already released." });
        var before = JsonSerializer.Serialize(record);
        record.ReleasedAt = timeProvider.GetUtcNow();
        record.ReleasedByUserId = actor.Id;
        record.ReleaseReason = request.Reason.Trim();
        record.Version = Guid.NewGuid();
        db.AuditEvents.Add(AuditEvent.Create(
            "platform.legal_hold.released", "LegalHold", id.ToString(), actor.Id,
            AuditSessionId(httpContext), record.ReleaseReason, before,
            JsonSerializer.Serialize(record), record.ReleasedAt.Value));
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ListFilesAsync(
        string? entityType,
        string? entityId,
        PlatformDbContext db,
        CancellationToken cancellationToken)
    {
        var query = db.StoredFiles.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityType))
            query = query.Where(x => x.EntityType == entityType);
        if (!string.IsNullOrWhiteSpace(entityId))
            query = query.Where(x => x.EntityId == entityId);
        return Results.Ok(await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(500)
            .ToArrayAsync(cancellationToken));
    }

    private static async Task<IResult> UploadFileAsync(
        HttpContext httpContext,
        PlatformDbContext db,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                httpContext.Request.Headers["X-HeatSynQ-Request"],
                "managed-file-upload",
                StringComparison.Ordinal))
            return Results.BadRequest(new { error = "The managed file upload request header is required." });
        if (!httpContext.Request.HasFormContentType)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["A multipart file is required."] });
        var form = await httpContext.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        var category = form["category"].ToString().Trim().ToLowerInvariant();
        var entityType = form["entityType"].ToString().Trim();
        var entityId = form["entityId"].ToString().Trim();
        var reason = form["reason"].ToString().Trim();
        var yearsValid = int.TryParse(form["retentionYears"], out var retentionYears);
        if (file is null || file.Length is <= 0 or > 25 * 1024 * 1024 ||
            string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(entityType) ||
            string.IsNullOrWhiteSpace(entityId) || string.IsNullOrWhiteSpace(reason) ||
            !yearsValid || retentionYears is < 1 or > 100)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["file"] = ["File (maximum 25 MB), category, entity, retention of 1-100 years, and reason are required."]
            });
        var actor = await userManager.GetUserAsync(httpContext.User);
        if (actor is null) return Results.Unauthorized();
        var originalName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(originalName))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["fileName"] = ["A valid file name is required."] });
        var blockedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".com", ".bat", ".cmd", ".ps1", ".js", ".html", ".htm", ".svg"
        };
        if (blockedExtensions.Contains(Path.GetExtension(originalName)))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["fileName"] = ["Executable and active-content file types are not accepted."]
            });
        var revision = (await db.StoredFiles
            .Where(x => x.Category == category && x.EntityType == entityType &&
                        x.EntityId == entityId && x.OriginalFileName == originalName)
            .MaxAsync(x => (int?)x.Revision, cancellationToken) ?? 0) + 1;
        var id = Guid.NewGuid();
        var relativePath = Path.Combine(id.ToString("N")[..2], $"{id:N}.bin");
        var root = Path.GetFullPath(configuration["Platform:FileStoragePath"]
            ?? Path.Combine(AppContext.BaseDirectory, "storage", "files"));
        var destination = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Invalid managed storage path." });
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = $"{destination}.upload";
        await using (var source = file.OpenReadStream())
        await using (var target = new FileStream(
            temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await source.CopyToAsync(target, cancellationToken);
        }
        string checksum;
        await using (var checksumStream = File.OpenRead(temporary))
        {
            checksum = Convert.ToHexString(await SHA256.HashDataAsync(
                checksumStream, cancellationToken));
        }
        File.Move(temporary, destination);
        var now = timeProvider.GetUtcNow();
        var record = new StoredFileRecord
        {
            Id = id,
            Category = category,
            EntityType = entityType,
            EntityId = entityId,
            OriginalFileName = originalName,
            StoredRelativePath = relativePath,
            ContentType = string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream" : file.ContentType,
            Length = file.Length,
            ChecksumSha256 = checksum,
            Revision = revision,
            CreatedByUserId = actor.Id,
            CreatedAt = now,
            RetainUntil = now.AddYears(retentionYears)
        };
        db.StoredFiles.Add(record);
        db.AuditEvents.Add(AuditEvent.Create(
            "platform.file.uploaded", "StoredFile", id.ToString(), actor.Id,
            AuditSessionId(httpContext), reason, "{}",
            JsonSerializer.Serialize(new
            {
                record.Category,
                record.EntityType,
                record.EntityId,
                record.OriginalFileName,
                record.Revision,
                record.ChecksumSha256,
                record.Length,
                record.RetainUntil
            }), now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            File.Delete(destination);
            throw;
        }
        return Results.Created($"/api/v1/platform/files/{id}", record);
    }

    private static async Task<IResult> DownloadFileAsync(
        Guid id,
        PlatformDbContext db,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var record = await db.StoredFiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (record is null) return Results.NotFound();
        var root = Path.GetFullPath(configuration["Platform:FileStoragePath"]
            ?? Path.Combine(AppContext.BaseDirectory, "storage", "files"));
        var path = Path.GetFullPath(Path.Combine(root, record.StoredRelativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return Results.NotFound();
        return Results.File(path, record.ContentType, record.OriginalFileName, enableRangeProcessing: true);
    }

    private static async Task<IResult> GetDetailedHealthAsync(
        HealthCheckService healthCheckService,
        CancellationToken cancellationToken)
    {
        var report = await healthCheckService.CheckHealthAsync(cancellationToken);
        return Results.Ok(new
        {
            Status = report.Status.ToString(),
            DurationMilliseconds = report.TotalDuration.TotalMilliseconds,
            Checks = report.Entries.OrderBy(x => x.Key).Select(x => new
            {
                Name = x.Key,
                Status = x.Value.Status.ToString(),
                x.Value.Description,
                DurationMilliseconds = x.Value.Duration.TotalMilliseconds,
                x.Value.Data
            })
        });
    }

    private static async Task<IResult> ListWorkAsync(
        PlatformDbContext db,
        CancellationToken cancellationToken) =>
        Results.Ok(await db.Outbox.AsNoTracking()
            .OrderByDescending(x => x.OccurredAt)
            .Take(500)
            .ToArrayAsync(cancellationToken));

    private static async Task<IResult> SubmitWorkAsync(
        QueuedWorkRequest request,
        HttpContext httpContext,
        PlatformDbContext db,
        UserManager<ApplicationUser> userManager,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (request.MessageType is not ("platform.notification" or "platform.print") ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.Payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["work"] = ["An approved message type, JSON payload, and idempotency key are required."]
            });
        var idempotencyKey = request.IdempotencyKey.Trim();
        var lockIndex = (int)((uint)StringComparer.Ordinal.GetHashCode(idempotencyKey)
            % WorkSubmissionLocks.Length);
        var submissionLock = WorkSubmissionLocks[lockIndex];
        await submissionLock.WaitAsync(cancellationToken);
        try
        {
            var existing = await db.Outbox.AsNoTracking().SingleOrDefaultAsync(
                x => x.IdempotencyKey == idempotencyKey,
                cancellationToken);
            if (existing is not null) return Results.Ok(existing);
            var actor = await userManager.GetUserAsync(httpContext.User);
            if (actor is null) return Results.Unauthorized();
            var now = timeProvider.GetUtcNow();
            var record = new OutboxRecord
            {
                Id = Guid.NewGuid(),
                MessageType = request.MessageType,
                Payload = request.Payload.GetRawText(),
                IdempotencyKey = idempotencyKey,
                OccurredAt = now,
                NextAttemptAt = now
            };
            db.Outbox.Add(record);
            db.AuditEvents.Add(AuditEvent.Create(
                "platform.work.submitted", "OutboxRecord", record.Id.ToString(), actor.Id,
                AuditSessionId(httpContext), $"Submitted {record.MessageType}",
                "{}", JsonSerializer.Serialize(new
                {
                    record.MessageType,
                    record.IdempotencyKey
                }), now));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                var winner = await db.Outbox.AsNoTracking().SingleOrDefaultAsync(
                    x => x.IdempotencyKey == idempotencyKey,
                    cancellationToken);
                if (winner is not null) return Results.Ok(winner);
                throw;
            }
            return Results.Accepted($"/api/v1/platform/work/{record.Id}", record);
        }
        finally
        {
            submissionLock.Release();
        }
    }

    private static string AuditSessionId(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(SessionIdClaim) ?? httpContext.TraceIdentifier;

    private sealed record FacilitySettingsRequest(
        string? CompanyName, string? FacilityName, string? FacilityCode,
        string? TimeZoneId, int DefaultRetentionYears, Guid? Version, string? Reason);
    private sealed record NumberSequenceRequest(
        string? Prefix, long NextValue, int Padding, Guid? Version, string? Reason);
    private sealed record RetentionPolicyRequest(int RetentionYears, Guid? Version, string? Reason);
    private sealed record LegalHoldRequest(string? Category, string? EntityType, string? EntityId, string? Reason);
    private sealed record AdministrativeReasonRequest(string? Reason);
    private sealed record QueuedWorkRequest(
        string? MessageType,
        JsonElement Payload,
        string? IdempotencyKey);
}
