# ADR 0059 — Kestrel header timeout and bounded pre-auth transport

**Date:** 2026-11-21
**Status:** Accepted for direct Kestrel HTTP transport; CI evidence in #160
**Issue:** #160, P0/M1 child #37 and epic #2, following ADR 0055–0058
**Environment:** ASP.NET Core 10 / local Docker and Azure future target

## Problem and trust boundary

The application JSON byte/time/concurrency middleware only runs after
HTTP request headers have been received and routed, and OIDC has
authenticated an actor. A slow sender holding incomplete headers can
occupy Kestrel transport resources *before* those middleware execute.
HTTP header size and count also consume parser work independently of
the JSON body limit. This is not solved by an identity rate limiter.

## Decision

Use KestrelServerLimits via one API-owned
ApiKestrelTransportLimits.Configure (and Validate before startup):

- Transport:RequestHeadersTimeoutMilliseconds default 10000,
  valid 1000..30000; assign RequestHeadersTimeout.
- Transport:MaxRequestHeadersTotalBytes default 16384, valid
  8192..32768; assign MaxRequestHeadersTotalSize.
- Transport:MaxRequestHeaderCount default 64, valid 16..100;
  assign MaxRequestHeaderCount.
- Preserve 70*1024 maximum raw request bytes in Kestrel; application
  JSON still uses a separate 64KiB cap and accepts no media uploads.
- Explicitly enable MinRequestBodyDataRate at the framework default
  240 B/s, grace period 5s, not null/disabled. This rate is NOT the
  total read deadline (application ADR 0056).
- Reject invalid ranges before Kestrel begins binding sockets; no
  HTTP route or operator override disables these limits on demand.
- Do not log raw header values or reflect request bytes in errors.

## TDD and limits of evidence

The test-first xUnit suite uses KestrelServerLimits for negative and
positive configuration and runs an actual disposable local Kestrel
HTTP/1.1 loopback socket. A deliberately incomplete header must be
closed/timed out by transport without ever entering the endpoint,
and an ordinary completed GET succeeds. This validates direct HTTP
parser behavior and configuration only, not any cloud front-end.
The RED and final GREEN commit SHA/workflow evidence live in #160.
Keep #37 OPEN until ingress, reverse proxy, multi-replica limits,
tenant fairness, binary media quotas, WAF, load tests and operational
alerts are tested separately. It does not establish GDPR readiness.

## Primary references

- Microsoft, Kestrel security considerations (headers timeout,
  minimum request rate and transport scope):
  https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/security-considerations?view=aspnetcore-10.0
- Microsoft, Kestrel server options:
  https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel?view=aspnetcore-10.0
- Microsoft, KestrelServerLimits:
  https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits?view=aspnetcore-10.0
