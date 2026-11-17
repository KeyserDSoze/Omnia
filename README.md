# Omnia · Personal Intelligence Platform

> **Foundation v0.1** — un unico monorepo per web, Android, iOS e API SaaS. Lo sviluppo avviene su `main` come richiesto. La foundation non è ancora un prodotto pronto a trattare dati personali reali.

## Visione

Una memoria personale verificabile e controllata dall'utente: fonti multiple (app, dispositivi, registrazioni consensuali, MCP), normalizzazione, provenance, tagging, embedding, ricerca, thread e agenti capaci di preparare azioni sotto controllo umano.

## Implementato nello scaffold

- Expo SDK 57 / React Native / web statico, tema chiaro/scuro; dashboard, note nella sessione e registrazione **del microfono locale** con permesso, senza upload.
- ASP.NET Core **.NET 10** con Domain/Application/Infrastructure/API, autenticazione JWT OIDC obbligatoria, `sub` e `tenant_id` richiesti, CORS allowlist.
- PostgreSQL 17 + pgvector (768 dimensioni): raw originale, JSON normalizzato, summary, tags, fonte, externalId, owner/tenant, embedding nullable, migrazione SQL transazionale e upsert idempotente.
- Adapter DI per embedding: `none` (dati segnati senza embedding), `ollama` (locale), `azure` (Azure OpenAI).
- Docker Compose: pgvector, Keycloak **solo development**, Ollama e API; xUnit unit/integration, Vitest e Playwright, pipeline GitHub Actions.
- GitHub Pages: workflow di export statico; l'API vive **separatamente** su Azure Container Apps e non può essere ospitata da Pages.

**Non implementato:** login mobile/web completo, syncing tra dashboard e API, cronologia persistente delle registrazioni, registrazione delle chiamate telefoniche, speech-to-text, integrazioni CRM/Outlook/WhatsApp/MCP, orchestrazione agenti, insight AI, billing, gestione utenti SaaS, cancellazione/export dati, provisioning Azure verificato. Gli adapter AI funzionano lato API ma l'uso con dati veri richiede security review.

## Repository

```text
.github/workflows/             CI, Pages, native builds
adr/                           Architectural Decision Records
docs/                          architecture, roadmap, security, research
docker/                        compose, Keycloak realm, API Dockerfile
infra/azure/                   PaaS foundation (Bicep; not yet validated)
src/backend/Omnia.Domain/      aggregates & invariants
src/backend/Omnia.Application/ use cases + ports
src/backend/Omnia.Infrastructure/ PostgreSQL + embeddings adapters
src/backend/Omnia.Api/         HTTP, OIDC, DI
src/mobile/src/app/            Expo Router, web/iOS/Android UI
src/mobile/src/domain/         testable pure logic
tests/                         xUnit / Vitest / Playwright
```

## Avvio web (senza backend)

```sh
cd src/mobile
npm install
npm run web
npm run typecheck
npm test
npm run build:web
npm run test:e2e
```

## Avvio API locale (servizi open source)

```sh
cp docker/.env.example docker/.env
# Generare password locali forti e sostituire i placeholder.
docker compose --env-file docker/.env -f docker/compose.yml up --build
curl http://localhost:8080/health
# In modalità embeddings ollama: docker compose --env-file docker/.env -f docker/compose.yml exec ollama ollama pull nomic-embed-text
```

Keycloak locale è disponibile su localhost:8081, realm `omnia`; creare un utente e configurare i client via console Keycloak. I token API devono includere `sub`, `tenant_id` e `aud=omnia-api`. **Nessun token di test o account demo è hardcoded.** Test integration richiede un Postgres pgvector usa-e-getta e la variabile `OMNIA_TEST_DB`.

## Rilascio

- **Web**: Settings → Pages → Source: GitHub Actions; workflow `.github/workflows/pages.yml` pubblica `src/mobile/dist` quando Pages è abilitato. URL atteso dopo deploy riuscito: https://keyserdsoze.github.io/Omnia/
- **Android/iOS**: `eas.json` prepara i profili, ma firma, account store e token Expo sono da configurare.
- **Azure**: vedere `infra/azure/README.md`; non dichiarare deploy o hosting attivi senza verifiche.

## Documentazione

[Architettura](docs/architecture.md) · [Roadmap](docs/roadmap.md) · [Sicurezza](docs/security.md) · [Concorrenza](docs/competitors.md) · [Decisioni](adr/)

## Feature backlog e disciplina di sviluppo

[Feature backlog (14 epics / 116 child)](docs/backlog.md) · [PRD](docs/product-requirements.md) · [Strategia TDD](docs/testing-strategy.md) · [Platform capability boundaries](docs/platform-capabilities.md).

Tutti i nuovi requisiti vanno tracciati come issue `feature` con fase e acceptance; test-first è obbligatorio. La foundation web/API è uno scaffold, **non** un SaaS che tratta dati reali. Nessuna connessione dati/automation operativa senza auth, consenso, retention e security release gate.

## Consent ledger v0.2 (feature #29, slice in corso)

Le API ora richiedono un consenso utente **attivo e limitato per sorgente** per ogni nuovo salvataggio in `/api/v1/memories`. La registrazione è per tenant+subject dal JWT OIDC, sorgente e finalità `memory.capture`, con scadenza massima 90 giorni, `policyVersion`, timestamp e revoca; PostgreSQL verifica nuovamente il grant nella transazione d'ingestione. Senza consenso HTTP 403 `consent_required`, e non vengono invocati provider AI. `GET /api/v1/consents`, `POST /api/v1/consents`, `DELETE /api/v1/consents/{id}` sono protetti da autenticazione. Vedere [contratto e limiti](docs/consent-api.md).

**Importante:** grant per l'account ≠ consenso di partecipanti terzi o base giuridica GDPR. La revoca interrompe ingest futuri ma non cancella dati già raccolti (#31). Il login UI/PKCE e un pannello per il consenso sono ancora da implementare: **non usare dati personali reali**.

## API throttling v0.2 (#37, first slice)

I percorsi `/api/v1/memories` e `/api/v1/consents` ora usano il limiter ASP.NET Core (finestra fissa di un minuto, **120 richieste per identità autenticata** di default, `429 rate_limited`, `Retry-After: 60` e nessuna coda). La chiave di quota deriva soltanto da `tenant_id` + `sub` nel JWT validato; richieste non autenticate usano il peer IP, non header controllati dal client. `/health` rimane non limitato per i probe. Il limite può essere ridotto in test con `RateLimit__RequestsPerMinute`; valori fuori 1–1000 rifiutano l'avvio. Test HTTP reali (TestServer con scheme di identità **solo test**) verificano il 429 e l'isolamento dei tenant. Non è ancora un sistema completo di quote SaaS, limiti sui media o difesa DDoS.
