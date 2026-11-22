# ADR 0076 — Fresh secure Web source key per manual capture, stable key per retry

- Date: 2026-11-22
- Status: narrow implementation; TDD RED/GREEN evidence in issue #177
- Parent: #41 / #125 / #8; follows ADR 0074 (#175 immutable replay)

## Problem

`normalizeMemo(content, occurredAt)` formerly built `externalId` as
`local-${occurredAt}`. Two independent manual notes created in the same
millisecond therefore shared one source identity. Since #175 correctly
protects an existing live canonical record against a different payload,
the second note received HTTP 409, and could not be saved without
changing its text or key. Timestamp is provenance time, not identity.

## Decision

1. For the authenticated Web cloud path, generate a **new**
   `local-${crypto.randomUUID()}` v4 source identifier at the moment
   of a new capture. Web is deployed over HTTPS (loopback permitted)
   and the Web Crypto method provides cryptographically strong random
   UUIDs. A secure identifier is **required** before any POST; absence
   or failure throws a user-visible error. No weak random/timestamp
   fallback is uploaded to PostgreSQL.
2. Preserve `occurredAt` as the original event time and preserve
   `retryRef.entry` unchanged when the same draft is retried after
   a network/ambiguous server result. Re-normalizing a retry would
   defeat server idempotence and could create duplicate notes.
3. The local demo used by unconfigured Web and currently native
   Expo Android/iOS may use a session-local increasing `demo-`
   suffix when Web Crypto is unavailable. It is **not persisted or
   synchronized**, cannot serve as a production/native ID contract,
   and grants no cloud permission.
4. No database schema/API/consent/retention changes. The API still
   enforces JWT tenant/subject, source-scoped consent, FORCE RLS
   and immutable live `(tenant,owner,source,external_id)` (#175).
   Source UUIDs are deduplication keys, **not secrets/authorization**.

## Test-first verification

- RED commit `372ee7988a75bada6bcd604784676157910d2e15`:
  Vitest with identical occurrence timestamps fails three assertions
  under timestamp-only IDs; real Keycloak/Chromium/PostgreSQL test
  exercises two distinct signed manual POSTs at one frozen browser
  `toISOString()` instant without fabricating responses or tokens.
- GREEN: application uses secure source key requirement, React Native
  local-demo fallback only, unchanged retry entry, CI+real OIDC E2E
  must pass on the final `main` SHA. Links tracked in issue #177.
- External reference: https://developer.mozilla.org/en-US/docs/Web/API/Crypto/randomUUID
  (secure context), https://docs.expo.dev/versions/v57.0.0/sdk/crypto/
  (secure native UUID requires future integration and device tests).

## Limitations

UUID collision probability is negligible, not mathematically zero;
server 409 remains authoritative if a collision occurs. This does
not solve native OIDC, offline encrypted queue, cross-tab retry
persistence, cross-source dedup, multi-device conflicts, downstream
provider replay or full GDPR compliance. All parent issues stay OPEN.
