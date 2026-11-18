# Omnia — repository instructions

**Canonical repository:** https://github.com/KeyserDSoze/Omnia  
**Working branch:** `main` when requested by the owner. Never force push.

## Before changing code

1. Read `README.md`, `docs/architecture.md`, `docs/roadmap.md` and applicable `adr/`.
2. Create/track issues for non-trivial features; document architectural decisions in ADR.
3. Write a failing behavioral test before implementing a feature (red → green → refactor).
4. Run real xUnit, typecheck, Vitest, Playwright, and PostgreSQL tests; never describe unexecuted tests as passed.
5. API mutations must be tenant-scoped and authenticated, and third-party actions require opt-in, scoped privileges and an audit trail.

## Boundaries

- `src/backend/Omnia.Domain`: invariants and entities; no cloud SDKs.
- `src/backend/Omnia.Application`: use cases and ports; no Npgsql/Azure.
- `src/backend/Omnia.Infrastructure`: PostgreSQL migrations and embedding adapters.
- `src/backend/Omnia.Api`: REST, JWT/OIDC, CORS, DI.
- `src/mobile`: Expo React Native and shared web UI; must work with Pages static export.
- `tests/`: xUnit unit + PostgreSQL integration, Vitest pure client logic, Playwright browser.
- `docker/`: disposable local equivalents; `infra/azure`: cloud provisioning templates.
- `docs/` and `adr/`: architecture, security and decisions.

## Safety

No hardcoded credentials, OAuth refresh tokens or private personal content in git. GitHub Pages is public static hosting, not a backend. No unconsented recording, passive surveillance, automatic messaging, invasive third-party capture or claims of universal access. A plugin/connector is NOT trusted code: use declared capabilities, least privilege, secrets vault, explicit user consent, network isolation and audit. Protect the canonical identity and immutable provenance of an event; never rewrite its raw content during normalization. Do not show deterministic keyword tagging as AI insight. No production deployment until threat model, consent/retention, genuine end-to-end authentication and security gates pass.

## Commands

```sh
cd src/mobile && npm install && npm run typecheck && npm test && npm run build:web
cd ../..
dotnet test tests/Omnia.Domain.Tests/Omnia.Domain.Tests.csproj
docker compose --env-file docker/.env -f docker/compose.yml up --build
# OMNIA_TEST_DB=... dotnet test tests/Omnia.Integration.Tests/Omnia.Integration.Tests.csproj
```

## Backlog e feature management

Leggere `docs/backlog.md` e `docs/product-requirements.md` prima di nuove funzionalità. Non duplicare issue: cercare per area/titolo. Ogni nuova feature usa `.github/ISSUE_TEMPLATE/feature.yml`, include epic parent, phase label, priorità, RED → GREEN → REFACTOR, evidenza commit SHA e URL di Actions. Le 14 epic e 116 issue child non indicano funzionalità completate. Le milestone sono label logiche; GitHub Projects/Milestones non configurati. Prima del lavoro su dati veri completare #29, #30, #31, #32, #33, #34, #36, #39, #41, #43 e test #17.

## New consent guard (#29 slice)

All new memory writes MUST pass an active source-scoped `memory.capture` grant. Never bypass `IConsentGuard` or the transactional PostgreSQL check inside `PostgresMemoryRepository.UpsertAsync`. Never initiate external embeddings before consent + domain payload validation. Tests must include revoked/expired grants and cross-tenant denial. The consent endpoint only records first-party acknowledgement; it does **not** legalize third-party recordings or replace GDPR review.

## Rate limit invariants (#37 slice)

Keep `app.UseRateLimiter()` **after** authentication so the key derives from a signed identity; apply the per-user policy to all new `/api/v1` routes. Never use user-supplied headers as quota identity in production. Anonymous fallback uses peer IP only. The in-memory fixed window is **not** a distributed SaaS budget; do not claim full anti-abuse protections without WAF, media quotas and multi-replica tests.

## OIDC Web authorization source of truth

`src/mobile/src/services/webAuth.ts` is only for browser OAuth and must use a maintained OIDC library, authorization code + PKCE S256, session state/nonce checks, ephemeral in-memory tokens, correct redirect /Omnia/, and strict public HTTPS/loopback config. Do not add localStorage/AsyncStorage for tokens, don't store password/client_secret or trust client-supplied tenant IDs. Web captures must be API-confirmed, error states explicit, and manual scoped consent obtained first. Native OIDC requires separate Expo AuthSession + SecureStore with device smoke before claiming parity.

## Mandatory PostgreSQL RLS invariants (#30)

For every query accessing `memory_entries` or `consent_grants`, use `PostgresActorScope.OpenAsync` and execute all SQL inside that same transaction. Source tenant/subject ONLY from a validated OIDC JWT; no client JSON/header/GUC override. Use PostgreSQL runtime role that is NOT schema owner, SUPERUSER, BYPASSRLS or owner-member, and keep distinct privileged migration credentials. The app must fail startup if RLS isn't FORCE enabled. Add real PostgreSQL negative tests for unscoped/cross-owner SQL and pooled connection reuse. Never weaken `PostgresRuntimeGuard` or bypass RLS for tests; migration/admin datasource is *only* for migrations and fixture setup. Keep application SQL parameterized (custom GUCs alone do not prevent arbitrary SQL injection).

## Personal data export / erasure boundaries (#31 slice)

All new storage for personal content or derived embeddings MUST be registered in a complete export+erasure inventory, with contract tests, before the feature may be claimed end-to-end. Only the two PostgreSQL primary tables are handled by `IPersonalDataService` today; never assert backup, device, provider, Keycloak or external cache deletion. `PostgresActorLock` MUST be acquired before any capture/grant/revoke or export/erasure, inside `PostgresActorScope`, to serialize requests and protect against post-erasure writes. Only JWT-validated tenant+subject are allowed; no selector in user input. Keep export no-store, streaming, complete (no silent caps) and private. Deletion requires explicit confirmation and should return an accurate scoped receipt. Preserve RLS and real-Postgres/Keycloak E2E coverage.

## Mandatory retention invariants (#136/#32)

All newly persisted memory **must** receive a server-generated expiry based on initial `received_at`; no client control, no automatic extension from idempotent retry. Do not expose expired notes through normal memory browsing. Extend retention only through a new user-authorized capture (never an implicit retry). All new personal-data stores and derivatives require their own documented purge and RLS/security tests. The only global purge function is `public.omnia_purge_expired_memories(integer)` with a bounded, hardcoded body, SECURITY DEFINER, default EXECUTE revoked and granted only to `omnia_retention`; do not put it in the HTTP API role. Use separate random secrets/identities for migration, API and worker. Keep the parent #32 open until source-specific/legal retention, backup restore/tombstones, audit and provider propagation exist. Never claim that 30 days is required by GDPR.
