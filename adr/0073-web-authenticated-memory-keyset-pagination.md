# ADR 0073 — Authenticated PostgreSQL keyset pages for Web timeline

- Date: 2026-11-22
- Status: Narrow implementation; exact RED/GREEN CI runs in #174
- Issue: #174, child #41/#79, identity #1, memory #8, privacy #2
- Extends: ADRs 0069–0072 (epoch fencing, API bearer, in-flight signout and HTTP no-store)

## Context
The Expo Web timeline previously fetched only the last 30 memories. Older
unexpired owner records existed in PostgreSQL and complete personal export,
but were unreachable from the UI. Fetching all entries on login would allow
unbounded private response size and memory usage. Naive OFFSET pagination
shifts on concurrent captures and with equal timestamps; careless cursors
can accidentally be confused with an authorization token.

## Decision
Retain backwards-compatible GET /api/v1/memories array endpoint.
Add GET /api/v1/memories/page?limit=1..50&before=<cursor> returning
{items, nextCursor}. Default 10, frontend explicitly loads ten per page.
Encode cursor as canonical unpadded base64url of exactly 19 UTC tick digits,
colon and 32 lowercase UUID hex digits, with strict length/roundtrip
validation. This is solely a **read position**, not confidential or signed,
not a credential and not a source for actor/tenant identity. Reject
non-canonical/malformed/oversized cursors and invalid limits with 400 before
SQL. No offset and no dynamic SQL.

Use (occurred_at DESC, id DESC) stable ordering and parameterized PostgreSQL
tuple (occurred_at, id) < (@beforeAt, @beforeId) for older pages,
LIMIT size+1 to detect next page. Read each page in PostgresActorScope
transaction with the restricted non-owner runtime identity and FORCE RLS,
filter tenant/owner solely from JWT-validated claims and expires_at>now().
Continue decrypting sealed payloads with canonical UUID and AAD; fail
closed on decryption/key errors; no plaintext fallback. No DB migration,
new personal store or extra credential is introduced.

Expose "Carica altre note" only while nextCursor is present, append
decrypted summaries and deduplicate by canonical row UUID in ephemeral
React state. A manual refresh replaces from the first page; logout,
new session, consent change and erasure invalidate CloudViewEpoch and any
in-flight append. A failed older page leaves the previous timeline and
cursor intact for deliberate retry. No localStorage, AsyncStorage or
offline persisted notes/cursors. Existing 401 behavior still discards
current Web session. No implicit download of full export.

## Tests and evidence
RED with actual PostgreSQL via xUnit TestServer: 13 synthetic sealed notes
with identical occurred_at, 5+5+3 keyset pages with no duplicate/loss,
different tenant/subject with and without borrowed cursor denied, 400
invalid cursors/limits, 401 anonymous, legacy GET unchanged, no-store.
RED also with real Keycloak/JWT browser capture of 12 notes and missing
older-load UX. GREEN requires 6+ real OIDC browser scenarios, Vitest
deferred page response/race tests, TypeScript, Expo/Pages, S3/Azure
contracts, PostgreSQL/RLS/PITR/restore and Docker CI all on exact main SHA.

## Limits and residual risks
Keyset stability is NOT multi-request snapshot isolation. Concurrent
updates, backdated new captures and automatic retention may change what
unfetched pages contain. A returned cursor intentionally does not attest
that a specific note existed or permit cross-owner reads; only JWT and
server-enforced RLS grant access. The current client is Expo **Web-only**
for authenticated pages; native OIDC, encrypted offline queue, server
push, full grouped timeline UX/accessibility and GDPR audit remain OPEN.

## Authoritative references
- https://www.postgresql.org/docs/17/functions-comparisons.html
- https://www.postgresql.org/docs/17/ddl-rowsecurity.html
- https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/parameter-binding?view=aspnetcore-10.0
- https://react.dev/learn/synchronizing-with-effects
