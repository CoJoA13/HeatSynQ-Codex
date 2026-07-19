# Backup and restore

## Policy

- Run an encrypted PostgreSQL and managed-file backup nightly.
- Retain local encrypted archives for 14 days by default.
- Copy every accepted archive to separate protected storage.
- Protect both destinations with BitLocker or equivalent encryption.
- Store the archive password in the Windows service credential store, not scripts.
- Perform a clean restore drill at least quarterly and after material schema changes.

## Backup

`scripts\backup-platform.ps1` requires local and off-server destinations plus the
configured `Platform__FileStoragePath`. It:

1. creates a PostgreSQL custom-format dump;
2. snapshots the managed-file tree and writes a backup manifest;
3. encrypts the database, manifest, and managed files together with 7-Zip AES-256 and encrypted headers;
4. tests the local archive;
5. copies it off-server;
6. tests the off-server archive independently;
7. removes expired local archives; and
8. writes the health marker only after every required step succeeds.

Required protected environment variables:

- `HEATSYNQ_DATABASE_URL`
- `HEATSYNQ_BACKUP_PASSWORD`

Example:

```powershell
.\scripts\backup-platform.ps1 `
  -Destination "D:\HeatSynQBackups" `
  -OffServerDestination "\\backup-server\protected\HeatSynQ" `
  -ManagedStoragePath "D:\HeatSynQ\storage\files" `
  -StatusPath "C:\ProgramData\HeatSynQ\last-successful-backup.txt"
```

## Restore drill

1. Provision an isolated empty PostgreSQL database.
2. Select an off-server archive and record its timestamp.
3. Set `HEATSYNQ_BACKUP_PASSWORD`.
4. Run:

   ```powershell
   .\scripts\restore-platform.ps1 `
     -Archive <archive> `
     -TargetDatabaseUrl <isolated database URL> `
     -MaintenanceDatabaseUrl <isolated server postgres database URL> `
     -TargetDatabaseName <isolated target database name> `
     -TargetManagedStoragePath <isolated managed-storage path>
   ```
5. Start the matching HeatSynQ release against the restored database.
6. Verify health, administrator login, roles and overrides, audit history, stored-file metadata/content, and outbox state.
7. Record recovery point, recovery time, operator, archive checksum, and results.
8. Securely destroy drill artifacts.

The restore script validates the archive and requires its managed-file tree before
changing the database. The target managed-storage directory must be empty unless
`-ReplaceManagedStorage` is explicitly supplied. Before changing the target, the
script creates a custom-format safety dump of its current database and stages the
new managed-file tree on the target volume. Database restore uses
`pg_restore --clean --if-exists --exit-on-error`. Existing managed storage is
renamed to a sibling backup during the final swap. If the archive database
restore or storage swap fails, both the safety database dump and prior file tree
are restored. Database rollback force-removes and recreates the complete target
database from a create-capable safety archive, using the separately supplied
maintenance-database connection. Objects introduced only by the failed archive
therefore cannot survive. Before archive extraction or database changes, the
script verifies that `TargetDatabaseName` exactly matches `current_database()`
from `TargetDatabaseUrl`; a mismatch stops the restore. The restore operator must
own the target database or have equivalent database create/drop privileges. If
either rollback step also fails, the script retains the recovery directory and
reports its path for manual recovery.
