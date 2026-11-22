# ADR 0077 — Active first-party consent visibility in bounded ledger

- Date: 2026-11-22
- Status: narrow correction, RED/GREEN GitHub Actions in issue #178
- Scope: child #178; parent #29 / #41 and privacy epic #2
- Related: ADR 0005 (scoped first-party consent), 0007 (FORCE RLS),
  0075 (stale grant withdrawal), 0076 (Web manual capture IDs)

## Problem

`GET /api/v1/consents` uses `PostgresConsentRepository.ListAsync`.
Its actor-scoped query previously selected newest grant rows with
`ORDER BY granted_at DESC, id DESC LIMIT 200`. Renewal and withdrawal
retain older grants by design. After 200+ newer expired/revoked rows, an
older still-active `manual/memory.capture` grant disappeared from the
bounded response. Web selected the active manual grant from these
results and showed "Nessun consenso attivo", even though ingestion's
separate database `EnsureActiveAsync` gate still authorized new notes.
The user could not find the grant handle to revoke via that view.
This is a UI/server observability mismatch, **not** an RLS bypass.

## Decision

Retain `GET /api/v1/consents` and its maximum 200 row contract,
with deterministic PostgreSQL ordering inside `PostgresActorScope`:

1. First, the actor's `manual/memory.capture` grant **currently**
   active according to database `now()`, which remains stable
   through the scoped transaction. This protects the first-party Web
   capture UX even if more than 200 *other* grants are active.
2. Next, all other currently active grants (subject to the 200 cap).
3. Finally, other recent revoked/expired history, with
   `granted_at DESC, id DESC` tie-break.

Do not change `IConsentGuard.EnsureActiveAsync`, `GrantAsync`,
`RevokeAsync`, indexes, JWT authentication, actor lock ordering or
the independent PostgreSQL FORCE RLS policies. Do not accept actor,
source, purpose or time from HTTP to reorder across trust boundaries.
`active` remains a server-derived response property. It does not
authorize capture in lieu of live authorization checks.

## Test-first verification

- RED commit `ffc4a0c25fe6cf9a254d5985f60fed77d14a09c7` adds real
  PostgreSQL 17/pgvector xUnit test to seed 260 newer revoked/expired
  grants into the restricted actor scope; then asserts older active
  manual/calendar grants remain in the 200 list and the manual
  grant can be revoked, with cross-owner/tenant negative checks.
- GREEN: narrow SQL ordering, docs, this ADR. All CI + real
  Chromium/Keycloak/JWT/API/PostgreSQL + Pages on the same `main`
  SHA must be verified before marking #178 completed.
- PostgreSQL 17 reference:
  https://www.postgresql.org/docs/17/queries-order.html
  https://www.postgresql.org/docs/17/queries-limit.html

## Unresolved boundaries

The 200-row response is still **not a complete consent ledger** and
is not a replacement for a future authenticated keyset-paginated
history API. If >200 non-manual scopes are concurrently active, some
may remain outside the bounded response. Native device consent UX,
provider OAuth revocation, third-party participant permissions,
retention, backup/export/erasure, legal GDPR assessment and audit
remain separate. Parent issues #29/#41/#2 stay OPEN.
