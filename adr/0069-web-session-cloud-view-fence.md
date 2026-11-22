# ADR 0069 — Session-generation fencing for Web cloud timeline reads

- Date: 2026-11-22
- Status: Narrow implementation; CI evidence must be added to issue #170
- Issue: #170 (child #41, identity epic #1, privacy epic #2)

## Problem and threat model
The Expo Web signed-in home previously used \`refreshCloud()\` to concurrently
fetch consent and note timeline then unconditionally apply React setGrant and
setMemos. Because the GETs continue asynchronously, pressing Logout cleared the
React state but an older HTTP response could later repopulate the timeline.
Similar stale reads can resurrect an out-of-date consent marker after grant,
revoke or confirmed data erasure. React's official effect guidance warns that
network responses can settle out of order and recommends ignoring stale results.
OWASP MASWE-0024 explicitly calls out personal data remaining visible after
termination of an authenticated client session.

## Decision
Create \`CloudViewEpoch\`, a nonpersistent generation guard owned by the Home
component and scoped to the current browser JS instance. Every \`refreshCloud\`
gets a new monotonic generation and will apply an actual API result only if
it is still the newest generation. A now-stale network error is ignored,
while errors from the current request propagate to the existing explicit UI.

Invalidate every pending refresh synchronously on start of logout, grant,
consent revocation and explicit database erasure, and on React component
cleanup. Clear notes and consent UI BEFORE awaiting best-effort OIDC token
revocation. Login remains disabled while logout is busy. Live memory and
consent data are never persisted in localStorage/sessionStorage/IndexedDB.
Keep the existing real OIDC + signed JWT API + RLS requirements and explicit
UI messages for unconfirmed remote token revocation or data erasure.
This is a view-state fence, NOT server-side bearer revocation, cross-device
logout, native device wiping, transactional GDPR erasure, or long-lived
offline sync.

## TDD and evidence
Test-first browser scenario against actual Keycloak, JWT bearer, HTTPS-equivalent
loopback API and PostgreSQL: create a synthetic note with scoped first-party
consent; fetch the REAL API response while refresh is in flight; delay only
its delivery in Playwright; logout and verify note is not restored. No fake
JWT, no fabricated HTTP payload and no bypass of PostgreSQL RLS.
Vitest deterministic deferred promises verify logout invalidation, current
generation wins, stale errors ignored, current errors surfaced and relogin
does not revive prior account state.
Record real RED and GREEN Actions URLs and exact SHA in #170, retaining
xUnit/physical PITR/mobile, browser OIDC and Pages gates.

## Residual risks
- Does not cancel already-issued GETs or remove them from remote server logs;
  bearer remains subject to provider revocation/expiry semantics.
- Client-only generation fencing does not replace endpoint authorization, RLS,
  retention, purge, encrypted browser/offline storage or provider revocation.
- On another device/client a separate logout path is required. #41/#39/#1/#2
  remain OPEN until their independent acceptance criteria pass.

## Official references
- https://react.dev/learn/synchronizing-with-effects
- https://mas.owasp.org/MASWE-0024/
- https://wstg.owasp.org/latest/10-Session_Management/06-Logout_Functionality/
