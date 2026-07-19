# HeatSynQ ERP

HeatSynQ is an on-premises, heat-treat-focused ERP built as an ASP.NET Core modular monolith with PostgreSQL.

Development is sequential. A module must pass its production-readiness gate before work begins on the next module.

## Current delivery

Module 1 — Platform & Administration is implemented on the `codex/module-1-identity-admin` branch and is pending review.

Module 1 includes:

- ERP-managed users and passwords, account lockout, optional authenticator MFA, recovery codes, and self-service security
- multiple roles per user, explicit allow/deny overrides, temporary grants, and default-deny server authorization
- tracked login sessions, user/admin revocation, disabled-account invalidation, and browser sign-out
- append-only searchable audit history with controlled CSV export
- company/facility settings, configurable numbering, retention policies, and legal holds
- revisioned, checksummed managed files with authenticated downloads
- idempotent durable work queue, notification records, print jobs, retries, and a Windows background service
- PostgreSQL migrations and database health monitoring
- storage, queue, worker-heartbeat, and encrypted-backup freshness health checks
- responsive, categorized, permission-aware navigation and administration screens
- encrypted local/off-server backup, clean restore, service install, versioned deployment, and rollback tooling

## Developer setup

Requirements:

- .NET 10 SDK
- PostgreSQL 16 or later

```powershell
dotnet tool restore
dotnet restore HeatSynQ.slnx --configfile NuGet.Config
$env:ConnectionStrings__Platform = "Host=localhost;Database=heatsynq;Username=heatsynq;Password=<secret>"
dotnet tool run dotnet-ef database update --project src\Modules\Platform\Infrastructure --startup-project src\Web
dotnet run --project src\Web
```

Run the worker separately:

```powershell
dotnet run --project src\Worker
```

Never store production credentials in `appsettings.json`. Supply them through protected machine-level environment variables or the Windows service configuration.

## Verification

```powershell
dotnet format HeatSynQ.slnx --no-restore --verify-no-changes
dotnet build HeatSynQ.slnx --configuration Release --no-restore
dotnet test HeatSynQ.slnx --configuration Release --no-build
```

Operational guidance is in [deployment](docs/operations/deployment.md) and [backup and restore](docs/operations/backup-and-restore.md).
