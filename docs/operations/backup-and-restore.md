# Backup and restore

## Policy

- Run an encrypted logical PostgreSQL backup nightly.
- Retain local encrypted archives for 14 days and protected off-server copies according to company policy.
- Protect the backup destination with BitLocker or equivalent storage encryption.
- Store the archive password in the Windows service credential store, not in scripts.
- Test a clean-server restore at least quarterly and after material schema changes.

## Backup

The supplied `scripts/backup-platform.ps1` uses `pg_dump` custom format and 7-Zip AES-256 encryption. The scheduled-task identity needs PostgreSQL access, write access to the staging/destination directories, and read/execute access to the tools.

Required protected environment variables:

- `HEATSYNQ_DATABASE_URL`
- `HEATSYNQ_BACKUP_PASSWORD`

Successful creation is not sufficient verification. The scheduled workflow must copy the archive off-server, test archive integrity, and alert when any step fails.

## Restore drill

1. Provision a clean PostgreSQL test instance.
2. Copy one off-server archive to an isolated restore location.
3. Validate archive integrity with `7z t`.
4. Decrypt/extract the `.dump`.
5. Create an empty restore database.
6. Run `pg_restore --clean --if-exists --exit-on-error`.
7. Apply no migrations unless the restore procedure explicitly targets a newer application release.
8. Start the matching HeatSynQ release against the restored database.
9. Verify health, administrator login, role grants, audit history, attachments, and outbox state.
10. Record recovery-point and recovery-time results and securely destroy drill artifacts.
