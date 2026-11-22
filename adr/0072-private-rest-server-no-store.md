# ADR 0072 — Server-enforced non-cacheable private REST responses

- Date: 2026-11-22
- Status: Narrow implementation; exact CI evidence in issue #173
- Issue: #173 (P0 M1 child of privacy epic #2, linked #31/#29/#41)
- Related: ADRs 0068–0071 for bounded API budget and browser session safety

## Problem and trust boundary
Authentication, owner-scoped PostgreSQL FORCE RLS and frontend fetch
cache:'no-store' are necessary but not sufficient to instruct an HTTP
intermediary or browser cache not to store a previous user's private REST
response. The /api/v1/consents and /api/v1/memories success paths returned
JSON with no centralized Cache-Control response policy. Export and some
error handlers explicitly sent no-store, but other successes, 401/403 and
new endpoints could silently omit the header. OWASP and RFC 9111 recommend
server-sent no-store for personal/sensitive responses; no-cache still allows
storage and private alone still allows private-browser storage.

## Decision
Install one ApiPrivateResponseCachePolicy middleware immediately after
WebApplication.Build(), BEFORE recovery quarantine, authentication, any
quota/body middleware and endpoint routing. Match only the /api/v1 path
segment; do not change public /health, /ready or similar non-segment prefix
/api/v1-other. Register OnStarting so the final response for EVERY
/api/v1 path, including errors, short-circuited 401/403/429/503, streaming
personal export and future endpoints, has these server-owned headers:

- Cache-Control: no-store — forbid storage by compliant private and shared
  HTTP caches.
- X-Content-Type-Options: nosniff — prevent browser MIME sniffing of JSON.
- Referrer-Policy: no-referrer — do not leak path/query referrers.

OnStarting overwrites downstream Cache-Control even if a later endpoint
adds a weaker caching directive; does not alter success/error status,
CORS policy, request/response body, auth claims, JWT, tenant/subject or
PostgreSQL transactions. Use no custom source-controlled secrets. Keep
the existing export no-store and rate-limit no-store, both compatible.

## TDD / evidence
Publish a RED xUnit WebApplicationFactory integration regression against
real PostgreSQL and the exact Minimal API, with test-only synthetic
identity (production JWT unaffected). Assert private no-store and nosniff
on successful GET consents/memories, denied 401, 403 no-consent capture,
429 actor quota, 400 erase validation and streaming export; assert health
and /api/v1-other do not inherit the policy. Additional Playwright against
real Keycloak PKCE, signed JWT, ASP.NET API and PostgreSQL verifies HTTP
no-store for 200 and 401. GREEN final must include full .NET xUnit/RLS,
PITR/restore, S3 and Azure contracts, mobile/Expo, browser OIDC and Pages
on the same main commit. Evidence URLs in issue #173.

## Non-goals and residual risks
No-store governs conforming HTTP caches, not the JavaScript Cache API,
service workers, IndexedDB, locally downloaded exports, already cached
responses, rogue proxy/CDN policies overriding origin, device/offline
data stores, client screenshots, provider or backup retention. The
frontend still must not persist tokens or raw private notes and the API
still must enforce signed JWT, RLS and source-scoped consent. No claim of
GDPR compliance or production data release; parent #2/#31/#41 stay OPEN.

## Sources
- https://cheatsheetseries.owasp.org/cheatsheets/HTTP_Headers_Cheat_Sheet.html
- https://cheatsheetseries.owasp.org/cheatsheets/Web_Cache_Security_Cheat_Sheet.html
- https://cheatsheetseries.owasp.org/cheatsheets/REST_Security_Cheat_Sheet.html
- https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Cache-Control
- https://www.rfc-editor.org/rfc/rfc9111
