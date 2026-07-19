# Windows single-server deployment

## Target

One supported Windows Server host runs PostgreSQL, HeatSynQ Web, and the HeatSynQ background worker. Encrypted backup archives are copied to a separate protected device.

## Provisioning

1. Patch Windows and restrict interactive administrator access.
2. Install PostgreSQL 16 or later and the .NET 10 Hosting Bundle.
3. Create dedicated Windows service identities with log-on-as-service rights and no interactive login.
4. Create a PostgreSQL database and least-privilege application login.
5. Store `ConnectionStrings__Platform`, `Platform__BootstrapSecret`, and storage paths in protected machine-level configuration.
6. Restore tools and dependencies with `dotnet tool restore` and `dotnet restore HeatSynQ.slnx --configfile NuGet.Config`.
7. Publish Web and Worker into `<install-root>\releases\<version>`, then create the
   `<install-root>\current` junction pointing to that release.
8. Create the migration bundle:

   ```powershell
   dotnet ef migrations bundle --project src\Modules\Platform\Infrastructure --startup-project src\Web -r win-x64 -o <release>\migrate.exe
   ```

9. Run `scripts\install-services.ps1` from an elevated PowerShell session. Both
   service executable paths are registered through the `current` junction so a
   release switch updates both services atomically.
10. Bind the web service through IIS or an approved reverse proxy to internal HTTPS with a company-trusted certificate.
11. Restrict inbound firewall access to approved LAN ranges and HTTPS.
12. Schedule `scripts\backup-platform.ps1` nightly and verify `/api/v1/platform/health`.

## Required production paths

Use protected absolute paths for:

- `Platform__DataProtectionKeysPath`
- `Platform__FileStoragePath`
- `Platform__BackupStatusPath`
- `Platform__WorkerHeartbeatPath`
- `Platform__MaintenanceFlagPath`

Development, test, and production require separate databases and storage roots. The application login must not be a PostgreSQL superuser. Grant service identities only the filesystem access each service needs.

## Update

1. Publish Web, Worker, release notes, and `migrate.exe` into one release source directory.
2. Run and verify a pre-update backup.
3. Run `scripts\deploy-release.ps1` with the release source, install root,
   semantic version, verified archive, and the exact absolute
   `Platform__MaintenanceFlagPath` value supplied as `-MaintenanceFlagPath`.
4. The script enters maintenance, stops Worker then Web, copies to a new version
   directory, runs the migration bundle, switches the stable junction, and starts
   Web then Worker. Maintenance mode is removed only after both services report
   `Running`.
5. Verify detailed health, administrator login, session revocation, managed-file download, queue processing, and an audit export.

## Rollback

If deployment fails after the junction switch, the deployment script stops both
services, restores the prior junction, and leaves maintenance mode active for
operator verification. `scripts\rollback-release.ps1` also switches the stable
junction to the recorded prior binaries. Database rollback is intentionally not
automatic. If a release migration is not backward compatible, use that release's
reviewed database decision: forward repair or restore the verified pre-update
archive with `restore-platform.ps1`. Never run an unreviewed down-migration in
production.
