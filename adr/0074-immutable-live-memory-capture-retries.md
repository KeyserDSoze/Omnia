# ADR 0074 — Immutable live memory capture retries and explicit 409 collisions

- Date: 2026-11-22
- Scope: Issue #175 (child #125/#41, epic #8/#2)
- Status: narrow implementation; real RED/GREEN evidence in GitHub issue #175
- Extends: ADR 0009 retention, sealed-memory architecture and ADR 0073 Web memory timeline

## Problem
The capture key (tenant_id, owner_sub, source, external_id) was unique and
the database UUID stable, but PostgresMemoryRepository.UpsertAsync performed
ON CONFLICT DO UPDATE for **every** duplicate, including unexpired records.
This silently replaced the AES-GCM sealed original, summary, event time,
embedding and other provenance when a request reused an existing externalId
with different body. An identical retry also rewrote sealed_payload with a
new random DEK and generated unnecessary WAL. An apparent "idempotent"
retry was therefore not immutable and could corrupt the only version of a
user's original note. Existing export and retention tests proved stable
row UUID and TTL, but NOT unchanged ciphertext/source data.

## Decision
An unexpired canonical key is write-once within its provisional retention
window. Keep actor advisory lock, active source-scoped consent and exact
RLS transaction. Read matching row FOR UPDATE, decrypt sealed payload using
canonical ID and validated tenant/subject AEAD AAD (or cautiously read
already-existing legacy plaintext). Compare content type, original raw,
normalized JSON, summary, tags and timestamp at PostgreSQL microsecond
precision. The comparison intentionally omits derived embedding values;
models can be nondeterministic and the first authenticated accepted
derivation remains canonical for the same source event.

- Exact live replay: return same canonical UUID and response without
  calling Encrypt or SQL UPDATE: sealed ciphertext, metadata, received_at,
  expires_at and WAL row remain untouched.
- Same key/live but different source payload or provenance: throw a
  content-free MemoryIdempotencyConflictException; API responds HTTP 409
  with only {"error":"memory_idempotency_conflict"}. Never leak original
  text, actor/tenant IDs or stored record identifiers in conflict responses.
  The Web client keeps the draft, explains the collision and never
  interprets 409 as auth expiry or network failure.
- New source externalId: new canonical event; another tenant/subject
  with same externalId is unaffected, as enforced by restricted
  PostgreSQL FORCE RLS.
- Already expired key: retain the existing explicit recapture behavior;
  revalidate consent, then only SQL conflict updates where expired_at
  condition is true and renew the provisional TTL. This is not an implicit
  retry extension.
- Unexpected concurrent live conflict (e.g. a privileged external
  process): ON CONFLICT DO UPDATE WHERE expires_at <= now() RETURNING id
  returns no row; fail closed with 409, no overwrite.

This is source-key idempotency, not an event-sourcing version history,
collaborative editing, generalized cross-source deduplication, or a
guarantee to suppress embedding-provider work in earlier application
stages. A captured note with absent occurredAt receives server time and
cannot be replayed identically unless the caller preserves a stable
timestamp; browser captures already do so. No migrations or new personal
stores. Existing legacy rows may conservatively reject same-key replays
until fully backfilled, rather than risk mutating private provenance.

## TDD verification
A RED xUnit real PostgreSQL/pgvector restricted-RLS test asserts:
same-entry replay preserves exact sealed_payload bytes and timestamps,
changed-body collision is rejected and original remains, separate
actor/tenant same externalId is independent, and expired explicit
recapture with consent remains available. RED browser Playwright uses
a real Keycloak PKCE session/JWT and route.fetch (not mocked HTTP) to
forward the first capture, repeat it exactly, then forward a modified
payload with same externalId; expect 409 and original timeline intact.
After GREEN run same test suite, Vitest 409 non-logout, .NET xUnit/RLS,
S3/Azure contracts, physical restore/PITR, Docker API, Expo static
typecheck, all Playwright and GitHub Pages on the exact main SHA.

## Residual risks and boundaries
Only source-level live key collision behavior is hardened. Not a
distributed deduplication graph, device/offline queue, tombstone lineage,
general edit/revision interface, cross-source semantic matches,
provider-side zero-work guarantees, authoritative GDPR retention policy
or evidence that backups and external derivatives are deleted. Parent
#125/#41/#8/#2 remain open. Never claim production readiness.

## Technical sources
- https://www.postgresql.org/docs/17/sql-insert.html
- https://www.postgresql.org/docs/17/explicit-locking.html
- https://www.postgresql.org/docs/17/ddl-rowsecurity.html
- https://www.rfc-editor.org/rfc/rfc9110.html#section-15.5.10
