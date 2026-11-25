# ADR 0064 — Actor-scoped and tenant-bounded JSON buffers through handler lifetime

- Date: 2026-11-22
- Status: Accepted narrow process-local API JSON memory-lifetime contract, CI final evidence in #165
- Issue: #165 under P0 parent #37 and privacy epic #2; extends ADR 0062–0063

## Threat

ADR 0062/0063 caps middleware-owned JSON buffers that persist
into slow downstream handlers (default 64 per API instance,
16 per OIDC tenant). The RECEIVE-only quotas (32 global /
16 tenant / 4 actor) are correctly returned after a body is
received, before the handler starts. A single legitimate
tenant+subject can therefore retain all 16 of its tenant's
buffer slots by starting multiple fast-body, slow-handler
requests, preventing other members of the same tenant from
using otherwise available process slots.

## Decision

Add an actor ceiling to the SAME process-local
ApiJsonBufferedRequestLimiter. Config:
- RequestBody:MaxConcurrentBufferedRequests remains 64 by
  default, explicit range 1..256.
- RequestBody:MaxConcurrentBufferedRequestsPerTenant remains
  optional, default min(16, global), explicit 1..global.
- RequestBody:MaxConcurrentBufferedRequestsPerActor is
  NEW, optional, default min(4, effective tenant cap),
  explicit range 1..effective tenant cap. Invalid
  configuration rejects startup before accepting traffic.

Use ONLY the already defined OIDC-derived
ApiRateLimitPolicy.PartitionKey (length-framed tenant+sub)
and TenantPartitionKey (length-framed tenant) from the
authenticated HttpContext. No client-provided headers,
X-Tenant-Id, X-Forwarded-For or payload fields become quota
identity. Duplicate/malformed claims use the direct-peer
anonymous fallback, and still cannot authorize a mutation.
No new personal-data persistence or logging.

For JSON POST/PUT/PATCH /api/v1, after authenticated policy
and cheap Content-Encoding/Length/Type 413/415 checks,
acquire global, tenant and actor buffered-request permits
atomically under ONE short lock, with NO queue, wait or
buffer allocation on saturation. Reject identically to
previous ADR 0062/0063: 503 request_buffer_busy, no-store,
Retry-After:1, no body read/handler and no actor/tenant
details in the response. A one-use idempotent lease holds
all 3 buffered permits during read AND handler execution,
until the bounded MemoryStream is disposed. Release on all
paths: success, 408, streamed 413, disconnect, stream
exception, handler exception. Both dictionaries retain
only ACTIVE identity counts and remove keys at zero;
each cardinality is <= active global slots.

The read-specific ApiJsonBodyReadConcurrencyLimiter remains
a separate 32/16/4 resource and returns its permit BEFORE
the handler even when the buffered-request lease stays
held through handler completion. Do not conflate the two
resource lifetimes.

## TDD and verification

Behavior-first [test-only commit 8cdda406](https://github.com/KeyserDSoze/Tao/commit/8cdda406a8eb76e2b403561ae3a6294d4677c525)
produced [RED CI 44960870525](https://github.com/KeyserDSoze/Tao/actions/runs/44960870525):
132 of 133 Integration xUnit tests passed, and the new
slow-handler actor test failed exactly at Assert.False
because the fifth same-actor request reached the handler.
Mobile succeeded.

Implementation candidate
[11a6595d](https://github.com/KeyserDSoze/Tao/commit/11a6595df792da62dbcceb85f8ea350fe6b07929)
introduces the atomic third buffered counter, startup
validation and no idle actor keys. Further synthetic tests
cover 128 simultaneous acquisitions, 8/6/2 global/tenant/
actor caps, 1000 sequential principals, key bounds, default
clamping, idempotent release and inter-tenant separation.
Older #163/#164 regressions for 408/413/415, aborted/failed
streams, thrown handler and early read release remain in CI.
Exact GREEN final SHA and all three GitHub Actions are
tracked in issue #165; do not claim validated production
behavior before workflow completion.

## Residual risks and exclusions

This is a process-local ceiling on concurrent JSON buffers
retained by middleware, not a guarantee each tenant/actor
can enter when all global slots are exhausted. No
distributed quota shared by API replicas, fair scheduler,
Kestrel/TLS/proxy/WAF ingress rate capacity, media/connector
budgets, full-process memory cap, database/AI processing
concurrency quota, realistic network load/SLO testing,
consent/GDPR legal release or DR authority change.
Keep parent #37 and epic #2 OPEN.

## Official references

- ASP.NET Core 10 rate/concurrency and partitioned limiting:
  https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0
- .NET Dictionary thread safety:
  https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2?view=net-10.0
- ADR 0063 prior tenant buffered ceiling:
  https://github.com/KeyserDSoze/Tao/blob/main/adr/0063-buffered-json-tenant-ceiling.md
