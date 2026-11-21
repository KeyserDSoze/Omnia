# ADR 0062 — Bounded lifetime of middleware-owned JSON request buffers

- Date: 2026-11-21
- Status: Accepted narrow process-local JSON resource-safety slice
- Issue: #163, parent #37 and epic #2; follows ADR 0055–0061

## Problem

Previous per-instance read admissions limit simultaneous JSON
receivers to global/tenant/actor 32/16/4 and release immediately
after complete buffering. The 64KiB-bounded MemoryStream continues
to exist until normal API handling completes, so many fast intake
requests entering slow downstream handlers could retain an
unbounded *number* of these individually bounded buffers.

## Decision

Introduce one process-local ApiJsonBufferedRequestLimiter using
SemaphoreSlim.Wait(0) and an idempotent one-use lease. Configure
RequestBody:MaxConcurrentBufferedRequests default 64, valid
1..256 inclusive; fail startup outside this range. After JWT/
OIDC, rate limiting and authorization, after the existing cheap
Content-Type/Content-Encoding/Content-Length 413/415 prechecks,
acquire without wait/queue BEFORE any buffer/ReadAsync. Saturation
returns HTTP 503 with only request_buffer_busy, Retry-After:1,
Cache-Control:no-store; no body read, binder, DB/AI side effect,
or payload/identity echo.

A new outer using scope holds the buffered-request permit during
both body reception and downstream handler, disposing it AFTER
the bounded MemoryStream is disposed and Request.Body restored.
Errors, timeout 408, streamed 413, disconnect and handler
exceptions all release the idempotent permit. Crucially the
separate global/tenant/actor RECEIVE permit must still be
explicitly disposed BEFORE downstream processing.

## TDD evidence policy

The test-first compile RED uses a missing buffered limiter type
and new optional InvokeAsync parameter; verify the CI failure
before implementing. Subsequent synthetic regression tests hold
one slow handler after completed JSON read, prove the reader
permit is free while buffer slot is occupied, and verify another
request fails 503 without reading bytes. Further checks cover
config, idempotent/parallel nonqueue admission, 413/415
precedence, GET bypass and recovery after client abort, stream
fault, timeout, streamed oversize and handler exception.
Track exact RED and GREEN workflow SHA and links in issue #163.
Retain CI xUnit with real PostgreSQL, S3/Azure contracts, physical
PITR, Docker/mobile, OIDC browser E2E and Pages on final main SHA.

## Limits

The default cap bounds simultaneously middleware-owned JSON
body *contents* at up to 64 x 64KiB (about 4MiB payload),
NOT total managed allocations, parser/handler copies, DB or AI
resource utilization, non-JSON API routes or other incoming
connections. A tenant can monopolize all buffers; there is
no per-tenant processing fairness, multi-replica coordination,
WAF, edge ingress/TLS protection, media/connector quota or
load test. No new personal data processing, RLS bypass, consent,
GDPR attestation or production release. #37 and #2 OPEN.

## Authoritative documentation

- https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0
- https://learn.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim?view=net-10.0
- https://learn.microsoft.com/en-us/aspnet/core/fundamentals/use-http-context?view=aspnetcore-10.0
