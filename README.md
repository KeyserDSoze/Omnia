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

## Conservazione automatica della memoria PostgreSQL — slice #136 / #32

Le note nella tabella `memory_entries` scadono **30 giorni dopo `received_at`**, l'istante server di prima ricezione; non dalla data dell'evento né dal rinnovo di un consenso. L'idempotent retry di una nota non allunga il periodo. Le note già scadute non appaiono nella timeline. Un worker ricorrente elimina fisicamente dal database primario i record scaduti, **inclusi testo raw, dati normalizzati e vettori**; esegue una prima scansione all'avvio dell'API e poi ogni 15 minuti (max 5.000 record per ciclo, con batch 500).

La configurazione locale ora richiede **tre segreti PostgreSQL differenti** in `docker/.env`: `POSTGRES_PASSWORD` per le migrazioni, `APP_DB_PASSWORD` per l'API con RLS e `RETENTION_DB_PASSWORD` per `omnia_retention`. Il ruolo di pulizia non legge direttamente tabelle personali e può chiamare soltanto una funzione SQL delimitata e testata. Il bootstrap Docker gestisce volumi esistenti.

**Attenzione:** i 30 giorni sono una policy tecnica iniziale e **non** una durata GDPR universalmente valida. I consensi/audit non sono soggetti a questa eliminazione finché non è definita una policy specifica. Backup/WAL, provider AI, dispositivi, object store e connettori sono fuori scope. Il parent #32 rimane aperto fino alla configurabilità per finalità/categoria/tenant, alla propagazione e alla revisione legale. Vedi [docs/retention.md](docs/retention.md).

## Cifratura applicativa — prima base #33 (sotto-issue #137)

`Omnia.Application.IEnvelopeEncryption` e `Omnia.Infrastructure.AesGcmEnvelopeEncryption` offrono un **codec crittografico riutilizzabile**, con AES-256-GCM, chiave dati casuale per oggetto, protezione della chiave dati tramite KEK versionata, contesto autenticato `tenantId / ownerSubject / purpose / recordId` e `Rewrap` per aggiornare la KEK senza perdere i contenuti. Le chiavi possono essere caricate **soltanto da un file JSON esterno con percorso assoluto** (`EnvelopeKeyRing.FromFile`) o direttamente da un provider segreto fidato; non vengono fornite chiavi di default. Dettagli in [docs/envelope-encryption.md](docs/envelope-encryption.md).

**Limite importante:** il codec NON è ancora collegato alle tabelle PostgreSQL né all'audio, ai token OAuth, ai provider AI o a un Key Vault/OpenBao. Le colonne attuali `raw_content`, `normalized_json`, `summary` e `embedding` **continuano a essere memorizzate come prima**. Non dichiarare che i dati correnti siano cifrati a livello applicativo. La parent #33 resta aperta per migrazione/backfill, applicazione a tutte le classi di dati, vault e verifiche di recupero/disaster recovery.


## Note nuove cifrate in PostgreSQL — slice #139

Dal deploy della migrazione `0005_memory_sealed_payload.sql`, le nuove note inviate alle API conservano `rawContent`, JSON normalizzato, riepilogo e tag **solo** come `sealed_payload bytea`, con `IEnvelopeEncryption` AES-256-GCM e associated data vincolata a tenant, utente e UUID canonico. Le colonne storiche restano redatte per le nuove note; un trigger vieta nuove INSERT in chiaro e downgrade degli encrypted rows. Timeline e export decifrano soltanto sotto la RLS dell'utente. Le note storiche precedenti alla migrazione rimangono in chiaro finché non è implementato un backfill protetto: l'export le indica come `legacy-plaintext`.

**Configurazione obbligatoria:** `Crypto:KeyRingPath` con file esterno, senza chiavi nel repository, browser o log. In Docker locale creare `docker/secrets/envelope.keyring.json`, proprietà dell'UID della API container e permessi 0400/0440 (la API è non-root). Generare una KEK casuale di 32 byte; non usare l'esempio testuale come chiave. Per esempio: `openssl rand -base64 32` e formato JSON della [guida](docs/envelope-encryption.md). Il file `docker/secrets/` è ignorato da Git. Senza chiave valida l'API rifiuta lo startup; senza nuove migrazioni l'API rifiuta le vecchie scritture.

**Non ancora protetti da questa slice:** note legacy, ID di origine e metadati, embeddings pgvector, storico consensi, backup/WAL, servizi esterni e audio. La feature padre #138/#33 resta aperta e l'ambiente **non è pronto per dati reali**. Dettagli: [docs/sealed-memory.md](docs/sealed-memory.md).

## API JSON intake concurrency — P0 hardening slice #159

Beyond 413 body bytes, 408 total receive deadline and 415 media
checks, the API has a per-process **concurrent JSON receive** guard:
RequestBody:MaxConcurrentReads defaults to **32**, configured
1..256; invalid values refuse API startup. When all slots are in
use, new supported JSON POST/PUT/PATCH /api/v1 requests get
**503 request_body_busy** (Retry-After: 1; no-store) without
reading the payload. Slots are released after buffering, before
DB/AI work. A 503 is a truthful temporary refusal, not a completed
write or consent grant. This is NOT cluster-wide admission, a WAF,
a limit on Kestrel connections or on downstream handler memory;
#37 remains open, and the API is not ready for real personal data.

