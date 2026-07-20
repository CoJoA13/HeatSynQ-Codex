# Module 1 acceptance record

## Automated scope

The Module 1 suite covers:

- bootstrap, password policy, lockout, login/logout, account status, session history/revocation;
- authenticator enrollment, TOTP challenge, recovery-code consumption, and MFA disable;
- roles, permission grants, temporary allow/deny overrides, and server-side authorization;
- append-only audit behavior, filtering, paging, and controlled export;
- facility settings, numbering allocation, retention policies, legal holds, and optimistic concurrency;
- managed-file authorization, SHA-256, immutable revisioning, and download;
- outbox idempotency, notification/print dispatch records, completion, failure, and retry;
- database/storage/queue/backup/worker health reporting;
- server-rendered administration pages and denied-route behavior; and
- PowerShell syntax, JavaScript syntax, formatting, build, and migration drift.

## PostgreSQL and recovery evidence

On 2026-07-19 the full migration chain was applied to a clean PostgreSQL 16 database. The PostgreSQL acceptance workflow completed bootstrap, cookie authentication, settings update, queued work, and audit query.

An independent recovery exercise then:

1. created a PostgreSQL custom-format dump;
2. created an AES-encrypted 7-Zip archive with encrypted headers;
3. passed archive integrity testing;
4. restored into a clean database with `pg_restore --exit-on-error`; and
5. verified audit, facility settings, outbox, and stored-file tables.

CI repeats the clean PostgreSQL migration and acceptance workflow on every pull request.

## Final sign-off checklist

- [ ] Administrator representative approval
- [ ] Sales representative confirms the shell/navigation baseline
- [ ] Receiving representative confirms workstation/browser baseline
- [ ] Operator representative confirms keyboard/scanner baseline
- [ ] Quality representative confirms audit/retention/legal-hold baseline
- [ ] Shipping representative confirms managed documents/printing baseline
- [ ] Billing representative confirms permission/navigation baseline
- [ ] Production Windows Server backup destination and service identities recorded
- [ ] Restore drill operator and next drill date recorded

Module 2 must not begin until the production owner signs the applicable operational/UAT items.
