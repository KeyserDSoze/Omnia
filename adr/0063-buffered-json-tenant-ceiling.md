# ADR 0063 — Tenant-bounded active JSON buffers during endpoint processing

- Date: 2026-11-21
- Status: Accepted as a narrow process-local API intake/handler buffer slice; CI evidence in #164
- Issue: #164, child of P0 API anti-abuse #37 / privacy epic #2
- Supersedes no previous decisions; extends ADR 0062

## Threat

ADR 0062 caps the global number of middleware-owned JSON buffers
that survive into a downstream Minimal API handler (64 by default).
An authenticated tenant can still keep all these slots occupied
using multiple independently validated actor identities, while
the 32/16/4 JSON **receiving** permits have already released.
This leads to avoidable process-local cross-tenant resource
monopolization during slow handler work.

## Decision

Add a second ceiling to ApiJsonBufferedRequestLimiter:
RequestBody:MaxConcurrentBufferedRequestsPerTenant is **optional**,
default min(16, MaxConcurrentBufferedRequests), valid explicit
range 1..global. Retain the global cap default 64, valid 1..256.
An invalid explicit tenant setting throws before API startup;
omitting the tenant override preserves valid low-global settings.

Derive the key with ApiRateLimitPolicy.TenantPartitionKey(HttpContext),
which uses only an authenticated OIDC JWT containing exactly
one nonblank bounded tenant_id and exactly one valid subject.
Multiple subjects in the same tenant SHARE the ceiling;
equal subjects in different tenants do not. Incomplete claims
use the direct-peer anonymous fallback, and cannot authorize a
mutation. Do not accept X-Tenant-Id, X-Forwarded-For or payload
values as quota partition keys.

For JSON POST/PUT/PATCH /api/v1, AFTER authentication, existing
429/authorization, cheap media-encoding/length 413/415 prechecks,
atomically check and increment both the global and tenant
counters under ONE lock, BEFORE allocating a buffer/ReadAsync.
If full, return generic HTTP 503 request_buffer_busy with
no-store/Retry-After:1 and **no** body read or handler. No queue.
A one-use idempotent IDisposable permit holds both resources
until the bounded MemoryStream is disposed AND the downstream
handler finishes. Success, timeout 408, streamed oversize 413,
client abort and any input/handler failure release both.

A short-lived dictionary stores only ACTIVE tenant counts.
Remove entries at zero; live key cardinality never exceeds
global active retained buffers. Expose only anonymous count
for tests, never log or persist tenant keys, payload or JWT.
The separate receive permits (global/tenant/actor 32/16/4)
still release *before* downstream handler execution.

## TDD and test scope

Test-only RED [c6b0abed](https://github.com/KeyserDSoze/Omnia/commit/c6b0abedc74044dacd3856f3982e4731eea71455)
on [CI 44957612052](https://github.com/KeyserDSoze/Omnia/actions/runs/44957612052)
exercises sixteen already-buffered synthetic JSON bodies,
each from distinct actor tenant A held in the handler. A
seventeenth tenant A request unexpectedly attempted ReadAsync
and failed at MustNotReadStream; 129 prior integration tests
passed, so the RED is behavioral, not missing source types.
GREEN implementation, extra tests and final main SHA-specific
CI evidence live in issue #164. Regressions cover 128-way
contention of global/tenant capacity, 1000 sequential inactive
tenant partitions, explicit invalid/default clamp, 503 with
no read/PII, other tenant success and spoofed headers. Earlier
#163 tests continue protecting buffer release in every failure
path and read permits' early release. Use real PostgreSQL,
Domain, S3/Azure provider contract, physical PITR/restore,
Docker/mobile, OIDC real-browser and Pages gates before close.

## Limitations

This is a **ceiling** for per-tenant middleware-owned in-memory
JSON buffers on ONE API process, not a reservation guaranteeing
another tenant capacity after the global pool is full. Not a
distributed limit between Azure/Compose instances, per-actor
processing limit, all-memory or DB/AI concurrency cap, Kestrel
connection/TLS/proxy/WAF mitigation, binary upload/connector
quota, real-world load test or GDPR/security release.
No new data storage/capture, consent processing, RLS bypass,
erasure or recovery gate. Keep #37/#2 OPEN.

## Authoritative references

- ASP.NET Core 10 concurrency and partitioned rate limiting:
  https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0
- .NET 10 Dictionary read/write synchronization:
  https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2?view=net-10.0
- Earlier retained-buffer lifetime ADR 0062:
  https://github.com/KeyserDSoze/Omnia/blob/main/adr/0062-bounded-json-buffer-lifetime.md
