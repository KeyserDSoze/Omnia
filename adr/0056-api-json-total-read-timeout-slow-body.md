# ADR 0056 — JSON API total body read deadline for slow/chunked clients

**Data:** 2026-11-21  
**Stato:** Accepted for a narrow testable application-layer guard; not a complete slowloris/DDoS mitigation.  
**Child issue:** #157 under #37 (P0 anti-abuse), follows #156/ADR 0055.

## Context / decision

ADR 0055 already limits authenticated `POST/PUT/PATCH /api/v1`
JSON request bodies to 64 KiB (configurable 1–64 KiB), rejecting
oversize declared or chunked bodies with 413 and unsupported compression
with 415. However a chunked client can keep the existing read loop
blocked **indefinitely without transmitting another byte**, or send
a trickle of bytes below the size limit. A byte budget does not bound
wall-clock resource occupancy. Kestrel has its own header timeout and
minimum request body data rate, but other hosts, proxies and TestServer
can have different transport behavior.

Add a **total body receive deadline**, not a per-read rolling timeout.
`RequestBody:ReadTimeoutMilliseconds` defaults to **10,000 ms** and
must be in **100–30,000 ms**; invalid values fail application
startup alongside the byte limit.

`ApiJsonBodyBudget` creates a `CancellationTokenSource` linked to
`HttpContext.RequestAborted` and calls `CancelAfter` **once before
the first read**. Use the linked token for every bounded read and
memory-buffer write; never re-arm it when a byte arrives. An
`OperationCanceledException` with the local timer canceled but the
client `RequestAborted` **not** canceled yields HTTP **408**
`{"error":"request_body_timeout"}`, with
`Cache-Control: no-store`. No body echo, logging, JSON binding,
database access, external embedding or erasure operation follows
a failed read. Existing 413/415 pre-checks take precedence.
An actual client disconnect keeps the original cancellation behavior:
**do not** mislabel a disconnected peer as a 408 deadline.

After complete, bounded JSON is in memory, **dispose the body timer
before** handing the stream to the normal Minimal API binder. The
deadline must not accidentally abort a later authorized database/AI
operation. The original `Request.Body` remains restored on exit.
This guard runs after OIDC authentication and the existing per-user
rate limiter/authorization on `/api/v1`, while /health and GET are
unchanged.

## TDD / verification

Test-first `tests/Tao.Integration.Tests/ApiJsonBodyReadTimeoutTests.cs`
with a cancellation-aware synthetic Stream and DefaultHttpContext
tests four independent invariants:

- A never-ending read returns 408 with `no-store` and never
  invokes the downstream handler;
- a drip-fed stream (one byte approximately every 30 ms) cannot
  indefinitely extend a **180 ms total** deadline;
- a caller-aborted request propagates cancellation rather than
  returning a fabricated 408;
- short JSON is passed byte-for-byte and original stream restored.
- Invalid config below 100 or above 30,000 ms fails fast;
  100, 10,000 and 30,000 ms are accepted.

**TDD RED verified:** [test-first `6fbac604`](https://github.com/KeyserDSoze/Tao/commit/6fbac60496363d32e29f77b106f899442d6c15ea)
and [CI 44927574990](https://github.com/KeyserDSoze/Tao/actions/runs/44927574990)
**expected FAILURE**: missing `ValidateReadTimeoutMilliseconds` (CS0117),
no four-argument `InvokeAsync` (CS1501). Existing domain 19/19,
S3 16/16, Azure 31/31 and mobile passed before the integration
compile gate.

**TDD GREEN verified:** [implementation `1a5ecfb8`](https://github.com/KeyserDSoze/Tao/commit/1a5ecfb863649cfc087b49261f55eee2cbbe49fe),
[CI 44927667367](https://github.com/KeyserDSoze/Tao/actions/runs/44927667367)
**SUCCESS**; [ADR/docs `820cb162`](https://github.com/KeyserDSoze/Tao/commit/820cb162e974c23848f285e85a7f98096b47efe2),
[CI 44927797752](https://github.com/KeyserDSoze/Tao/actions/runs/44927797752)
**SUCCESS**: domain 19/19, SeaweedFS S3 16/16, Azure SDK 31/31,
real PostgreSQL integration 87/87 (5 new timeout tests), physical
PITR/multi-cluster witness, Docker and mobile all SUCCESS.
[OIDC real-browser E2E 44927797766](https://github.com/KeyserDSoze/Tao/actions/runs/44927797766)
and [Pages 44927797785](https://github.com/KeyserDSoze/Tao/actions/runs/44927797785)
**SUCCESS** on same `820cb162` SHA.

This evidence amendment creates a **new final main SHA**. Re-run
and confirm all three workflows success on that SHA before closing
#157. These are synthetic streams and do not prove real ingress
slowloris mitigation, physical WAF, distributed quotas or GDPR readiness.

## Security, operational and GDPR limits

- Kestrel minimum data rates, request-header timeout and
  reverse-proxy timeouts/WAF are **separate** necessary controls;
  this middleware cannot block a client during slow headers
  before middleware executes or forcefully interrupt a
  non-cooperative third-party Stream that ignores cancellation.
- No global request processing timeout, slow-response protection,
  per-replica concurrent connection cap, distributed rate counter,
  media upload limit, connector quota, k6 scale proof, ingress
  configuration test, real Azure staging, or GDPR legal signoff.
- Client-disconnect cancellation may cause an aborted connection
  without a readable HTTP 408, which is correct; never claim
  that an already disconnected client received a response.
- No capture/recording of third-party content, no new
  personal-data storage, no payload-specific logging.
  Continue to require tenant/subject auth, consent and PostgreSQL
  RLS before any mutation. Parent **#37 stays OPEN**.

## Primary sources

- [Microsoft — Kestrel security: slow POST, request headers and minimum body data rate](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/security-considerations)
- [Microsoft — request timeouts and RequestAborted](https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts?view=aspnetcore-10.0)
- [Microsoft — using HttpContext and cancellation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/use-http-context?view=aspnetcore-10.0)
- [RFC 9110 — 408 Request Timeout](https://www.rfc-editor.org/rfc/rfc9110.html#section-15.5.9)
- [GDPR (EU) 2016/679](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/)
