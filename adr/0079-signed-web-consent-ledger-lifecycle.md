# ADR 0079 — Signed Web consent history navigation and source withdrawal

- Date: 2026-11-23
- Status: accepted design; see issue #181 for exact-SHA RED/GREEN evidence
- Related: ADR 0005 (first-party scope), 0007 (FORCE RLS),
  0068 (actor request-rate budget), 0069/0070/0071 (Web session and in-flight
  fences), 0075 (stale grant scope revocation), 0077 (legacy manual visibility),
  0078 (chronological consent keyset API)

## Context

The actor-scoped `GET /api/v1/consents/page` API now returns
a paged chronological first-party ledger. Before #181, the Web UI
only showed the current manual capture grant from legacy `GET /consents`.
A user with active grants for other first-party source strings had no
UI affordance to find or withdraw them, or traverse >200 historical rows.

## Decision

For authenticated Web cloud mode only:

1. An explicit **Consulta registro consensi** action reads the first
   chronological page using the authenticated `MemoryApi.consentPage`.
   The page contains up to 25 grant records and `nextCursor`; an
   explicit **Carica altri consensi** action appends further pages.
   No background crawling, auto-pagination, token storage or client
   owner/tenant input. A count + "altre pagine" / "fine storico"
   tells users whether more pages exist.
2. Render each first-party record's `source`, `purpose`,
   `policyVersion` and **server-derived** active/revoked/expired
   status. Revocation is offered only when `active=true`; it sends
   only its ID to existing `DELETE /api/v1/consents/{id}`.
   The backend resolves and revokes the scope atomically under
   the signed actor PostgreSQL lock and FORCE RLS (#176).
   An HTTP 204 is the only confirmation of withdrawal, not a click.
   A new signed refresh obtains current status, and no note is
   silently erased by consent withdrawal.
3. The `ConsentLedgerEpoch` gate ignores obsolete first-page/page
   responses and failures. Reset rows/cursors/error/loading immediately
   on logout, 401, grant, withdrawal, erasure or first-page timeline
   refresh. Generational fences plus session-scoped HTTP responses
   prevent a previous account's data from reappearing.
4. New controls are accessible Pressable buttons with loading/error
   feedback. The Web ledger and keys are session-only in React state;
   no localStorage/AsyncStorage persistence. Demo/native do not
   pretend to support secure cloud consent management.

## TDD and evidence

- RED commit `52a44ff86b83f7f2070bc05708f5284f1249afd1`
  introduces Vitest lifecycle tests and real Keycloak E2E attempting
  to open the absent ledger UI; see CI and E2E jobs in #181.
- GREEN adds view-only epoch helper, controlled UI and real JWT
  Playwright scenario with 27 synthetic first-party grants, keyset
  continuation, authenticated source withdrawal, post-revoke
  `consent_required` 403, status reread and sign-out clearing.
- An additional real Chromium/Keycloak regression demonstrates a
  second account's first-party grant UUID does not leak across OIDC
  subjects, while the dedicated PostgreSQL RLS suite covers distinct
  tenant claims. The Web ledger also reloads after losing in-memory
  tokens, requiring a fresh real IdP login and signed database read.
- Tests and actions must be verified on final **same main SHA**.

## Trust boundaries / limitations

The UI does not prove permission from third parties or activate
real external connectors; a source string is only a first-party
capture label and `memory.capture` purpose. An outdated view can
display stale active flags until refreshed; the server remains
authoritative and rejects unknown/revoked/foreign IDs. The page
cursor is a position, **not** a cross-request snapshot. No
unbounded automatic listing, cross-page transactional snapshot,
search/index for grant source, native cross-device offline support,
legal GDPR sign-off, complete external erasure or backup revocation
is asserted. Parents #29/#41/#2 and related compliance gates stay
open even if the narrow UI child #181 passes.
