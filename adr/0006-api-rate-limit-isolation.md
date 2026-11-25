# ADR 0006 — Per-identity API throttling before SaaS cluster quotas

- Date: 2026-11-17
- Status: accepted for foundation; distributed controls still pending
- Feature: #37 (partial), #102 SaaS quotas (future)

## Context

Tao's data endpoints need predictable resource bounds. A single tenant should not monopolize a process, and untrusted identity headers must not bypass quotas. In a future Azure Container Apps deployment multiple replicas require coordinated budgets.

## Decision

Use built-in ASP.NET Core rate limiting with a fixed 60s window, configurable 120 requests per signed `tenant_id`+`sub` per instance and no queue. Authentication precedes the middleware; unauthenticated access is keyed by the actual peer IP rather than an untrusted `X-Forwarded-For`. Apply to all API v1 groups, not `/health`; return 429 `rate_limited` and `Retry-After`. Reject invalid limiter settings at startup.

## Verification

A real ASP.NET Core TestServer exercises HTTP requests, JWT shape emulated by a test-only auth handler, and actual disposable PostgreSQL. Separate #17 must validate the real OIDC/JWKS browser login. Test at limit+1 for 429, new subject/tenant for independent quotas, health not throttled.

## Limitations

This is process-local, not global multi-replica regulation, and not a DDoS shield. Missing: edge rate limits, distributed counters, upload/media size and throughput, per-connector or AI quotas, metrics/alerts. Keep #37 open.
