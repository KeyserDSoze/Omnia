# Omnia — product & engineering roadmap

## v0.1 — Foundation (code in main, validation pending)

- [x] Monorepo conventions `src/`, `tests/`, `docs/`, `adr/`, `docker/`, `infra/`, `.github/`.
- [x] Expo React Native cross-platform shell; light/dark, quick text notes held in session, local microphone capture.
- [x] ASP.NET Core .NET 10 Domain/Application/Infrastructure/API with fail-closed OIDC bearer and tenant-scoped PostgreSQL API.
- [x] Canonical text ingestion, provenance keys, initial summary/tags, pgvector column and optional Ollama/Azure OpenAI embedding providers.
- [x] PostgreSQL versioned SQL migration, idempotent upsert; xUnit, Vitest, Playwright test sources, GitHub CI, web export and Pages workflow.
- [ ] Verify CI green (must be checked on Actions; no claim before runs).
- [ ] Configure GitHub Pages → Actions and verify URL live; native build credentials not yet configured.
- [ ] Commit package-lock.json and NuGet packages.lock.json after reproducible install/restore, then switch CI to frozen restores.

## v0.2 — Functional secure personal timeline (next)

- [ ] Expo Web/Android/iOS OIDC PKCE auth & token refresh using platform-safe storage and logout.
- [ ] UI actual GET/POST API integration, online/offline sync and clear error / retry UX.
- [ ] Persist local audio safely with encryption and consent metadata; upload with authenticated streaming, quotas, object storage MinIO/Azure Blob and transcription pipeline.
- [ ] Port IObjectStore/MinIO/Azure, outbox, RabbitMQ/Service Bus, background workers with retry + dead letter; DI parity tests.
- [ ] PostgreSQL RLS defence-in-depth, schema evolutions, retention, export, user delete and audit log.
- [ ] Integration E2E browser OIDC → API → PostgreSQL and separate mobile device smoke with native simulator.

## v0.3 — Omnia Memory Graph

- [ ] Connectors Microsoft Graph Outlook/Calendar, CRM adapter and consent registry with scoped OAuth.
- [ ] MCP client and server gateway, capability sandbox, network egress policy and provenance.
- [ ] Ingestion of JSON, XML, text, audio (binary in object store), documents with schema versioning and event dedupe.
- [ ] Embedding retry/indexing, pgvector semantic search, temporal/thread stitching, evidence citations, taxonomy editable by users.
- [ ] Daily recap with traceable origin links; AI insights as opt-in, non-diagnostic suggestions.

## v0.4 — Delegated personal agency

- [ ] Per-channel draft assistant with user-approved tone settings; send requires specific consent and preview.
- [ ] Scoped automation policies, permission revocation, audit, escalation and limits.
- [ ] Human feedback training, observation/correction workflow and prompt-injection controls.
- [ ] SaaS onboarding, tenant quotas, subscription billing, privacy/security governance.

## Release gates

Azure deployment / App Store approval / real personal-data launch require threat model, privacy review (GDPR, recording laws), OIDC and tenant e2e gates, encrypted backup, deletion/export tests, PaaS networking and secret management, and real CI evidence. *No call interception promised.*

## Piano feature tracciabile (2026-11-17)

La pianificazione corrente è [docs/backlog.md](backlog.md): M0 prerequisiti privacy/TDD; M1 MVP secure timeline; M2 media + schema canonico/vector + Azure staging; M3 connectors/MCP + AI grounded; M4 agenti e SaaS; M5 enterprise/residency. 14 epics e 116 child aperte **non** sono feature implementate. La foundation web già rilasciata e la CI iniziale verde non dimostrano auth end-to-end, Azure production o native signed release.

## Avanzamento tecnico #29 (2026-11-17)

- [x] Vertical slice server di **scoped memory.capture consent**: entità Domain, use case/port Application, migrazione PostgreSQL/adapter e API JWT, revoca, gate prima dell'AI e gate transazionale durante l'UPSERT.
- [x] Aggiunti test xUnit Domain e integrazione PostgreSQL per approvazione, rifiuto, revoca, isolamento, scadenza e idempotenza (verifica reale tramite CI da allegare).
- [ ] UI per consenso informato e OAuth PKCE (#39), scopes per media/connector/azioni, audit append-only, cancellazione/export (#31), retention (#32), RLS (#30). Issue epic #2 / feature #29 resta **aperta** fino al completamento dei criteri.

## Avanzamento #37 (2026-11-17)

- [x] Prima slice: limiter HTTP per tenant/sub derivati dal bearer JWT autenticato, 429 senza queue e Retry-After; fallback peer-IP per anonimi.
- [x] API integration test TestServer con identità sintetiche per lo stato 429/isolamento, reale PostgreSQL nel test host (vedere CI run).
- [ ] Limiti media / connector, distributed cluster-wide limiter, WAF/rate at edge, load test/metrics; feature #37 rimane **aperta**.

## v0.2 — First functional Web slice (#39, #41)

- [x] Expo Web OIDC Authorization Code + PKCE S256 con `oidc-client-ts`, token in sola memoria e stato redirect in sessionStorage; login, logout con revoca best-effort e refresh token durante sessione.
- [x] Web UI note manuali ↔ API JWT ↔ PostgreSQL: consenso per scope manual, error handling, reload timeline e idempotent retry key, nessuna falsa conferma.
- [x] Fallback Pages senza configurazione che dichiara apertamente demo locale; configurazione incompleta rifiutata.
- [ ] Real-world end-to-end provider OIDC→API→DB (test in `e2e/auth-sync.spec.ts` e workflow da verificare).
- [ ] Android/iOS AuthSession PKCE + secure storage (#40); offline sync persistente/encrypted (#117), export/delete, RLS, legal consent review.
