# ADR 0055 — API JSON request-body budget, bounded streaming and no implicit decompression

**Data:** 2026-11-21  
**Stato:** Accepted for authenticated API JSON mutations and CI-only contract; not complete SaaS anti-abuse.  
**Issue child:** #156 under parent #37 and epic #2.  
**Requisiti:** ASP.NET Core 10, CORS/JWT, PostgreSQL RLS, privacy, SLO/DoS hardening.

## Problema

`Program.cs` already configures Kestrel `MaxRequestBodySize=70KiB`,
but this is a **server limit**, not necessarily applied by
WebApplicationFactory/TestServer or other ingress configurations.
Body lengths may be absent with `Transfer-Encoding: chunked`, and a
request can reach the JSON model binder or downstream services before
application-specific size/encoding checks. Unbounded buffer allocations
and accidental body logging would be especially dangerous for an
application processing private notes and consent/erase commands.

## Decisione (vertical slice)

Add a small `ApiJsonBodyBudget` middleware for **POST/PUT/PATCH**
requests under `/api/v1`. Execute it **after** OIDC authentication,
the existing identity-partitioned rate limiter and authorization,
and **before** Minimal API JSON binding, DB, consent and AI work.
Anonymous/invalid sessions are still denied at the authentication
boundary, without pre-buffering their bodies.

Configure `RequestBody:MaxJsonBytes` with default 65536 bytes and
fail-fast valid range **1024–65536**. Keep the Kestrel 70KiB upper
bound as defense in depth. The middleware:
1. rejects `Content-Encoding` other than absent or exact `identity`
   with HTTP **415** and a generic `unsupported_content_encoding`
   error; no automatic decompression or expansion is enabled;
2. rejects an advertised `Content-Length` above the budget with
   HTTP **413** `request_body_too_large`, before reading;
3. **does not trust** missing/small `Content-Length`: reads the
   incoming body with bounded chunks of at most
   `remainingBudget+1` and never buffers beyond `MaxJsonBytes`,
   so chunked streaming also receives **413** at the boundary;
4. for a successful small body rewinds the exact bytes into a
   memory stream for the existing JSON binder and restores the
   original input stream in a `finally`; no payload content,
   tenant, subject, token or body bytes are logged or stored;
5. sets `Cache-Control: no-store` for 413/415 responses and
   emits generic JSON errors. `GET /health`, `/ready` and other
   non-JSON routes are unaffected by this app-specific budget.

This is a **resource cap**, not an assertion of client honesty or
a distributed entitlement. A malicious slow client can still use
connections/CPU until ingress-level request timeouts intervene.
The scheme does not allow new file/audio uploads, decode gzip, or
replace tenant/owner validation. Existing `429` budgets, JWT+RLS
and recovery quarantine remain unchanged.

## TDD / tests

Test-first `tests/Tao.Integration.Tests/ApiJsonBodyBudgetTests.cs`
runs via WebApplicationFactory + disposable real PostgreSQL
roles and a **test-only fake auth handler**. It asserts declared
oversize `Content-Length` -> 413, chunked/unknown Content-Length
oversize -> 413, compressed `Content-Encoding: gzip` -> 415,
small valid JSON flows into the existing application and gets
the ordinary validation 400, health/consent GET remain valid,
and config outside the permitted range is refused.
No test sends user personal data; no test claims real OIDC
validation (the separate real-browser/JWKS workflow covers it).

- **RED verificato**: test-first
  [`5ed18198`](https://github.com/KeyserDSoze/Tao/commit/5ed18198b59c8d13e529bfe93609243f46c101f2),
  [CI 44925769441](https://github.com/KeyserDSoze/Tao/actions/runs/44925769441)
  **FAILURE attesa CS0103** nei test Integration per `ApiJsonBodyBudget`
  mancante; Domain 19/19, S3 16/16 e Azure 31/31 erano
  passati prima della compilazione Integration.
- **GREEN verificato**: implementazione classe
  [`00a38732`](https://github.com/KeyserDSoze/Tao/commit/00a38732a5cf6897588b722b2a523184fe19825e),
  registrazione API e config
  [`8b3c2845`](https://github.com/KeyserDSoze/Tao/commit/8b3c2845c2fa8575179d63a8a3eac6123d5a2bcd),
  ADR/docs [`a0565c74`](https://github.com/KeyserDSoze/Tao/commit/a0565c74c9420f3a8797c41bd4fbb88b39100773).
  [CI 44926025165](https://github.com/KeyserDSoze/Tao/actions/runs/44926025165)
  **SUCCESS** sullo stesso SHA: 82/82 real PostgreSQL Integration
  (inclusi HTTP 413/415 e chunked), 19/19 Domain, 16/16 SeaweedFS
  S3, 31/31 Azure SDK; recovery/PITR fisici, Docker e mobile.
  [OIDC 44926025187](https://github.com/KeyserDSoze/Tao/actions/runs/44926025187)
  e [Pages 44926025208](https://github.com/KeyserDSoze/Tao/actions/runs/44926025208)
  **SUCCESS** sul commit `a0565c74`.
- L'aggiornamento ADR con questa prova crea un **nuovo commit main**:
  prima della chiusura verificare nuovamente CI/OIDC/Pages tutte
  GREEN sullo SHA finale. I test non dimostrano produzione anti-DDoS,
  quote distribuite, WAF o gestione sicura degli upload media.

## Limitazioni, threat model, privacy

- No multi-replica distributed quota/counter (fixed-window limiter is
  in-process), CDN/WAF/DDOS, body slowloris/connection timeouts,
  k6 scale test, per-client or per-connector storage/compute quotas,
  binary upload budgets, verified production ingress settings,
  automatic third-party capture or production GDPR certification.
- Neither 413 nor 415 should reveal private content, actor identity,
  current quota window, upload contents or request body fragments.
- API identity derives from verified OIDC claims in production, never
  from `X-Test-*` headers (test host only). PostgreSQL RLS and
  recovery quarantine remain independent controls.
- Keep **parent #37 OPEN**, plus cross-cutting #2/#31 and all
  DR/cloud readiness blockers. Do not equate this slice with a
  production-ready anti-abuse gateway.

## Fonti

- [Microsoft — ASP.NET Core request size vs decompression](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/request-decompression?view=aspnetcore-10.0)
- [Microsoft — ASP.NET Core rate limiting and middleware ordering](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0)
- [IHttpMaxRequestBodySizeFeature](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.features.ihttpmaxrequestbodysizefeature?view=aspnetcore-10.0)
- [RFC 9110 — 413 Content Too Large](https://www.rfc-editor.org/rfc/rfc9110.html#section-15.5.14)
- [GDPR (EU) 2016/679](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/)
