using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HeatSynQ.Platform.Infrastructure.Persistence;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public Guid SessionVersion { get; set; } = Guid.NewGuid();
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
    public string TimeZoneId { get; set; } = "America/Chicago";
    public int DefaultRetentionYears { get; set; } = 10;
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
