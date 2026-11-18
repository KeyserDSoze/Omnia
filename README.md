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

## Browser OAuth OIDC/PKCE + API reali (#39, #41 — web slice)

Se configuri **tutte e tre** le variabili pubbliche Expo, la UI Web attiva il login OIDC Authorization Code + PKCE S256, il fetch dei consensi e delle note dal server, e il salvataggio reale su PostgreSQL. Non può inviare note in assenza di login e consenso `memory.capture`. La UI conserva i token di accesso/refresh **solo in memoria**, usa `sessionStorage` esclusivamente per lo stato temporaneo del redirect e rimuove `code` dall'indirizzo dopo il callback. Una ricarica completa richiede un nuovo login; il refresh in sessione prova a usare i token di rinnovo e al fallimento richiede login. Si può revocare il consenso manuale (le note vecchie NON vengono cancellate).

Per l'ambiente Docker locale già predisposto:
```sh
# 1. Configura docker/.env con credenziali locali casuali, poi avvia Keycloak/PostgreSQL/API
docker compose --env-file docker/.env -f docker/compose.yml up --build -d db keycloak api
# 2. Da un'altra shell avvia il web server Expo con sole impostazioni PUBBLICHE (non secrets)
cd src/mobile
EXPO_PUBLIC_OMNIA_OIDC_ISSUER=http://localhost:8081/realms/omnia \
EXPO_PUBLIC_OMNIA_OIDC_CLIENT_ID=omnia-web \
EXPO_PUBLIC_OMNIA_API_URL=http://localhost:8080 \
npm run web
```
Keycloak non crea credenziali utente reali per default: creare un utente sintetico nel realm locale e testare l'OIDC flow; non commitare password. Redirect consentiti nel realm import: `http://localhost:19006/*` e `http://127.0.0.1:19006/*`. Client OAuth **pubblico**, senza secret. L'API valida firma, issuer, audience e sub/tenant del bearer JWT. Configurare separatamente HTTPS, client, origins e issuer reali per una futura deployment Azure/Pages (non incluso). Le variabili `EXPO_PUBLIC_*` sono visibili nel browser: **NON inserire segreti**.

La Pages pubblica su <https://keyserdsoze.github.io/Omnia/> mantiene la demo locale quando non riceve configurazione cloud. La presenza di una sola variabile viene trattata come errore di configurazione, mai come sync implicito. Native iOS/Android resta demo fino all'OIDC via browser nativo con SecureStore (#40). Audio resta solo locale, non viene caricato dal flusso memo.

## PostgreSQL RLS e connessione non privilegiata (feature #30)

La memoria e i consensi ora hanno **Row-Level Security applicata dal database**. L'API usa un ruolo `omnia_app` non superuser, non owner e privo di `BYPASSRLS`; un account distinto di migrazione installa le tabelle e concede all'app solo SELECT/INSERT/UPDATE/DELETE sui dati personali. Il backend **rifiuta l'avvio** se manca uno dei due ruoli, se i ruoli coincidono, se l'utente applicativo può bypassare RLS o se le policy non sono `FORCE`. I repository impostano la coppia `tenant_id`+`owner_sub` verificata dal JWT esclusivamente tramite `set_config(..., is_local=true)` nella stessa transazione SQL del lavoro; le connessioni riutilizzate non ereditano l'identità precedente.

Per Docker locale creare due password differenti in `docker/.env` (variabili `POSTGRES_PASSWORD`, `APP_DB_PASSWORD`), poi `docker compose --env-file docker/.env -f docker/compose.yml up --build -d`. Il servizio one-shot `db-bootstrap` crea o aggiorna il ruolo runtime: serve anche per volumi già esistenti. L'API riceve `Database__ConnectionString` (solo `omnia_app`) e `Database__MigrationConnectionString` (solo owner schema). **In Azure occorre provisionare questi due account/identità separati con permessi minimi e secret vault**: la configurazione Docker è solo uno sviluppo locale.

I test con pgvector vero verificano SQL non filtrato, blocco INSERT/UPDATE cross-owner, revoca e assenza di leak del context tra connessioni. La UI su GitHub Pages resta demo, l'accesso OIDC Web funziona solo con API+Keycloak reali configurati. Questa difesa non neutralizza la SQL injection se l'attaccante controlla istruzioni SQL arbitrarie e cambia i parametri custom `omnia.*`; servono query parametrizzate, permessi minimi, audit e threat review.

## Esportazione e cancellazione dati (prima slice #31)

Quando Expo Web è collegata a Keycloak e API reali, la sezione **I tuoi dati** permette di scaricare un JSON con tutte le note e cronologie di consenso presenti nel PostgreSQL principale, comprese versioni raw/normalizzate e embedding derivati. Il download avviene dal browser soltanto dopo richiesta esplicita; i dati non finiscono in GitHub Pages o localStorage.

La cancellazione richiede una spunta e la digitazione esatta di `ELIMINA`; il server richiede inoltre il valore `ERASE_MY_DATA`. La transazione PostgreSQL rimuove consensi e note incluse eventuali embedding, e permette di ripetere l'operazione senza errore. L'identità dell'account e la RLS provengono sempre dal JWT verificato. I nuovi grant/contenuti sono serializzati con la cancellazione tramite advisory lock per attore.

**Perimetro reale:** questa funzione agisce soltanto su `memory_entries` e `consent_grants` del database PostgreSQL primario. Non elimina backup, file audio ancora sul dispositivo, dati presso Keycloak, log, copie esterne o richieste ai provider AI già inviate. La cancellazione del database non significa che sia soddisfatto l'intero diritto all'oblio; la feature completa #31 resta aperta, come #32 retention e gli altri gate privacy. Dettagli: [docs/privacy-data-rights.md](docs/privacy-data-rights.md).
