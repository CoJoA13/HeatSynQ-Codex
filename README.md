# HeatSynQ ERP

HeatSynQ is an on-premises, heat-treat-focused ERP built as an ASP.NET Core modular monolith with PostgreSQL.

Development is deliberately sequential. A module must pass its production-readiness gate before work begins on the next module.

## Current delivery

Module 1 — Platform & Administration is in progress.

Implemented foundations:

- .NET 10 LTS solution and interactive server-rendered web application
- PostgreSQL platform schema and initial Entity Framework migration
- ASP.NET Core Identity persistence model
- role grants and expiring per-user permission overrides
- tested permission precedence: deny, allow, role, default deny
- account disabling and immediate session-version revocation
- append-only audit-event enforcement
- idempotent outbox storage and retry domain behavior
- categorized, responsive navigation shell
- health and versioned platform-status endpoints

Database provisioning, first-administrator bootstrap, complete account-management workflows, MFA enrollment, background processing, file storage, backup validation, and browser acceptance tests remain before Module 1 can be accepted.

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

Never store production credentials in `appsettings.json`. Supply them through protected environment variables or the Windows service configuration.

## Verification

```powershell
dotnet test HeatSynQ.slnx --configuration Release
dotnet build HeatSynQ.slnx --configuration Release --no-restore
```

Operational guidance is in [docs/operations/deployment.md](docs/operations/deployment.md) and [docs/operations/backup-and-restore.md](docs/operations/backup-and-restore.md).
