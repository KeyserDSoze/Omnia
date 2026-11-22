# ADR 0070 — Fail-closed browser session on API bearer 401

- Date: 2026-11-22
- Status: Narrow implementation; exact main CI evidence in issue #171
- Issue: #171 (P0/M1 child #39 and #41, identity epic #1 and privacy epic #2)
- Extends: ADR 0069 session-generation fencing for stale successful GET responses

## Problem
An already signed-in Expo Web Home fetches consent grants and memory
timeline with a Keycloak JWT. When the .NET API rejects an existing bearer
with HTTP 401 (missing, malformed, expired or no longer accepted), MemoryApi
previously threw an error but did not clear the signed-in React state.
The user still sees the old account's note and manual-consent indicator.
This is different from ADR 0069's delayed successful response after logout.
401 means the request lacks valid credentials; it does not guarantee that
the OIDC provider has globally revoked all device sessions.

## Decision
The Web MemoryApi receives an optional in-memory CloudSessionBoundary with
a current client session generation and an immediate unauthorized callback.
The exact request generation is captured before awaiting token access.
Every GET, POST, DELETE and personal-data export handles a real 401 by
invoking the callback ONLY when its generation still owns the session;
older 401 responses cannot close a newly established session. Also fail
closed when the token getter itself fails, with no API request sent if
token acquisition crosses a logout/generation change.

The Home callback synchronously invalidates CloudViewEpoch, clears timeline,
consent, pending text, erase confirmations, capture retry state and signed-in
UI, then displays an explicit re-login notice. A dedicated BrowserAuth
discardLocalSession calls oidc-client-ts UserManager.removeUser() without
calling network revocation or pretending to invalidate tokens globally.
Explicit user logout retains its existing best-effort remote revocation.
The existing CloudViewEpoch prevents unrelated in-flight 200 responses
from restoring cleared data. 403 (authorization/consent), 429, 5xx and
offline errors do NOT trigger an automatic signout. The token remains in
JS memory, with only transient OAuth redirect state in sessionStorage.
No personal data or secrets are written to browser persistence.

## TDD
RED on main: Playwright logs in via actual Keycloak, authorizes manual
capture, persists synthetic private content to real PostgreSQL, then
forwards a browser GET to the REAL .NET API with just its Authorization
header removed. The server sends a genuine 401 (no test-only JWT,
fabricated response or bypass of RLS), and pre-fix UI incorrectly retains
signed-in state and the prior note. GREEN browser regression requires the
login control to reappear, private content and grant indicator to clear,
and an explicit auth error. Add deterministic Vitest tests for GET/export
401, 403/429/503/offline non-logout, stale 401 after newer login, late
token getter and failed OIDC access token. Keep current real browser
PKCE+JWT, xUnit/PostgreSQL/RLS, physical PITR, Docker, S3/Azure SDK,
mobile/Expo and Pages checks all green at identical final main SHA.

## Limits
The client cannot enforce server JWT revocation, block a compromised bearer,
terminate external OIDC SSO, or erase backend/backup/provider/device data.
This only clears local in-memory view and token state on real server denial.
Native session lifecycle, third-party consent and complete erasure remain
separate, OPEN #39/#41/#1/#2 and privacy/release gates.

## Official references
- https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Status/401
- https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html
- https://developer.mozilla.org/en-US/docs/Web/Security/Authentication/Session_management
