# ADR 0071 — User-driven logout preempts in-flight Web cloud operations

- Date: 2026-11-22
- Status: Narrow implementation; record exact RED/GREEN evidence in #172
- Issue: #172 (child #41/#39, identity epic #1 and privacy epic #2)
- Extends: ADR 0069 stale GET view fence, ADR 0070 API 401 session invalidation

## Context and threat
Previously Home.logout() returned silently whenever busy was true. Therefore
the user could not terminate the visible web session while a capture POST,
consent grant/revoke, primary DB erasure or private JSON export was awaiting
a slow response. Concurrently, when a late mutation result arrived it might
update the signed-in UI, claim successful capture/erasure, trigger a new
authenticated timeline refresh or offer a private export download in the wrong
browser session. This violates the user-controlled signout requirement.
React recommends ignoring asynchronous responses from a superseded session,
and OWASP recommends explicit session termination. Aborting a fetch on the
client is not sufficient proof that a POST already accepted by PostgreSQL
has been rolled back.

## Decision
Allow explicit logout EVEN if a cloud API operation is pending. Use a
separate short-lived logoutInProgress ref to block duplicate logout
actions, rather than gating logout on the general mutation busy flag.
Before any await, increment current Web session epoch, invalidate all
CloudViewEpoch read results and clear memory, grant, editor and privacy
confirmation; then perform best-effort OIDC revocation and clear local
token state. If there was an in-flight operation, display a truthful
notice that it MAY ALREADY HAVE BEEN PROCESSED by the server and that
data should be verified upon next login. Never pretend logout cancels a
remote committed POST or an erasure. Keep existing server idempotency and
consent/RLS semantics.

On every cloud mutation or private export, Home captures the session
generation before awaiting. After completion and in catches/finally,
apply UI changes ONLY when that generation is still current. A late
accepted capture must not start a fresh GET /memories after logout.
A late exported Blob must never initiate an object-URL download.
The API transport also checks the session epoch after the HTTP response
and after reading JSON/blob bodies, before returning any private result.
An old 401 continues to be ignored as in ADR 0070; stale responses/errors
do not logout a different account. In-app 403/429/offline remain explicit,
without arbitrary signout.

## TDD verification
Use actual Keycloak login PKCE, signed API JWT and real PostgreSQL capture;
with Playwright route.fetch POST the real request until 201 from the DB,
delay ONLY delivery, click Logout while busy, verify immediate removal of
consent/drafts and no late private UI, no follow-up GET, truthful uncertainty
notice, and after fresh re-login verify the accepted note persists.
Similarly delay the real authenticated JSON export response and verify no
download after logout. Never invent E2E JWTs, API results or data.
Deterministic Vitest deferred Promises cover stale successful 201, body
read race, delayed private export, stale network failures and current
success/403. Keep real OIDC E2E, .NET 10 PostgreSQL/RLS and PITR/restore,
S3/Azure, Expo TypeScript/Vitest/Playwright and Pages green on same main SHA.

## Limits
This is **Web-only** in-memory presentation safety, not distributed
session termination, a guarantee of cancelling remote mutations, GDPR
erasure from all replicas/backups, secure native offline queue or token
revocation across every device. Even if the caller has signed out, the
API may have committed a correctly authorized, consented note already.
RLS and server authorization must remain the actual trust boundary.

## Sources
- https://react.dev/learn/synchronizing-with-effects
- https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html
- https://developer.mozilla.org/en-US/docs/Web/API/Fetch_API/Using_Fetch
