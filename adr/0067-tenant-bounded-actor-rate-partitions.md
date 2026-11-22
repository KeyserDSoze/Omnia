# ADR 0067 — Per-tenant bounded actor rate partition cardinality

- Date: 2026-11-22
- Status: Implemented narrowly; final SHA-specific Actions verification in #168
- Issue: #168, child of P0 #37 and privacy epic #2
- Extends: ADR 0065 (tenant request quota), ADR 0066 (global actor cardinality)

## Problem

The authenticated API pipeline checks a process-local per-actor fixed
minute budget BEFORE aggregate per-tenant request budget. After tenant A
has already spent its aggregate minute quota, additional distinct signed
subjects in A still allocate global per-actor window entries before the
tenant middleware denies them. Even though #167 bounds the **total**
actor keys, A can exhaust it and starve unrelated signed tenant B.
This is a fairness/capacity issue distinct from byte, buffer and ingress
limits and from the per-tenant request budget itself.

## Decision

Keep the existing actor-first then tenant-first-rate-order, OIDC/JWT
authentication, endpoint authorization, no-queue rejections and
independent 60-second actor/tenant request counters. Introduce a
**separate cardinality cap for tracked actor keys within each tenant**
in the shared ApiFixedWindowRequestLimiter engine. In actor mode,
every TryAcquire requires both account and tenant partition keys, both
derived from the SAME validated OIDC claims by ApiRateLimitPolicy.
No request header, payload, query param or client IP may override them.

New configuration RateLimit:MaxTrackedActorsPerTenant: optional,
default min(256, MaxTrackedActors), valid range 1..MaxTrackedActors;
invalid explicit values fail API startup. The existing global actor cap
defaults to 8192 (max 32768); tenant rate partitions remain bounded to
4096 by default; all old minute quotas remain unchanged. Both the
global actor dictionary and the tenant→active-actor-count dictionary
are protected under ONE short admission lock, with the latter cardinality
bounded by the former. Counters increment only for NEW actor windows
and decrement on the same lazy 15-second expiration sweep that
removes stale windows. Repeated requests from an existing actor do
not allocate a new partition. At per-tenant saturation a NEW actor
gets generic 429 rate_limited, Cache-Control:no-store, Retry-After
1..60, with no queue/body read/handler and no tenant/subject echo.
No active actor is evicted. Retry-After at cardinality saturation
is an advisory retry hint, NOT guaranteed capacity.

## Test-first and validation

Test-only commit on main [9a0c0312](https://github.com/KeyserDSoze/Omnia/commit/9a0c03129e937690490b1c442af7a3a4be1ca62d)
adds a real xUnit WebApplicationFactory + PostgreSQL behavioral
regression: global actor cap 3, per-tenant cap 1, aggregate tenant
quota 1. Tenant A successfully makes one request then sends more
unique signed subjects and spoofed forwarding headers; tenant B
MUST still be admitted. Pre-fix code retains A's three actor keys
despite tenant 429 and rejects B's first request. Record exact
RED and GREEN runs/SHAs in #168 after GitHub Actions complete.

Additional TimeProvider-based regressions cover expiration, concurrent
128 new actors of one tenant, other tenants and invalid configuration.
Keep real browser OIDC, Pages and all existing xUnit/REST/PG/PITR
gates mandatory on the final main SHA.

## Limitations and security tradeoffs

- Does NOT reserve capacity for each tenant: enough tenants may still
  saturate the global cap. New legitimate actors of a busy tenant are
  throttled until expiry cleanup.
- Does NOT refund a tenant's quota if an actor limiter rejects earlier,
  or vice versa; this two-stage accounting is not atomic.
- Does NOT coordinate replica or process budgets. No distributed quota,
  ingress slow-client/DDoS/WAF or media/connector/storage/AI budget.
- No cloud deployment, personal-data release or GDPR completion claim.
  Parent #37 and #2 stay OPEN.

## Official references

- ASP.NET Core 10 rate limiting and chained permits:
  https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0
- Middleware ordering:
  https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/?view=aspnetcore-10.0
- TimeProvider:
  https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider?view=net-10.0
