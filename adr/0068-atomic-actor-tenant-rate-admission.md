# ADR 0068 — Atomic per-process OIDC actor and tenant request admission

- Date: 2026-11-22
- Status: Narrow implementation; verify final main Actions in #169
- Issue: #169 under P0 anti-abuse #37 / epic privacy #2
- Supersedes: two-stage HTTP admission order in ADRs 0065–0067

## Context
The prior API performs actor-rate admission before aggregate tenant-rate
admission. A signed actor which passes actor quota but is subsequently denied
by tenant quota has ALREADY consumed one actor minute permit and, for a
previously unseen subject, one bounded actor dictionary entry. With a small
global actor partition cap, multiple tenant-denied requests may starve
unrelated tenants despite #168's per-tenant cardinality cap. Simply reversing
the pipeline would consume tenant quota when actor admission later fails.
Microsoft explicitly documents that chained fixed-window permits cannot be
refunded after a downstream rejection.

## Decision
For authenticated, authorized /api/v1 requests use ONE
ApiAtomicRequestRateBudget middleware. On the same authenticated
HttpContext extract actor and tenant framed keys from validated JWT claims;
never use body, X-Tenant-Id or X-Forwarded-For. A single per-process
ApiAtomicRequestWindowLimiter lock serializes all live two-budget
admissions, probes actor and tenant fixed-window engines without spending
quota, and commits exactly one unit to both engines ONLY when both can
accept. A probe is permitted to lazily sweep expired partitions; it must
not create new keys or increment rate counts. Keep the actor and tenant
window engines and their existing 60s/15s expiry, config validation,
per-tenant actor partition ceiling, global partitions and retry limits.
The legacy standalone admission facades remain for narrow unit tests, but
they MUST NOT be registered as production HTTP pipeline steps or published
as independently mutating singleton services.

At either saturation, return the existing generic 429 rate_limited,
Cache-Control:no-store, Retry-After 1..60, no queue and no request body
read, binder, database work, identity echo or partial quota charge.
Maintain authorization, CORS preflight, /health and /ready semantics.
The runtime coordinator lock is short and held only for in-RAM rate-window
dictionary reads and writes, never around async HTTP/SQL/AI work.

## Evidence and TDD
[RED commit 717e3de](https://github.com/KeyserDSoze/Tao/commit/717e3de4145b667699a778f6d29a00cc001017ce)
on [Tao CI 45004920213](https://github.com/KeyserDSoze/Tao/actions/runs/45004920213):
153/154 real PostgreSQL xUnit integration tests passed; only
Tenant429MustNotAllocateActorPartitionBeforeAnotherTenantArrives failed
(expected 200, actual 429 for independent tenant). Domain 19/19,
S3 16/16, Azure SDK 31/31 passed, so this is behavioral RED.
GREEN/final CI and exact SHA must be recorded in #169 before closure.
Add deterministic window tests for actor-denial not consuming tenant,
tenant-denial not creating actor partition, 128 parallel signed subjects,
partition cardinality and 60-second expiry; retain real OIDC browser,
mobile, Docker, physical PostgreSQL PITR and Pages CI gates.

## Limits / residual risks
Atomicity is process-local in the single live HTTP coordinator, not a
distributed transaction across API replicas or external quota stores.
The independent facade objects MUST NOT be called directly from other
production paths while using the coordinator; its invariant explicitly
depends on that encapsulation. Fixed windows still allow boundary
bursts and expired keys are cleaned lazily. No tenant capacity reservation
when the global actor cap is full; no pre-auth WAF/edge protection, quotas
for media/connector/AI/storage, real workload benchmarking, GDPR signoff
or authorization to accept real personal data. #37 and #2 stay OPEN.

## Official references
- https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0
- https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/?view=aspnetcore-10.0
