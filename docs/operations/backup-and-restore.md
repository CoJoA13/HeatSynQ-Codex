# Backup and restore

## Policy

- Run an encrypted logical PostgreSQL backup nightly.
- Retain local encrypted archives for 14 days by default.
- Copy every accepted archive to separate protected storage.
- Protect both destinations with BitLocker or equivalent encryption.
- Store the archive password in the Windows service credential store, not scripts.
- Perform a clean restore drill at least quarterly and after material schema changes.

## Backup

`scripts\backup-platform.ps1` requires local and off-server destinations. It:

1. creates a PostgreSQL custom-format dump;
2. encrypts it with 7-Zip AES-256 and encrypted headers;
3. tests the local archive;
4. copies it off-server;
5. tests the off-server archive independently;
6. removes expired local archives; and
7. writes the health marker only after every required step succeeds.

Required protected environment variables:

- `HEATSYNQ_DATABASE_URL`
- `HEATSYNQ_BACKUP_PASSWORD`

Example:

```powershell
.\scripts\backup-platform.ps1 `
  -Destination "D:\HeatSynQBackups" `
  -OffServerDestination "\\backup-server\protected\HeatSynQ" `
  -StatusPath "C:\ProgramData\HeatSynQ\last-successful-backup.txt"
```

## Restore drill

1. Provision an isolated empty PostgreSQL database.
2. Select an off-server archive and record its timestamp.
3. Set `HEATSYNQ_BACKUP_PASSWORD`.
4. Run `scripts\restore-platform.ps1 -Archive <archive> -TargetDatabaseUrl <isolated database URL>`.
5. Start the matching HeatSynQ release against the restored database.
6. Verify health, administrator login, roles and overrides, audit history, stored-file metadata/content, and outbox state.
7. Record recovery point, recovery time, operator, archive checksum, and results.
8. Securely destroy drill artifacts.

The restore script always validates the archive before extraction and uses `pg_restore --clean --if-exists --exit-on-error`.
