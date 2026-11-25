# ADR 0066 — Bounded signed actor request-rate partitions

- Date: 2026-11-22
- Status: Accepted narrow per-instance rate cardinality slice; final CI in #167
- Issue: #167, parent P0 API anti-abuse #37 and privacy epic #2
- Extends ADR 0065; supersedes its decision to keep the ASP.NET actor partition factory

## Problem

Existing ASP.NET Core 10 named endpoint policy
RequireRateLimiting("tao-api") enforces a fixed-window
120/min rate for each validated tenant_id+sub, but Tao
had no explicitly bounded number of actor partition
entries retained by that policy. Many independently
authenticated subjects could create arbitrarily many
actor rate buckets, even after ADR 0065 capped aggregate
per-tenant requests/minute and the tenant window store.
This is a process-memory/cardinality attack surface,
not a flaw in JWT authentication or PostgreSQL RLS.

## Decision

Remove the per-actor AddRateLimiter named policy and
RequireRateLimiting endpoint annotations. Replace with
one ApiActorRequestWindowLimiter per API process:
RateLimit:RequestsPerMinute default 120, valid 1..1000
(UNCHANGED actor allowance), plus new
RateLimit:MaxTrackedActors default 8192, valid 1..32768.
Invalid explicit configuration fails startup.

Share the bounded ApiFixedWindowRequestLimiter core
with existing ApiTenantRequestWindowLimiter, preserving
RateLimit:TenantRequestsPerMinute default 600 and
RateLimit:MaxTrackedTenants default 4096. Each facade
has separate active window state under one short lock,
a fixed 60-second quota per key, and 15-second
lazy expiry sweeps during incoming request traffic.
This avoids duplicated expiry logic and an O(N)
scan on every cardinality-saturated rejected request.
Actor keys are capped at 512 characters, tenant keys
at 256. Expired keys can remain in bounded RAM while
idle; no durable storage, background tasks, logging
of keys or raw content.

For authenticated /api/v1 routes use middleware
ordering: UseAuthentication → UseAuthorization →
ApiActorRequestRateBudget →
ApiTenantRequestRateBudget →
ApiJsonBodyBudget → endpoint. Client-supplied
X-Tenant-Id, X-Forwarded-For or request payloads
cannot set the actor or tenant quota identity.
ApiRateLimitPolicy.PartitionKey derives signed
tenant+sub with length framing; TenantPartitionKey
derives the corresponding signed tenant. Incomplete
claims use direct-peer fallback but cannot authorize
protected user mutations. Health/ready, anonymous
401/403 and OPTIONS preflight do not spend these
new minute budgets.

When an actor window is full OR actor partition
cardinality has reached its bound, return generic
429 rate_limited, Cache-Control:no-store and
Retry-After 1..60, without queue, body read,
handler/DB work or private identity echo. Existing
known actors may continue to spend unused requests,
no eviction of active windows to favor new accounts;
unknown actors get fail-closed 429 until expiry
cleanup. The existing per-tenant window remains
an independent second admission step.

## TDD evidence and CI gate

On test-first [b564d5a6](https://github.com/KeyserDSoze/Tao/commit/b564d5a676e80712ef4e0811c3e6219cfbde05aa),
[RED CI 44966863089](https://github.com/KeyserDSoze/Tao/actions/runs/44966863089)
showed 142 passed / 1 failed / 143 total real
PostgreSQL xUnit integration tests: a third
authenticated distinct subject was incorrectly
admitted despite explicit MaxTrackedActors=2.
All prior tests passed, mobile passed; RED was
behavioral, not compilation.

Implementation commit
[d2c8b554](https://github.com/KeyserDSoze/Tao/commit/d2c8b554cdbd260ad1d2ff96cc1faf55e70b6482)
introduces bounded shared windows, actor middleware
and uniform privacy-preserving 429. New actor
regressions test 128 contenders, 1000 expired
actors, deterministic TimeProvider 60s renewal,
invalid config, active actor reuse, GET/POST
no-body-read and signed cross-tenant isolation.
Legacy per-actor WebApplicationFactory, tenant
aggregate and all JSON budget tests remain
mandatory. Issue #167 holds final SHA-specific
CI, physical PostgreSQL/WAL PITR/restore,
S3/Azure contract, mobile/Playwright, OIDC
real-browser and Pages evidence; only close
after all are verified on one final main SHA.

## Limitations

The new limit applies to ONE API process. No
multi-replica coordination, sliding-window fairness,
global slot reservation, ingress/TLS/socket/WAF
or DDoS protection, provider/AI/storage/connector
budgets, realistic load tests or legal GDPR gate.
A busy actor partition dictionary may deny
legitimate new account requests until expiration.
Fixed-window edges still permit bursts across
neighboring 60-second windows. Parent #37/#2 OPEN.

## Official references

- Microsoft ASP.NET Core 10 rate limiting:
  https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0
- ASP.NET Core 10 middleware order:
  https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/?view=aspnetcore-10.0
- .NET TimeProvider:
  https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider?view=net-10.0
- Prior ADR 0065:
  https://github.com/KeyserDSoze/Tao/blob/main/adr/0065-aggregate-tenant-request-rate-window.md
