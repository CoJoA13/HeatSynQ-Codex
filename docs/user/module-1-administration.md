# Module 1 administrator guide

## First administrator

Set a one-time `Platform__BootstrapSecret`, start HeatSynQ, and submit the bootstrap request from a protected workstation. Bootstrap is permanently rejected after the first user exists. Remove the secret from machine configuration immediately afterward.

The first user receives the system `Administrator` role and every Module 1 permission.

## Users and sessions

Open **Administration → Users & sessions** to:

- create an ERP account and assign one or more roles;
- disable or restore an account with a required reason;
- revoke all sessions immediately;
- add a temporary or permanent explicit permission allow/deny; and
- revoke an override with a required reason.

Users can open their name in the top bar to enroll authenticator MFA, save one-time recovery codes, or disable MFA after confirming their password.

Never share accounts. Disable a departed user's account; do not attempt to delete it.

## Roles and permissions

Open **Administration → Roles & permissions**. Permission precedence is:

1. active explicit deny;
2. active explicit allow;
3. any assigned role grant;
4. default deny.

Use roles for normal access. Use per-user overrides only for documented exceptions, and add an expiration whenever access is temporary.

## Facility controls

Open **Administration → Company & facility** to maintain:

- company, facility code/name, time zone, and default retention;
- controlled number sequences;
- category retention policies; and
- active legal holds.

Every change requires a reason and is audited. Released legal holds remain in history.

## Audit and health

**Audit history** displays the latest administration/security events. CSV export is a separate permission and the export itself is audited.

**System health** reports:

- PostgreSQL connectivity;
- managed-storage write access/free space;
- waiting, overdue, and repeatedly failing queued work;
- last verified encrypted backup; and
- background worker heartbeat.

Treat unhealthy database, storage, or backup results as stop-work conditions for affected workflows. Preserve the displayed trace ID when escalating an unexpected error.

## Controlled files and queued work

Managed uploads accept up to 25 MB, calculate SHA-256, and create immutable revisions. Executable and active-content file extensions are rejected. Downloads require the same server-side permission as the metadata view.

Notification and print submissions require an idempotency key. Reusing the same key returns the existing work item instead of creating a duplicate.
