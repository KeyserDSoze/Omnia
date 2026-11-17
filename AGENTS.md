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
