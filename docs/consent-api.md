# Consent API — first-party memory capture (v0.2 slice)

**Issue:** #29 (partially implemented). **Protected resources:** all endpoints require a valid OIDC bearer JWT, with exactly one `sub` and `tenant_id` claim. Owner/tenant are **never accepted in request bodies**.

## Create or renew authorization

`POST /api/v1/consents`

```json
{
  "source": "manual",
  "purpose": "memory.capture",
  "policyVersion": "privacy-v1",
  "expiresAt": "2026-12-01T00:00:00Z",
  "accepted": true
}
```

- `accepted` must be `true`, representing an explicit first-party request; false/missing → `400 invalid_consent`.
- `source` is a normalized identifier such as `manual`, `crm`; `purpose` is **only** `memory.capture` in this slice; do not claim permission to sync accounts or send messages. `policyVersion` is required and persisted.
- Expiration must be later than the server current time and no later than 90 days after grant. Missing/invalid/oversized fields → 400. JWT invalid → 401, JWT missing unique `sub`/`tenant_id` → 403.
- Reply is HTTP `201 Created` with UUID grant, source, purpose, policyVersion, grantedAt/expiresAt/revokedAt. Re-grant supersedes the previous grant for that owner/source/purpose and leaves its record with revocation timestamp.

## List grants and revoke

- `GET /api/v1/consents` → fino a **200 grant** per tenant+subject dal JWT, con `active` calcolato dal server. Ordinamento deterministico: **grant manual/memory.capture attivo prima**, altri grant attivi, poi storico più recente (`grantedAt DESC, id DESC`). Il limite resta 200: non è una pagina completa di storico e non garantisce visibilità di >200 scope attivi non-manuali; una futura API paginata è distinta.
- `DELETE /api/v1/consents/{grant-id}` → 204 on success or when already revoked; 404 if unknown/other user/tenant. Revocation prevents **new** memory ingestion for that scope.

## Memory ingest changes

`POST /api/v1/memories` returns `403 { "error": "consent_required" }` when no active first-party grant exists for the source with purpose `memory.capture`. Two gates:
1. Application validates the payload, then reads `IConsentGuard` before sending text to the optional embedding provider.
2. Postgres acquires `FOR SHARE` lock on the active scoped grant and writes the memory entry within the same transaction, preventing committed revocation from racing past persistence.

The existing memory API `GET` still permits viewing previously saved entries after revocation. It does not quietly delete data or pretend that revoke equals erasure. For erasure/export and retention see #31/#32.

## Privacy limitations

This ledger records only the first-party authenticated request; it is **not** verified consent of anyone mentioned or recorded. The UI does not yet display a proper legal notice/consent form; the real PKCE login and native secure-storage flows remain #39/#40. No background connector, automated message, voice-cloning or call capture is authorized by a `memory.capture` grant. Revocation may not retract a model request already dispatched before the revocation; new operations after revocation are blocked. **Use synthetic data only** pending other security gates.

## Test evidence

Domain tests in `tests/Tao.Domain.Tests/ConsentTests.cs`; real pgvector PostgreSQL tests in `tests/Tao.Integration.Tests/ConsentPersistenceTests.cs`. CI execution must be verified and linked in issue #29 before treating this slice as passing.

## Consent history truncation: active manual visibility (#178 / ADR 0077)

Previous chronological `LIMIT 200` could hide a still-effective
`manual/memory.capture` grant behind 200+ newer, revoked or expired
records. `IConsentGuard` could still authorize ingestion while the Web
UI misleadingly said there was no grant to revoke. The restricted FORCE
RLS PostgreSQL query now selects the current manual grant first, then
other active grants, then the newest history, within the same 200-row cap.
The backend still decides authorization via fresh DB checks on every
write; a response field is not an authority or a legal consent record.

Real pgvector PostgreSQL xUnit fixtures cover 260 historical grants,
live manual/calendar scopes, cross-tenant/owner isolation and revocation.
See issue #178 for RED/GREEN main Actions. No complete history
pagination, no unlimited active-source listing, no provider consent
and no GDPR legal compliance claim; #29/#41/#2 remain open.


## Complete first-party history by keyset (#180 / ADR 0078)

The additive `GET /api/v1/consents/page?limit=25&before=<cursor>`
provides **chronological** grant history for the authenticated JWT
actor, including active, expired, revoked and superseded grants.
Defaults to 25, maximum 50; response is
`{"items":[...],"nextCursor":"c1.<opaque>" | null}`. Follow
`nextCursor` until null to read the history at the time of the
requests. Invalid/oversized/malformed cursor or size returns
`400 {"error":"invalid_consent_page"}`; no bearer → 401.
Cursor is an opaque versioned keyset position, **not** an authorization,
snapshot or tenant/subject selector. All queries enforce restricted
PostgreSQL FORCE RLS and signed identity; no-store applies to every
private API response. Record ordering `grantedAt DESC, id DESC`
uses UUID tie-breaks when timestamps are equal.

The original bounded `GET /api/v1/consents` array remains unchanged,
including its first active manual grant priority. The Web typed client exposes `consentPage`, and explicit paginated
navigation and per-source withdrawal are implemented in the authenticated
UI (#181 / ADR 0079), without silently truncating the history.
Neither a paginated API nor a UI control grants consent. Concurrent changes
are not snapshot-isolated across pages; GDPR/third-party consents require
separate review.


## Explicit Web browsing and source-scoped withdrawal (#181 / ADR 0079)

In signed-in Web cloud mode, **Consulta registro consensi** loads
the first 25 chronological first-party grants; **Carica altri
consensi** loads further bounded pages until **Fine dello storico**.
Each row shows source, purpose, policy version and active/expired/
revoked state from the signed API. Only active grants have a
**Revoca [source]** action; a 204 from the existing actor-scoped
DELETE is required before announcing success. A fresh server read
after **Aggiorna registro** verifies the updated history. Revocation
stops new ingestion for that source/purpose but **does not erase**
historical notes or prove third-party permission.

Logout/401/erase/explicit consent mutation/timeline refresh
invalidate in-flight ledger reads and clear client rows and cursors.
No browser token/history persistence or automatic crawling of
unbounded pages. A stale page is never accepted after a session
change. Client paging does not provide a transactional
cross-request snapshot and only contains first-party
`memory.capture` records; #29/#41/#2 stay OPEN.
