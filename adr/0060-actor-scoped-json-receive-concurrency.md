# ADR 0060 — Actor-scoped and globally bounded JSON intake without idle partition retention

- Date: 2026-11-21
- Status: Accepted as a narrow, verified-in-CI process-local API intake contract; evidence in issue #161
- Issue: #161, child of #37 / epic #2 (P0/M1), follows ADR 0055–0059

## Threat and missing behavior

A process-local limit on *all* in-flight authenticated JSON
request-body reads (#159) prevents unbounded per-instance reader
tasks, but a single valid OIDC tenant+subject can occupy the entire
instance by initiating multiple slow bodies. Per-user fixed-window
rate limiting counts requests rather than concurrent in-flight reads.
This is avoidable, instance-local unfairness; it does not require a
new distributed service or additional user data storage.

ASP.NET Core supports partitioned concurrency rate limiting, but
some per-key limiter approaches cache partitions after they are idle.
For a receive-only, global permit-capped resource, an active-only
dictionary under a short lock makes bounded cardinality explicit.

## Decision

Preserve `RequestBody:MaxConcurrentReads` 1..256 default 32 and
introduce optional `RequestBody:MaxConcurrentReadsPerActor`,
default min(4, global) and valid **1..global**. Invalid explicit
configuration rejects application startup.

After OIDC authentication, per-principal rate limiting,
authorization and 413/415 cheap rejection, the JSON body guard
acquires a lease under one lock only if BOTH total active reads
and that actor's active reads are below their limits. No queue,
wait, retry, buffer allocation or ReadAsync takes place when
saturated; return HTTP 503 with only `request_body_busy`,
`Retry-After: 1` and `Cache-Control: no-store`.

Use the same `ApiRateLimitPolicy.PartitionKey(context)` as the
authenticated rate limiter. It uses the length-framed tenant_id+sub
from the validated bearer JWT; if no valid authenticated principal
exists, only the network peer IP (not X-Forwarded-For) provides the
fallback key. This component MUST NOT accept any tenant/subject,
X-Forwarded-For, JSON, or arbitrary identity header from clients
as a quota identity. Derivation remains the responsibility of the
upstream JWT/OIDC authentication pipeline, independently tested.

The active-only dictionary is protected by the same lock as global
capacity counters; entries are removed immediately when the last
permit of that actor is disposed. Its cardinality never exceeds
the global simultaneous body-read count. A bounded partition key
avoids accidental retention of arbitrary long strings. A one-use
idempotent lease releases both counters on every failure/success,
including read timeout, size failure, stream exception or abort.
After successful buffering, release BEFORE running downstream JSON
binding, PostgreSQL or AI. Do not log, emit, export or persist keys.

## TDD and threat review

Test-first [commit c7ca11e6](https://github.com/KeyserDSoze/Omnia/commit/c7ca11e60a70b9288b9c4c2eef8ed5e835ef34f5)
exercises the old global-only behavior via synthetic held body
streams and expects the fifth request of the same actor to be denied
while a second actor is accepted. CI run 44952228652 confirms the
RED was behavioral (117 passed / 1 failed). Subsequent tests cover
atomic parallel admission, global cap, idle actor eviction across
1000 unique principals, idempotent release, startup validation and
X-Forwarded-For spoofing. CI GREEN result and final SHA are tracked
in #161; do not assert success before Actions finish.

## Limitations and non-goals

This is **per signed actor per API process**, not a cluster-wide
distributed limiter or even tenant fairness: attackers with multiple
legitimate OIDC identities in one tenant can still saturate the
global pool. It is not a TCP/TLS header limiter, a cap on completed
handler buffer memory, a device quota, a binary upload budget, or
a WAF/edge protection. Nor does it authorize data capture or relax
consent, row-level security, GDPR export/erasure or recovery gates.
Child #161 may close after verified CI on final main SHA, but
parent #37, epic #2 and production compliance gates stay OPEN.

## References

- ASP.NET Core 10 rate-limiting, including concurrency vs rate
  limit and partitioned fairness:
  https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0
- .NET Dictionary concurrent access requires synchronization:
  https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2?view=net-10.0
- ADR 0058, global read cap:
  https://github.com/KeyserDSoze/Omnia/blob/main/adr/0058-json-body-read-concurrency.md
