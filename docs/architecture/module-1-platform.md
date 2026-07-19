# Module 1: Platform & Administration

## Boundaries

`Platform.Domain` contains business invariants with no database or UI dependency. `Platform.Infrastructure` owns the `platform` PostgreSQL schema, Identity persistence, audit storage, permission records, and durable outbox records. `Web` hosts the interactive UI and versioned HTTP surface.

Later modules may reference published platform contracts and identifiers. They must not query private platform tables directly.

## Security invariants

- Authorization defaults to deny.
- Active explicit user denial wins over every grant.
- Active explicit user allowance wins over role grants.
- An assigned role may grant an action when no active override exists.
- Expired overrides have no effect.
- Disabling an account changes its session version so existing sessions can be rejected.
- Audit events are append-only at the persistence boundary.
- Passwords are managed by ASP.NET Core Identity with a 12-character minimum, complexity requirements, lockout after five failures, and PBKDF2 configured for 210,000 iterations.
- MFA is optional and will use Identity authenticator tokens.

## Platform schema

The initial migration creates Identity users/roles plus:

- `permission_definitions`
- `role_permissions`
- `user_permission_overrides`
- `audit_events`
- `outbox`
- `facility_settings`

The outbox idempotency key is unique. Audit records store actor, session, reason, before/after JSON, entity identity, action, and timestamp.

## Public HTTP surface

- `GET /api/v1/platform/status` returns the service, module, API version, and running state.
- `GET /health` executes registered health checks, including PostgreSQL connectivity.

Sensitive administration APIs will require named action-permission policies when implemented. Hiding a UI element is never considered authorization.

## Known acceptance gaps

Module 1 is not complete until account/role/override workflows, administrator bootstrap, MFA, session validation, file storage, worker processing, backup verification, and browser acceptance tests are implemented.
