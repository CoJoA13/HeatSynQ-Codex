using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HeatSynQ.Platform.Infrastructure.Persistence;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public Guid SessionVersion { get; set; } = Guid.NewGuid();
    public bool MustChangePassword { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public string? DisabledReason { get; set; }
}

public sealed class ApplicationRole : IdentityRole<Guid>
{
    public string Description { get; set; } = string.Empty;
    public bool IsSystemRole { get; set; }
}

public sealed class AuditEvent
{
    private AuditEvent()
    {
    }

    public Guid Id { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string EntityType { get; private set; } = string.Empty;
    public string EntityId { get; private set; } = string.Empty;
    public Guid ActorUserId { get; private set; }
    public string SessionId { get; private set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string BeforeJson { get; private set; } = string.Empty;
    public string AfterJson { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }

    public static AuditEvent Create(
        string action,
        string entityType,
        string entityId,
        Guid actorUserId,
        string sessionId,
        string reason,
        string beforeJson,
        string afterJson,
        DateTimeOffset occurredAt)
    {
        return new AuditEvent
        {
            Id = Guid.NewGuid(),
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            ActorUserId = actorUserId,
            SessionId = sessionId,
            Reason = reason,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            OccurredAt = occurredAt
        };
    }
}

public sealed class PermissionDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class RolePermission
{
    public Guid RoleId { get; set; }
    public string PermissionKey { get; set; } = string.Empty;
}

public sealed class UserPermissionOverrideRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string PermissionKey { get; set; } = string.Empty;
    public bool IsAllowed { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class OutboxRecord
{
    public Guid Id { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class FacilitySettings
{
    public Guid Id { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string FacilityName { get; set; } = string.Empty;
    public string FacilityCode { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "America/Chicago";
    public int DefaultRetentionYears { get; set; } = 10;
    public Guid Version { get; set; } = Guid.NewGuid();
}

public sealed class NumberSequenceRecord
{
    public string Key { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public long NextValue { get; set; }
    public int Padding { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}

public sealed class RetentionPolicyRecord
{
    public string Category { get; set; } = string.Empty;
    public int RetentionYears { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}

public sealed class LegalHoldRecord
{
    public Guid Id { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid PlacedByUserId { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
    public Guid? ReleasedByUserId { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}

public sealed class StoredFileRecord
{
    public Guid Id { get; set; }
    public string Category { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredRelativePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long Length { get; set; }
    public string ChecksumSha256 { get; set; } = string.Empty;
    public int Revision { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset RetainUntil { get; set; }
}

public sealed class NotificationRecord
{
    public Guid Id { get; set; }
    public Guid OutboxMessageId { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public sealed class PrintJobRecord
{
    public Guid Id { get; set; }
    public Guid OutboxMessageId { get; set; }
    public string Printer { get; set; } = string.Empty;
    public string DocumentPath { get; set; } = string.Empty;
    public int Copies { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PrintedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class PlatformSessionRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public string? RevokeReason { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string Workstation { get; set; } = string.Empty;
    public string AuthenticationMethod { get; set; } = string.Empty;
}

public sealed class PlatformDbContext(
    DbContextOptions<PlatformDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<PermissionDefinition> PermissionDefinitions => Set<PermissionDefinition>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserPermissionOverrideRecord> UserPermissionOverrides => Set<UserPermissionOverrideRecord>();
    public DbSet<OutboxRecord> Outbox => Set<OutboxRecord>();
    public DbSet<FacilitySettings> FacilitySettings => Set<FacilitySettings>();
    public DbSet<PlatformSessionRecord> Sessions => Set<PlatformSessionRecord>();
    public DbSet<NumberSequenceRecord> NumberSequences => Set<NumberSequenceRecord>();
    public DbSet<RetentionPolicyRecord> RetentionPolicies => Set<RetentionPolicyRecord>();
    public DbSet<LegalHoldRecord> LegalHolds => Set<LegalHoldRecord>();
    public DbSet<StoredFileRecord> StoredFiles => Set<StoredFileRecord>();
    public DbSet<NotificationRecord> Notifications => Set<NotificationRecord>();
    public DbSet<PrintJobRecord> PrintJobs => Set<PrintJobRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("platform");

        builder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(160);
            entity.Property(x => x.EntityType).HasMaxLength(120);
            entity.Property(x => x.EntityId).HasMaxLength(160);
            entity.Property(x => x.SessionId).HasMaxLength(160);
            entity.Property(x => x.Reason).HasMaxLength(1000);
        });

        builder.Entity<PermissionDefinition>(entity =>
        {
            entity.ToTable("permission_definitions");
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(160);
            entity.Property(x => x.Module).HasMaxLength(80);
            entity.Property(x => x.Action).HasMaxLength(80);
        });

        builder.Entity<RolePermission>(entity =>
        {
            entity.ToTable("role_permissions");
            entity.HasKey(x => new { x.RoleId, x.PermissionKey });
        });

        builder.Entity<UserPermissionOverrideRecord>(entity =>
        {
            entity.ToTable("user_permission_overrides");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.PermissionKey });
        });

        builder.Entity<OutboxRecord>(entity =>
        {
            entity.ToTable("outbox");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.IdempotencyKey).IsUnique();
            entity.HasIndex(x => new { x.CompletedAt, x.NextAttemptAt });
        });

        builder.Entity<FacilitySettings>(entity =>
        {
            entity.ToTable("facility_settings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FacilityCode).HasMaxLength(32);
            entity.Property(x => x.Version).IsConcurrencyToken();
        });

        builder.Entity<NumberSequenceRecord>(entity =>
        {
            entity.ToTable("number_sequences");
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(80);
            entity.Property(x => x.Prefix).HasMaxLength(40);
            entity.Property(x => x.Version).IsConcurrencyToken();
        });

        builder.Entity<RetentionPolicyRecord>(entity =>
        {
            entity.ToTable("retention_policies");
            entity.HasKey(x => x.Category);
            entity.Property(x => x.Category).HasMaxLength(80);
            entity.Property(x => x.Version).IsConcurrencyToken();
        });

        builder.Entity<LegalHoldRecord>(entity =>
        {
            entity.ToTable("legal_holds");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Category, x.ReleasedAt });
            entity.HasIndex(x => new { x.EntityType, x.EntityId });
            entity.Property(x => x.Category).HasMaxLength(80);
            entity.Property(x => x.EntityType).HasMaxLength(120);
            entity.Property(x => x.EntityId).HasMaxLength(160);
            entity.Property(x => x.Reason).HasMaxLength(1000);
            entity.Property(x => x.ReleaseReason).HasMaxLength(1000);
            entity.Property(x => x.Version).IsConcurrencyToken();
        });

        builder.Entity<StoredFileRecord>(entity =>
        {
            entity.ToTable("stored_files");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new
            {
                x.Category,
                x.EntityType,
                x.EntityId,
                x.OriginalFileName,
                x.Revision
            }).IsUnique();
            entity.Property(x => x.Category).HasMaxLength(80);
            entity.Property(x => x.EntityType).HasMaxLength(120);
            entity.Property(x => x.EntityId).HasMaxLength(160);
            entity.Property(x => x.OriginalFileName).HasMaxLength(260);
            entity.Property(x => x.StoredRelativePath).HasMaxLength(500);
            entity.Property(x => x.ContentType).HasMaxLength(160);
            entity.Property(x => x.ChecksumSha256).HasMaxLength(64);
        });

        builder.Entity<NotificationRecord>(entity =>
        {
            entity.ToTable("notifications");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OutboxMessageId).IsUnique();
            entity.Property(x => x.Recipient).HasMaxLength(160);
            entity.Property(x => x.Subject).HasMaxLength(240);
        });

        builder.Entity<PrintJobRecord>(entity =>
        {
            entity.ToTable("print_jobs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OutboxMessageId).IsUnique();
            entity.Property(x => x.Printer).HasMaxLength(160);
            entity.Property(x => x.DocumentPath).HasMaxLength(500);
            entity.Property(x => x.Error).HasMaxLength(2000);
        });

        builder.Entity<PlatformSessionRecord>(entity =>
        {
            entity.ToTable("sessions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.CreatedAt });
            entity.HasIndex(x => new { x.UserId, x.EndedAt, x.RevokedAt });
            entity.Property(x => x.RevokeReason).HasMaxLength(1000);
            entity.Property(x => x.IpAddress).HasMaxLength(64);
            entity.Property(x => x.UserAgent).HasMaxLength(512);
            entity.Property(x => x.Workstation).HasMaxLength(160);
            entity.Property(x => x.AuthenticationMethod).HasMaxLength(40);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceAppendOnlyAudit();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnforceAppendOnlyAudit();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforceAppendOnlyAudit()
    {
        var changedAudit = ChangeTracker.Entries<AuditEvent>()
            .FirstOrDefault(x => x.State is EntityState.Modified or EntityState.Deleted);

        if (changedAudit is not null)
        {
            throw new InvalidOperationException("Audit events are append-only.");
        }
    }
}
