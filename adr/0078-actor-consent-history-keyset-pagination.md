# ADR 0078 — Additive actor-scoped consent history keyset API

- Date: 2026-11-23
- Status: narrow backend implementation, RED/GREEN workflow evidence in #180
- Parent: #179, #29, #41, #2
- Existing contracts: ADR 0005 scoped consent, ADR 0007 non-owner FORCE RLS,
  ADR 0073 memory pagination, ADR 0075 stale-handle scope revocation,
  ADR 0077 legacy manual-active consent priority

## Problem

The original `GET /api/v1/consents` returns an array capped at 200
records. The previous #178 change preserves manual capture visibility,
but cannot represent a ledger with 275 events, multiple older grants or
216 concurrent valid non-manual scopes. Silently claiming to show the
complete history would mislead users about visibility and withdrawal.

## Decision

Add `GET /api/v1/consents/page?limit=25&before=<cursor>`:

1. Authenticate using the existing OIDC bearer + unique signed `sub`
   and `tenant_id`. Never accept owner/tenant parameters.
2. Validate `limit` 1..50 (default 25). Invalid size/token returns
   generic HTTP 400 `invalid_consent_page` without echoing input.
3. Use a **distinct** `c1.` + 70-character canonical Base64URL
   position for UTC grantedAt ticks and immutable grant UUID. It is
   intentionally incompatible with the `MemoryPageCursor` encoding.
   This cursor is neither secret nor signed: modifying it can choose
   a position but cannot grant access to any other actor's records.
4. Under the restricted runtime PostgreSQL login and `PostgresActorScope`
   FORCE RLS transaction, query `(granted_at, id) < (@beforeAt, @beforeId)`
   ordered `granted_at DESC, id DESC` with `LIMIT limit+1`. Expose
   `items[]` and nullable `nextCursor` after determining hasMore.
   No OFFSET, concatenated SQL input, new schema or database index.
5. Return first-party grant id/source/purpose/version/timestamps and
   the server-derived active flag. Existing `GET /api/v1/consents`
   array and its manual-first priority remain unchanged. Revocation,
   ingestion gate, retention and other privileges are unaffected.
6. The optional typed Web client method `consentPage` is not a UX
   claim. UI full-history navigation and revoke affordances remain
   parent #179 pending their own RED/E2E evidence.

## Test-first proof

- RED commit test-only `22f31f4b86a016f5c809550be38265a979951fa1`:
  real TestServer/pgvector PostgreSQL test with 275 actor grants sharing
  one timestamp (216 active, 59 revoked), 2 foreign actor scopes,
  25-per-page traversal, expected HTTP 405 MethodNotAllowed before implementation (no GET route).
- GREEN adds this API, repository, cursor parser and DI, Vitest typed
  client test, and real Keycloak/Chromium JWT smoke check.
- Exact SHA and GitHub Actions CI/PITR/Pages/E2E proof attached to #180.

## Safety and limitations

A cursor is a position, **not** an access capability, replay-resistant
session token or cross-request snapshot. Concurrent re-grants, erasures,
new rows and time passage can change the visible ledger while paging;
the caller should refresh to observe current status. Page order is
chronological, not the manual-first priority of the legacy list.
No guarantee of unbounded instantaneous UI listing of all active
sources, browser history management, legal GDPR transparency,
third-party permissions or OAuth grant revocation. Parent #179 and
privacy/security epics remain OPEN.

References: PostgreSQL 17 official
https://www.postgresql.org/docs/17/functions-comparisons.html
https://www.postgresql.org/docs/17/queries-limit.html
https://www.postgresql.org/docs/17/ddl-rowsecurity.html