## Kestrel transport header safeguards (#160)

The .NET 10 API now sets server-level limits *before* middleware:
Transport:RequestHeadersTimeoutMilliseconds 10000 (valid
1000..30000), Transport:MaxRequestHeadersTotalBytes 16384 (valid
8192..32768), and Transport:MaxRequestHeaderCount 64 (valid
16..100). Kestrel enforces a 70KiB maximum request body and an
explicit 240 B/s minimum body receive rate (5s grace) even before
the authenticated JSON middleware. Incorrect config refuses startup;
a loopback TCP test exercises incomplete headers and ordinary GET.
These are direct Kestrel protections, not full DoS prevention or
security guarantees for reverse proxies, Azure staging, TLS,
HTTP/3 or distributed SaaS quotas. Parent #37 stays open.

## JSON receive fairness per authenticated actor (#161)

The API's 32 default concurrent *JSON receive* slots are now shared
with an additional **per-OIDC tenant+subject** cap: optional
RequestBody:MaxConcurrentReadsPerActor, default **4** (or the global
limit if smaller), valid 1..MaxConcurrentReads. Exhausted actor or
process slots return the same 503 request_body_busy/no-store response
without reading JSON. The limiter uses verified bearer claims, never
X-Forwarded-For; it removes idle actor keys and releases slots before
normal handler work. This is only per-process/actor protection, not
a global tenant budget, distributed quota, ingress WAF or production
readiness. Parent #37 remains open.

## Per-tenant JSON intake resource cap (#162 / ADR 0061)

The .NET 10 API now applies **three** process-local, nonqueueing
concurrent *JSON body receiving* limits: global default 32,
tenant default 16 (or lower global), and actor default 4 (or
lower global). Configure optional
`RequestBody:MaxConcurrentReadsPerTenant` 1..MaxConcurrentReads;
unsafe settings reject startup. Tenant identity is derived only
from OIDC-validated tenant_id claims, not X-Forwarded-For or
client tenancy headers. Saturation responds 503/no-store before
reading bytes and slots return before endpoint work. Idle
tenant/actor keys are immediately discarded. This is NOT a
cross-node budget, capacity reservation for every tenant, WAF,
binary media limit or production/GDPR security release; #37 open.

## Bounded live JSON buffers across handler processing (#163)

The API additionally caps the number of middleware-owned buffered
JSON requests still in progress, even AFTER read permits have
released. RequestBody:MaxConcurrentBufferedRequests defaults to 64,
valid 1..256. It returns generic 503 request_buffer_busy, no-store
and Retry-After:1 before reading any rejected body, without a queue.
The slot is retained until the bounded JSON MemoryStream and handler
are complete. The 32/16/4 process/tenant/actor receive quotas
still release before endpoint work. This is not a full memory ceiling,
a DB/AI limit, a distributed/tenant-fair quota or GDPR release.

## Per-tenant retained JSON request cap (#164 / ADR 0063)

The API now prevents one OIDC tenant from occupying every
middleware-owned JSON buffer during slow downstream handlers:
defaults **64 globally / 16 per tenant**, with an optional
RequestBody:MaxConcurrentBufferedRequestsPerTenant setting
(1..global, auto-clamped default). Both active counters are
atomic, 503 request_buffer_busy before reading or entering a
handler when full, no queue and automatic eviction of inactive
tenant entries. Existing 32/16/4 JSON READ budgets release
before handler and are unaffected. Does not reserve guaranteed
slots if global capacity is full, span replicas or limit total
process memory, DB/AI, media connectors or upstream WAF.
#37 remains OPEN.

## Per-actor bounded JSON buffer lifetime (#165 / ADR 0064)

Omnia now applies atomic, per-API-instance live buffered
JSON request ceilings to **global / OIDC tenant / OIDC
tenant+subject actor**, defaults **64 / 16 / 4**, while
the downstream handler holds its bounded JSON body stream.
Optional RequestBody:MaxConcurrentBufferedRequestsPerActor
may be set from 1 through the effective tenant cap;
unset defaults clamp safely. Exhausted buffer capacity
returns 503 request_buffer_busy/no-store/Retry-After:1
before reading any bytes, without queuing. Inactive quota
identities are evicted; their values are not logged or
persisted. Independent concurrent READ limits 32/16/4
remain released BEFORE handlers. This is per-process
resource safety, NOT assured tenant reservations,
distributed capacity, whole-memory/DB/AI control,
media/connector quotas, WAF or GDPR release.

## Tenant-aggregate authenticated API rate (#166 / ADR 0065)
In addition to per-account 120 requests/minute, Omnia
limits all accounts of one OIDC tenant collectively to
RateLimit:TenantRequestsPerMinute (default **600** per
fixed 60-second window; valid 1..10000) per API process.
On saturation authenticated /api/v1 returns HTTP
429 rate_limited/no-store/Retry-After before body read
or handler. RateLimit:MaxTrackedTenants defaults 4096
(valid 1..16384); when all partitions are full, new
tenant identities fail closed until expired windows
are reclaimed on request traffic. Authentication,
health, existing JSON limits and tenant isolation are
unchanged. This is NOT a distributed multi-instance
quota, WAF, guaranteed fairness or GDPR/production go-live.
