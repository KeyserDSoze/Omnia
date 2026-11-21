# ADR 0058 — Fail-fast process-local concurrency cap for JSON body reads

- Date: 2026-11-21
- Status: Accepted as a bounded in-process API middleware slice; CI evidence tracked in #159
- Issue: #159 (child of #37, epic #2). Depends on ADR 0055, 0056 and 0057.
- Phase: P0 / M1 secure timeline, no expanded data processing or external integrations.

## Context

The current JSON API middleware enforces a size cap (including
unknown Content-Length), total receive timeout and media-type checks.
However, many authenticated slow connections could concurrently block
on ReadAsync, each retaining its own per-request buffer and a task.
Rate limiting counts arrivals per user/window, not concurrent body
receivers. A non-queued, resource-specific admission control limits
the *number of simultaneously active body reads* on one API process.

## Decision

The API creates **one shared ApiJsonBodyReadConcurrencyLimiter** at
startup. RequestBody:MaxConcurrentReads defaults to 32, valid 1..256
inclusive; invalid config throws before the app begins serving.

For JSON POST/PUT/PATCH under /api/v1, AFTER recovery gate,
authentication, per-principal rate limit and authorization,
ApiJsonBodyBudget still checks Content-Encoding (415), declared
Content-Length (413), Content-Type/UTF-8 (415), then calls the shared
limiter's **non-blocking Wait(0)**. A null permit yields HTTP **503**
with an uninformative JSON error request_body_busy,
Cache-Control: no-store and Retry-After: 1. No body bytes are
read and the ordinary handler is never called. No queue or retry
is performed automatically.

A one-use, idempotent IDisposable lease owns one SemaphoreSlim
permit. Its lifetime covers only reading into a bounded memory
buffer. A using guard releases on exceptions, client abort, 408
total deadline, 413 for streamed oversize and success. The lease
is explicitly disposed **before** normal JSON model binding,
DB/AI provider calls or any other handler work. A downstream
handler must not monopolize an intake permit.

No tenant IDs, user IDs, JSON snippets, tokens or request headers
are logged or reflected in the result. Existing RLS, consent,
authentication, 429 per-identity, JSON validation and recovery
quarantine remain independently enforced.

## Testing and evidence policy

Test-first #159 exercises a one-slot limiter with a body stream
that blocks until released: a simultaneous request must fail 503
before its ReadAsync. Additional cases verify 415/413 precedence,
timeout 408, client abort, permit release before slow handler,
and configuration bounds. Unit-level synthetic middleware tests
are not network/ingress or PostgreSQL/OIDC attestation; CI on main
must also pass the existing real PostgreSQL, Keycloak browser,
provider contracts, Docker, mobile and Pages workflows. Follow
issue #159 for SHA-specific observed RED/GREEN evidence.

## Explicit limits

- **Not distributed**: each replica has its own 32 slots; not a
  global quota, not tenant-fair, and not an identity admission limit.
- **Not end-to-end memory budget**: completed JSON memory buffers
  persist until downstream request completion.
- **Not connection protection**: Kestrel sockets, TLS, headers,
  proxy queues and incomplete unauthenticated requests are outside
  this middleware. Streams that ignore cancellation remain a risk.
- No binary/media upload, connector quotas, k6 load or edge WAF.
  GDPR and security release remain open; parent #37 and #2 OPEN.

## References

- Microsoft ASP.NET Core concurrency/rate limiting and no queue:
  https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0
- Microsoft Kestrel slow POST and server security:
  https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/security-considerations?view=aspnetcore-10.0
- Microsoft SemaphoreSlim, acquisition and release:
  https://learn.microsoft.com/en-us/dotnet/standard/threading/semaphore-and-semaphoreslim
