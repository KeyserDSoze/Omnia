# ADR 0065 — Aggregate authenticated tenant request windows

- Date: 2026-11-22
- Status: Accepted for narrow per-process HTTP admission; final CI in #166
- Issue: #166 under anti-abuse #37 and privacy epic #2

## Context
The existing ASP.NET Core endpoint policy enforces
RateLimit:RequestsPerMinute=120 for each signed tenant+subject.
Many valid accounts in the same tenant can therefore send
more than 120 total requests/minute even when each actor
respects its own quota. Existing JSON receiver/buffer
admission restricts concurrency but NOT fast GET, DELETE or
overall request rate over time.

## Decision
Introduce ApiTenantRequestWindowLimiter, one process-local
fixed 60-second window per validated OIDC tenant. Add its
middleware AFTER UseAuthentication, UseRateLimiter and
UseAuthorization and BEFORE ApiJsonBodyBudget/DB handler.
Cover all authenticated /api/v1 HTTP methods; preserve
health/ready, preflight and anonymous challenge semantics.

Config:
- RateLimit:TenantRequestsPerMinute default 600,
  valid 1..10000, fail startup on invalid value;
- RateLimit:MaxTrackedTenants default 4096,
  valid 1..16384, fail startup on invalid value.
The actor 120/min policy remains active independently.

ApiRateLimitPolicy.TenantPartitionKey derives the tenant
solely from unique bounded signed OIDC tenant_id and sub.
Caller headers X-Tenant-Id, X-Forwarded-For, JSON and query
values MUST NOT define quota identity. Invalid claims
fall back to direct-peer partition but do not authorize
protected mutations. One lock serializes fixed-window
admission for concurrent subjects and no queue or wait is
permitted. On saturation return generic HTTP 429
rate_limited, Cache-Control:no-store and Retry-After
between 1 and 60 seconds BEFORE any body/handler/DB work,
without identifying tenant, actor or payload in the error.

A dictionary strictly bounds the tenant partitions.
Opportunistically clean expired windows at most once
every 15 seconds when traffic arrives; DO NOT scan
all partitions on each denied request. When at capacity,
fail closed for new tenant keys rather than evicting
active tenants or allowing unbounded memory. Idle keys
may linger in bounded RAM without incoming requests;
never log/persist them. This is deliberately a capacity/
availability tradeoff and fixed-window edges can permit
bursts across adjacent 60-second periods.

## TDD evidence and verification gate
Test-first RED [4096db9a](https://github.com/KeyserDSoze/Omnia/commit/4096db9a38bda0ef4127ffd815f17b076ca1462a)
on [CI 44962256629](https://github.com/KeyserDSoze/Omnia/actions/runs/44962256629):
135 passed, 1 behaviorally failed of 136 real
PostgreSQL Integration tests: third subject of one
tenant incorrectly got 200 instead of aggregate 429
despite per-sub 10/min policy, while mobile passed.
Implementation and final CI evidence live in issue #166.
New manual TimeProvider tests cover 60-second expiry,
Retry-After values, 128 concurrent admissions,
1000 tenant partition reclamations, cardinality
saturation fail-closed, invalid config and 429 before
body reads. Require same SHA successful Domain, real
PostgreSQL/RLS, S3/Azure, physical PITR, Docker/mobile,
OIDC real browser and Pages before closing.

## Residual risks / non-goals
This is process-local only, NOT a distributed quota
between Azure/Compose replicas. Fixed windows can
double burst across boundaries. New legitimate tenants
may get 429 during cardinality saturation. Existing
per-actor limiter's partition retention is unchanged.
No anonymous ingress/socket/WAF DDoS control, media/
connector/storage/AI budget, guaranteed tenant slots,
realistic network load, legal GDPR/security signoff
or production readiness. Keep #37 and #2 OPEN.

## Official documentation
- https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0
- https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/?view=aspnetcore-10.0
- https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider?view=net-10.0
