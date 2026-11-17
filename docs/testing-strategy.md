# Omnia — Test strategy e Evidence Gates (TDD)

## Regola di lavoro

Per ogni feature: (1) test che fallisce per il comportamento richiesto; (2) implementazione minima; (3) refactor e regressioni; (4) esecuzione reale con SHA/URL Actions. Mai chiudere una issue solo perché il test è stato scritto o un mock passa. Ogni boundary esterno ha **test di contratto** oltre ai fake usati nello unit layer.

## Pyramid

| Layer | Test runner | Esempio | Frequenza |
|---|---|---|---|
| Domain/application | xUnit + Vitest | normalizzazione, idempotenza, tag, session state, policy decision | ogni commit |
| API + PostgreSQL | xUnit integration/Postgres pgvector | migrations, JWT 401/403, RLS, write/read, delete | ogni commit |
| Provider contracts | xUnit + disposable Docker dependencies | MinIO/Azure IObjectStore, RabbitMQ/Service Bus IEventBus, Ollama/Azure embeddings | ogni commit OSS; Azure staging opt-in |
| True Browser E2E | Playwright Chromium Web under `/Omnia/` | login PKCE reale, React→HTTP→PostgreSQL, theme, capture, consent revoke | ogni commit core |
| Native E2E | Android emulator/iOS simulator + device test | secure token, microphone permission, offline capture, deep link | prerelease e PR mobile |
| AI eval | synthetic golden corpus/red-team | grounded recap, hallucinations, model/summary provenance, prompt injection | cambi modello/feature AI |
| Nonfunctional | k6/chaos/SCA/secret scanning | quotas, restore, massive imports, kill switch, security | staging + release |

## Component truthfulness

- Fake/mocks testano algoritmi e fallimenti, **non** attestano disponibilità cloud o provider OAuth.
- `IEmbeddingProvider` `none` deve dare *pending/unembedded*, mai un vettore inventato; per embeddings testare l'insert PostgreSQL con vettore non nullo e modello/dimension corretto.
- Test del mondo reale devono usare access token emessi da un provider di prova, un database effettivo e l'app web compilata; il test di pagina statica è un controllo diverso.
- Per microfono/chiamate: permission/state e fallback reali, non garantire intercettazione audio non disponibile.
- Per MCP: simulate server malicious, tool description injection, SSRF, token audience, scope escalation, data poisoning, wrong tenants.
- Fixtures solo sintetiche, password generate per CI, clean-up idempotente, segreti mascherati, test reports associati a commit SHA.

## Required gates per stage

- **M0:** .NET/Expo locked dependencies, secret/SCA scans, compose config, smoke pgvector, xUnit, Vitest, Playwright static.
- **M1:** real OIDC/PCKE browser→API→PostgreSQL, cross-tenant denied, CORS/CSP, consent fail closed, logout + token revoke, data export/delete.
- **M2:** media upload with object store, STT contract, timestamps and provenance, async retry/DLQ, DB RLS, pgvector search, Azure staging IaC review.
- **M3:** connector OAuth and webhook contract, MCP sandbox red-team, AI quality eval and native builds tested on emulator/simulator.
- **M4:** read-only default actions, user approval gated, replay protection, billing sandbox/idempotency, quota and invoice handling.

## CI evidence policy

Each issue links a commit with passing required jobs and relevant logs. Mark as 'implementation in progress' rather than 'done' if any gate is untested due to missing real cloud credentials, device or provider API. Smoke/preview environments may use temporary accounts but never personal data. 
