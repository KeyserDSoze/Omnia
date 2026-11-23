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

Domain tests in `tests/Omnia.Domain.Tests/ConsentTests.cs`; real pgvector PostgreSQL tests in `tests/Omnia.Integration.Tests/ConsentPersistenceTests.cs`. CI execution must be verified and linked in issue #29 before treating this slice as passing.

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
including its first active manual grant priority. The Web typed client
exposes `consentPage`, but navigation/selection/revoke of arbitrary
active sources is **not yet implemented in the UI**, tracked in #179.
Do not claim that UI displays all active grants or that the paginated
API itself grants consent. Concurrent changes are not snapshot-isolated
across pages; GDPR/third-party consents require separate review.
