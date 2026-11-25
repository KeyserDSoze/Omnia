# ADR 0061 — Active-only tenant and actor JSON intake cap

- Date: 2026-11-21
- Status: Accepted for narrow process-local API read-phase scope; final Actions evidence in #162
- Issue: #162, child of P0 #37 / privacy epic #2; follows ADR 0055–0060
- Components: ASP.NET Core 10 API transport-after-OIDC; no new DB, worker, AI or client behavior

## Problem

ADR 0060 limits global concurrent JSON reads and each authenticated
tenant+subject actor. Many valid identities within one tenant can
nonetheless fill ALL global slots, preventing another tenant from
reading when capacity would otherwise be available. A fixed-window
request rate limiter does not cap simultaneous slow body reads.

The per-tenant policy is a resource *ceiling*, not a promise that
every tenant receives reserved capacity at global saturation.

## Decision

Extend the existing ApiJsonBodyReadConcurrencyLimiter with a third
`RequestBody:MaxConcurrentReadsPerTenant` setting, default
min(16, MaxConcurrentReads), explicitly valid 1..MaxConcurrentReads.
The existing global default 32 and actor default min(4,global)
remain unchanged. Invalid explicit values fail API startup.

Derive `ApiRateLimitPolicy.TenantPartitionKey(HttpContext)` from
the SAME validated JWT OIDC tenant_id+sub claim gate used by the
existing actor PartitionKey. Accept only exactly one nonempty
tenant_id (max 128 chars) AND one nonempty sub (max 255); length-
frame the tenant key as tenant:{len}:{value} and preserve the
existing actor account:{tenantlen}:{tenant}:{sublen}:{sub} format.
In the absence of correctly authenticated claims use an anonymous
partition based only on the direct connection peer, never any
X-Forwarded-For or client-supplied X-Tenant-Id header. This fallback
is defence-in-depth: all mutation endpoints require authorization,
and no tenant value from these quotas is used for DB/RLS access.

After authentication, existing fixed-window 429 and authorization,
and cheap 415/413 prechecks, test three counts together under ONE
short lock: total, per tenant, per actor. If any cap is reached,
do not queue, wait, allocate request body buffers or call ReadAsync;
respond 503 with only request_body_busy, Cache-Control:no-store
and Retry-After:1, with no echo or logged identity. Otherwise
increment all three, issuing an idempotent disposable permit.
Decrement all on complete read, 408 total deadline, streamed 413,
client abort or stream fault. Always release BEFORE normal binding,
DB or AI handler work. An actor/tenant entry is removed immediately
when its count becomes zero, bounding active partition cardinality
at the global slots. No persistent tracking of identities is added.

## TDD and evidence

A behavior-first integration test on RED commit
[2765bcae](https://github.com/KeyserDSoze/Tao/commit/2765bcae0fbfc8b62cd1064b8da15ca6cb70d6ce)
held 16 slow JSON streams for four actors from one tenant against
global32/actor4. The next actor of the same tenant incorrectly
reached a handler. [RED CI 44954479076](https://github.com/KeyserDSoze/Tao/actions/runs/44954479076)
failed exactly 1/121 xUnit Integration tests (120 passed),
while mobile passed. Additional regression tests cover 128 parallel
admissions, explicit/global/tenant/actor configuration, eviction
of 1000 sequential tenant keys, per-tenant identity claim collision
and forwarded header spoof, plus previously implemented timeout
and media guards. Final GREEN and documentation HEAD are recorded
in GitHub issue #162; avoid claiming full success before verification.

## Constraints and residual risks

This is a per-process tenant cap, not distributed coordination and
not fairness with reserved capacity when other tenants already hold
all global slots; HTTP sockets, slow TLS/headers, reverse proxies,
WAF, media/connector/provider quotas and load testing remain separate
P0 work. Completed memory buffers live until downstream request
finishes; this quota only bounds active reads. No privacy consent,
RLS, GDPR legal assessment, retention, erasure or production-grade
DR gate is completed by this code. Keep parent #37 and epic #2 OPEN.

## Authoritative references

- Microsoft ASP.NET Core 10 — rate limiting / concurrency vs period,
  queueing and partitioning:
  https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0
- Microsoft .NET — Dictionary thread safety:
  https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2?view=net-10.0
- Prior ADR 0060:
  https://github.com/KeyserDSoze/Tao/blob/main/adr/0060-actor-scoped-json-receive-concurrency.md
