# Module 1: Platform & Administration

## Boundaries

`Platform.Domain` contains business invariants with no database or UI dependency. `Platform.Infrastructure` owns the `platform` PostgreSQL schema, Identity persistence, audit/file metadata, platform configuration, and durable outbox processing. `Web` hosts the interactive UI and versioned HTTP surface. `Worker` is the independently hosted Windows background service.

Later modules may use public platform services, contracts, and identifiers. They must not query private platform tables directly.

## Security invariants

- Authorization defaults to deny and is enforced on UI routes and HTTP endpoints.
- Active explicit user denial wins over every grant.
- Active explicit user allowance wins over role grants.
- An assigned role may grant an action when no active override exists.
- Expired overrides have no effect.
- Disabling an account or revoking sessions rotates the Identity security stamp.
- Login sessions record authentication method, workstation, user agent, IP address, start, end, and revocation.
- Audit events are append-only at the persistence boundary.
- Passwords use ASP.NET Core Identity with a 12-character minimum, complexity, five-attempt lockout, and PBKDF2 at 210,000 iterations.
- Authenticator MFA is optional; recovery codes are shown once and stored through Identity token persistence.
- Administrative mutations require reasons and record before/after context.

## Platform schema

The migrations create Identity tables plus:

- `permission_definitions`, `role_permissions`, and `user_permission_overrides`
- `sessions` and append-only `audit_events`
- `facility_settings`, `number_sequences`, `retention_policies`, and `legal_holds`
- `stored_files`
- `outbox`, `notifications`, and `print_jobs`

Outbox idempotency keys and generated notification/print ownership are unique. Managed files use opaque storage paths, SHA-256 checksums, immutable revision metadata, and retention dates. Active legal holds prevent future retention workers from disposing of matching records.

## Operations

- `GET /health` is the generic service health surface.
- `GET /api/v1/platform/health` is permission-controlled and returns database, queue, file storage, backup, and worker details.
- The worker polls durable outbox rows, creates notification/print records idempotently, and applies exponential retry delays.
- Backup success is reported only after local encryption validation, off-server copy, and second integrity validation.
- Release tooling requires a verified pre-update backup and an EF migration bundle.

## Deferred from Module 1

Business data, process engineering, orders, production execution, and quality workflows begin in later modules. Direct equipment connectivity, offline browser synchronization, and multi-facility UI are explicitly outside Module 1.
