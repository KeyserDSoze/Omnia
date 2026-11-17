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

- `GET /api/v1/consents` → up to 200 grants for the signed-in tenant+subject, newest first, including `active` (computed with server clock).
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
