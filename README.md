# Tao · Personal Intelligence Platform

> **Foundation v0.1** — un unico monorepo per web, Android, iOS e API SaaS. Lo sviluppo avviene su `main` come richiesto. La foundation non è ancora un prodotto pronto a trattare dati personali reali.

## Visione

Una memoria personale verificabile e controllata dall'utente: fonti multiple (app, dispositivi, registrazioni consensuali, MCP), normalizzazione, provenance, tagging, embedding, ricerca, thread e agenti capaci di preparare azioni sotto controllo umano.

## Implementato nello scaffold

- Expo SDK 58 / React Native / web statico, tema chiaro/scuro; dashboard, note nella sessione e registrazione **del microfono locale** con permesso, senza upload.
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
src/backend/Tao.Domain/      aggregates & invariants
src/backend/Tao.Application/ use cases + ports
src/backend/Tao.Infrastructure/ PostgreSQL + embeddings adapters
src/backend/Tao.Api/         HTTP, OIDC, DI
src/mobile/src/app/            Expo Router, web/iOS/Android UI
src/mobile/src/domain/         testable pure logic
tests/                         xUnit / Vitest / Playwright
```

## Avvio web (senza backend)

```sh
cd src/mobile
npm ci
npm run web
npm run typecheck
npm test
npm run build:web
npm run test:e2e
```

Il progetto Web usa `src/mobile/package-lock.json` verificato da
`node src/mobile/scripts/verify-mobile-lockfile.mjs`; CI, Pages, OIDC
e il workflow native manuale richiedono `npm ci`. Per modificare
dipendenze, aggiornare intenzionalmente il lockfile, revisionare il diff
di transitive e ripetere una installazione pulita. Versioni opzionali
native compatibili con Expo SDK 58 sono fissate nel manifest; vedi
[ADR 0085](adr/0085-locked-expo-npm-dependency-graph.md) e
[runbook](docs/npm-dependency-lock.md). La CI verde non valida una build
native firmata.

## Riproducibilità NuGet .NET 10

Ogni progetto .NET ha un proprio `packages.lock.json` verificato dal
preflight `python3 tests/quality/verify_nuget_locks.py`; GitHub CI,
OIDC E2E e la build Docker API eseguono `dotnet restore --locked-mode`.
Il file root `Directory.Build.props` blocca anche gli eventuali restore
impliciti quando `CI=true`. Per un upgrade intenzionale di dipendenze
vedere [ADR 0086](adr/0086-dotnet-nuget-locked-restore.md) e
[runbook](docs/nuget-dependency-lock.md). I lockfile non dimostrano
la riproducibilità binaria delle immagini o release .NET/Azure e non
sostituiscono SBOM/SCA.

## SDK .NET e runtime Docker (pinning v0.1)

La toolchain .NET di CI/OIDC è definita da `global.json`:
**SDK 10.0.404 esatto**, senza roll-forward o prerelease.
Il Dockerfile API usa immagini Microsoft Ubuntu Noble con versioni
esplicite (`sdk:10.0.404-noble`, `aspnet:10.0.15-noble`) e ne
controlla SDK/runtime realmente eseguiti. Le modifiche sono
coperte da [ADR 0087](adr/0087-dotnet-exact-sdk-and-container-patch.md)
e [runbook](docs/dotnet-toolchain-pin.md); la CI non accetta
tag floating `10.0` o `dotnet-version: 10.0.x`.
I tag patch delle immagini **non sono digest immutabili** e non
sostituiscono SBOM, SCA o revisione CVE: #15/#10 restano aperte.

## Immagini .NET API multiarch bloccate tramite digest OCI

Per la build Docker, i due `FROM` Microsoft mantengono il tag di versione
SDK/runtime e aggiungono `@sha256` del relativo **indice di manifest**
multiarchitettura, verificato sul registro MCR per linux/amd64 e
linux/arm64. La CI usa il [validator](tests/quality/verify_dotnet_image_pins.py)
offline e quello remoto prima del restore: controlla che il tag ufficiale
non sia stato ricostruito con un digest nuovo senza revisione. L'immagine
viene poi realmente compilata su amd64; **non è ancora eseguita una build
arm64 reale**. Istruzioni per aggiornamenti di sicurezza nel
[runbook](docs/dotnet-oci-index-pinning.md) e
[ADR 0088](adr/0088-dotnet-multiarch-oci-image-index-pinning.md).
Il digest è un vincolo di contenuto, non una firma del produttore né
uno scan malware/CVE o una certificazione supply chain.

## SBOM da lockfile — CycloneDX 1.6

Ogni CI produce **due inventari deterministici** per le dipendenze
bloccate di Expo/npm e dei 20 progetti .NET/NuGet:
`tao-npm.cdx.json` e `tao-nuget.cdx.json`. I documenti includono
package URL (PURL), versioni risolte, SHA-512 e riferimenti ai lock
di origine; non contengono dati utente. Sono generati in un percorso
temporaneo, verificati a byte identici e caricati nell'artefatto
GitHub Actions `tao-locked-dependency-sbom` con conservazione 14
giorni. Vedi [ADR 0089](adr/0089-deterministic-locked-package-cyclonedx-sbom.md),
[runbook SBOM](docs/locked-dependency-sbom.md) e [#192](https://github.com/KeyserDSoze/Tao/issues/192).
**Un SBOM di lockfile non è una scansione CVE**, non prova
licenze, attestation, runtime/binary/container coverage o compliance:
#22/#15/#10 restano aperte.

## Vulnerabilità dipendenze — CI security gate (non ancora verde)

Tao esegue in CI il job **`security-audit`**: scanner npm ufficiale
su tutti i package del lock e sul sottoinsieme produzione, più verifica
NuGet delle dipendenze dirette/transitive di tutti i 20 progetti.
Lo script `tests/quality/run_live_advisory_audit.py` pubblica un report
JSON come artefatto Actions `tao-security-audit` (14 giorni),
**anche quando il gate fallisce**. Gli output incompleti o le advisory
non disponibili producono fallimento, mai un falso esito pulito.
Le eccezioni sono inizialmente assenti e, se introdotte, richiedono
advisory, versione, issue, owner, motivazione e scadenza entro 30 giorni.
I controlli usano il grafo bloccato, senza installazioni forzate.

Il 23 novembre 2026 è stato aggiornato il **solo Vitest alla 5.1.1**
con `npm ci`, TypeScript, Vitest, Expo export e Playwright superati,
eliminando **2 critical** di `tinypool`/tooling. Restano **3 high**
nella catena di produzione `expo-router` /`query-string` /
`decode-uri-component`, più 3 moderate: [#194](https://github.com/KeyserDSoze/Tao/issues/194).
L'override ingenuo alla versione ESM `decode-uri-component 0.5.1`
ha rotto il parser CommonJS; il test di compatibilità impedisce di
ripeterlo. È aperta [#195](https://github.com/KeyserDSoze/Tao/issues/195)
per una migrazione Expo compatibile. **Finché ci sono finding high
non mitigati la CI security resta volutamente rossa.**
Vedi [ADR 0090](adr/0090-fail-closed-npm-nuget-advisory-gate.md)
e il [runbook](docs/dependency-advisory-scan.md).

## Aggiornamento di sicurezza Expo SDK58

Tao usa ora il lock npm v3 verificato con **Expo SDK58 (58.0.26),
Expo Router 58.0.32, React Native 0.88.1, React 19.3.0 e
Vitest 5.1.1**. Il grafo non include più `query-string 7`,
`decode-uri-component 0.2.2` e `tinypool`; nessun override
transitivo è stato introdotto. [Trial isolato
45217840306](https://github.com/KeyserDSoze/Tao/actions/runs/45217840306):
`npm ci`, typecheck, Vitest, Web export, Playwright, bundle Android e
audit npm all/prod hanno superato i controlli (**0 HIGH/CRITICAL,
3 MODERATE**). Il [gate SCA reale
45220400873](https://github.com/KeyserDSoze/Tao/actions/runs/45220400873)
ha verificato npm all/prod più NuGet 20/20 con job SUCCESS.

Il nuovo [validator](src/mobile/scripts/verify-sdk58-security.mjs)
impedisce il ritorno di vulnerabilità note, incompatibilità native,
override di dipendenze e perdita di `web.output=static` e
`/Tao` per GitHub Pages. CI verifica anche i **bundle Metro
Android e iOS**; non sostituiscono EAS build, firma, distribuzione o
test su emulatori e dispositivi, ancora pendenti nella #195.
Vedi [ADR 0091](adr/0091-expo-sdk58-security-migration.md) e
[runbook Expo](docs/expo-sdk58-security-upgrade.md).
Non è una certificazione di assenza CVE o di conformità GDPR.

## Avvio API locale (servizi open source)

```sh
cp docker/.env.example docker/.env
# Generare password locali forti e sostituire i placeholder.
docker compose --env-file docker/.env -f docker/compose.yml up --build
curl http://localhost:8080/health
# In modalità embeddings ollama: docker compose --env-file docker/.env -f docker/compose.yml exec ollama ollama pull nomic-embed-text
```

Keycloak locale è disponibile su localhost:8081, realm `tao`; creare un utente e configurare i client via console Keycloak. I token API devono includere `sub`, `tenant_id` e `aud=tao-api`. **Nessun token di test o account demo è hardcoded.** Test integration richiede un Postgres pgvector usa-e-getta e la variabile `TAO_TEST_DB`.

## Rilascio

- **Web**: Settings → Pages → Source: GitHub Actions; workflow `.github/workflows/pages.yml` pubblica `src/mobile/dist` quando Pages è abilitato. URL atteso dopo deploy riuscito: https://keyserdsoze.github.io/Tao/
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
EXPO_PUBLIC_TAO_OIDC_ISSUER=http://localhost:8081/realms/tao \
EXPO_PUBLIC_TAO_OIDC_CLIENT_ID=tao-web \
EXPO_PUBLIC_TAO_API_URL=http://localhost:8080 \
npm run web
```
Keycloak non crea credenziali utente reali per default: creare un utente sintetico nel realm locale e testare l'OIDC flow; non commitare password. Redirect consentiti nel realm import: `http://localhost:19006/*` e `http://127.0.0.1:19006/*`. Client OAuth **pubblico**, senza secret. L'API valida firma, issuer, audience e sub/tenant del bearer JWT. Configurare separatamente HTTPS, client, origins e issuer reali per una futura deployment Azure/Pages (non incluso). Le variabili `EXPO_PUBLIC_*` sono visibili nel browser: **NON inserire segreti**.

La Pages pubblica su <https://keyserdsoze.github.io/Tao/> mantiene la demo locale quando non riceve configurazione cloud. La presenza di una sola variabile viene trattata come errore di configurazione, mai come sync implicito. Native iOS/Android resta demo fino all'OIDC via browser nativo con SecureStore (#40). Audio resta solo locale, non viene caricato dal flusso memo.

## PostgreSQL RLS e connessione non privilegiata (feature #30)

La memoria e i consensi ora hanno **Row-Level Security applicata dal database**. L'API usa un ruolo `tao_app` non superuser, non owner e privo di `BYPASSRLS`; un account distinto di migrazione installa le tabelle e concede all'app solo SELECT/INSERT/UPDATE/DELETE sui dati personali. Il backend **rifiuta l'avvio** se manca uno dei due ruoli, se i ruoli coincidono, se l'utente applicativo può bypassare RLS o se le policy non sono `FORCE`. I repository impostano la coppia `tenant_id`+`owner_sub` verificata dal JWT esclusivamente tramite `set_config(..., is_local=true)` nella stessa transazione SQL del lavoro; le connessioni riutilizzate non ereditano l'identità precedente.

Per Docker locale creare due password differenti in `docker/.env` (variabili `POSTGRES_PASSWORD`, `APP_DB_PASSWORD`), poi `docker compose --env-file docker/.env -f docker/compose.yml up --build -d`. Il servizio one-shot `db-bootstrap` crea o aggiorna il ruolo runtime: serve anche per volumi già esistenti. L'API riceve `Database__ConnectionString` (solo `tao_app`) e `Database__MigrationConnectionString` (solo owner schema). **In Azure occorre provisionare questi due account/identità separati con permessi minimi e secret vault**: la configurazione Docker è solo uno sviluppo locale.

I test con pgvector vero verificano SQL non filtrato, blocco INSERT/UPDATE cross-owner, revoca e assenza di leak del context tra connessioni. La UI su GitHub Pages resta demo, l'accesso OIDC Web funziona solo con API+Keycloak reali configurati. Questa difesa non neutralizza la SQL injection se l'attaccante controlla istruzioni SQL arbitrarie e cambia i parametri custom `tao.*`; servono query parametrizzate, permessi minimi, audit e threat review.

## Esportazione e cancellazione dati (prima slice #31)

Quando Expo Web è collegata a Keycloak e API reali, la sezione **I tuoi dati** permette di scaricare un JSON con tutte le note e cronologie di consenso presenti nel PostgreSQL principale, comprese versioni raw/normalizzate e embedding derivati. Il download avviene dal browser soltanto dopo richiesta esplicita; i dati non finiscono in GitHub Pages o localStorage.

La cancellazione richiede una spunta e la digitazione esatta di `ELIMINA`; il server richiede inoltre il valore `ERASE_MY_DATA`. La transazione PostgreSQL rimuove consensi e note incluse eventuali embedding, e permette di ripetere l'operazione senza errore. L'identità dell'account e la RLS provengono sempre dal JWT verificato. I nuovi grant/contenuti sono serializzati con la cancellazione tramite advisory lock per attore.

**Perimetro reale:** questa funzione agisce soltanto su `memory_entries` e `consent_grants` del database PostgreSQL primario. Non elimina backup, file audio ancora sul dispositivo, dati presso Keycloak, log, copie esterne o richieste ai provider AI già inviate. La cancellazione del database non significa che sia soddisfatto l'intero diritto all'oblio; la feature completa #31 resta aperta, come #32 retention e gli altri gate privacy. Dettagli: [docs/privacy-data-rights.md](docs/privacy-data-rights.md).

## Conservazione automatica della memoria PostgreSQL — slice #136 / #32

Le note nella tabella `memory_entries` scadono **30 giorni dopo `received_at`**, l'istante server di prima ricezione; non dalla data dell'evento né dal rinnovo di un consenso. L'idempotent retry di una nota non allunga il periodo. Le note già scadute non appaiono nella timeline. Un worker ricorrente elimina fisicamente dal database primario i record scaduti, **inclusi testo raw, dati normalizzati e vettori**; esegue una prima scansione all'avvio dell'API e poi ogni 15 minuti (max 5.000 record per ciclo, con batch 500).

La configurazione locale ora richiede **tre segreti PostgreSQL differenti** in `docker/.env`: `POSTGRES_PASSWORD` per le migrazioni, `APP_DB_PASSWORD` per l'API con RLS e `RETENTION_DB_PASSWORD` per `tao_retention`. Il ruolo di pulizia non legge direttamente tabelle personali e può chiamare soltanto una funzione SQL delimitata e testata. Il bootstrap Docker gestisce volumi esistenti.

**Attenzione:** i 30 giorni sono una policy tecnica iniziale e **non** una durata GDPR universalmente valida. I consensi/audit non sono soggetti a questa eliminazione finché non è definita una policy specifica. Backup/WAL, provider AI, dispositivi, object store e connettori sono fuori scope. Il parent #32 rimane aperto fino alla configurabilità per finalità/categoria/tenant, alla propagazione e alla revisione legale. Vedi [docs/retention.md](docs/retention.md).

## Cifratura applicativa — prima base #33 (sotto-issue #137)

`Tao.Application.IEnvelopeEncryption` e `Tao.Infrastructure.AesGcmEnvelopeEncryption` offrono un **codec crittografico riutilizzabile**, con AES-256-GCM, chiave dati casuale per oggetto, protezione della chiave dati tramite KEK versionata, contesto autenticato `tenantId / ownerSubject / purpose / recordId` e `Rewrap` per aggiornare la KEK senza perdere i contenuti. Le chiavi possono essere caricate **soltanto da un file JSON esterno con percorso assoluto** (`EnvelopeKeyRing.FromFile`) o direttamente da un provider segreto fidato; non vengono fornite chiavi di default. Dettagli in [docs/envelope-encryption.md](docs/envelope-encryption.md).

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

Tao now applies atomic, per-API-instance live buffered
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
In addition to per-account 120 requests/minute, Tao
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

## Bounded per-account rate partitions (#167 / ADR 0066)

The 120/min authenticated actor quota now also bounds
retained actor-rate state to **8192 actor windows per
API instance**, configurable as RateLimit:MaxTrackedActors
(1..32768). It replaces the former ASP.NET named
per-actor partition policy but preserves the per-user
minute rate and signed tenant+subject keys. A common
60-second window engine also powers the existing
tenant-wide 600/min, 4096-partition quota. Unknown
actors receive fail-closed HTTP 429/no-store/Retry-After
when all tracked actor partitions are occupied, while
existing active actors can use their remaining quota.
Expired partitions are lazily reclaimed on traffic;
there is no unbounded key retention, but no guarantee
of capacity for newly arriving users either. Neither
quota is distributed across replicas or sufficient
for full SaaS ingress/WAF/load/GDPR release.


## Tenant-bounded actor request windows (#168 / ADR 0067)

Authenticated `/api/v1` requests retain a process-local 60-second
actor budget (120/min default) and independent tenant-wide budget
(600/min default). The actor partition dictionary remains bounded globally
(default 8192) and now also **per validated OIDC tenant** via optional
`RateLimit:MaxTrackedActorsPerTenant` (default min(256, global actor
limit), valid 1..global actor limit). Additional fresh actor IDs within one
tenant receive 429 `rate_limited`, no-store and Retry-After before
JSON read or DB access. Previously admitted actors keep their remaining
quota. Expired actor windows and tenant counters are reclaimed together
on request-driven lazy cleanup. This does not reserve one capacity slot
per tenant, distribute quotas across nodes, refund earlier budget steps,
or replace ingress/WAF/DoS controls. #37 and #2 remain open; synthetic
test evidence and limitations are recorded in #168.


## Atomic actor + tenant request-rate admission (#169 / ADR 0068)

The authenticated /api/v1 middleware now checks both signed OIDC actor
and aggregate tenant **60-second** fixed-window budgets as one process-local
admission. A 429 from either check does not increment the other budget
or create an actor/tenant partition. One short coordinator lock prevents
concurrent admissions from exceeding either limit, without reading bodies,
waiting or queuing. Existing actor 120/min, tenant 600/min and
MaxTrackedActors/MaxTrackedActorsPerTenant/MaxTrackedTenants remain
unchanged; 429 is generic and no-store, Retry-After is advisory.
This is NOT distributed between API replicas, an ingress/WAF defence,
capacity reservation or real-data GDPR release. Parent #37/#2 remain open.


## Cloud browser session isolation (#170 / ADR 0069)

An in-memory request generation now protects the web timeline/consent UI:
late responses cannot repopulate an earlier signed-in account after logout
or erasure, and older refreshes cannot override a newer one. Logout clears
the personal timeline immediately before attempting remote token revocation.
A real Keycloak + JWT + PostgreSQL Playwright regression and deferred-promise
Vitest tests guard this behavior. No client-side token or private-note storage
is added; this does **not** assert native parity or global token revocation.


## Browser bearer failure handling (#171 / ADR 0070)

A genuine API 401 or failed in-memory OIDC token lookup now ends the
visible Expo Web cloud session immediately, clearing private note/consent
UI and pending draft/confirmation. Request epochs prevent a late 401
from an older account from logging out the next session. 403, throttling
and transient network failures remain explicit retryable errors.
Playwright exercises an actual Keycloak-signed capture and PostgreSQL
timeline, then removes only the outgoing Authorization header so the real
API produces 401; Vitest covers error/reordering cases. This is not a
global Keycloak logout, token revocation of other devices or production
privacy/GDPR signoff.


## In-flight Web logout and pending operations (#172 / ADR 0071)
Expo Web logout now preempts slow authenticated requests. It immediately
clears local private notes, consent, drafts and confirmation, regardless
of whether a capture or personal export is in flight. Late responses
cannot repopulate another session's UI, trigger refreshes or download
a private JSON export. An already accepted server mutation is NOT
rolled back by closing the browser session: Tao explicitly warns
that a request may have been processed and must be checked at the
next login. Real Keycloak/JWT/PostgreSQL browser scenarios and
deferred-promise Vitest tests exercise the behavior; native/device
and production privacy gates remain outstanding.


## Private API response cache safety (#173 / ADR 0072)

The .NET API now sets \`Cache-Control: no-store\`,
\`X-Content-Type-Options: nosniff\` and \`Referrer-Policy: no-referrer\`
for **every** \`/api/v1\` response including success, consent denial,
OIDC 401, actor/tenant throttling, errors and streamed personal export,
even if an endpoint short-circuits. The /health and /ready probes are
not affected. Tests exercise real PostgreSQL via xUnit and JWT/OIDC
through Playwright Keycloak. This only governs HTTP caches honoring
origin headers, not service-worker Cache API, downloads, backups or
rogue CDN rules; wider privacy/security gates remain open.


## Navigazione note precedenti — Web (#174 / ADR 0073)

La timeline Web autenticata non si interrompe più dopo le ultime 30 note:
\`GET /api/v1/memories/page?limit=10&before=<cursor>\` serve lotti limitati,
ordinati per timestamp e UUID con \`nextCursor\` soltanto se restano
record. "Carica altre note" accoda senza duplicati e senza memorizzare note
o cursori in localStorage. La navigazione è isolata da JWT/RLS lato
PostgreSQL e invalidata su logout, nuova sessione, revoca, cancellazione
e refresh. Il vecchio \`GET /api/v1/memories\` array resta compatibile.
Il cursore non è una credenziale; pagine successive non sono uno snapshot
immutabile in presenza di nuove note retrodatate o scadenze. Native,
sincronizzazione offline cifrata e rilascio per dati reali restano aperti.


## Retry sicuri e identificatori canonici (#175 / ADR 0074)

Una nota con lo stesso \`source + externalId\` nello stesso account/tenant
non viene più riscritta silenziosamente durante il periodo di validità:
un retry identico restituisce l'UUID iniziale **senza aggiornare il
ciphertext AES-GCM**, la provenienza o la scadenza; contenuti differenti
con identica chiave ricevono HTTP \`409 memory_idempotency_conflict\`,
senza dati personali nella risposta. Il browser mantiene il testo nel
campo di modifica e mostra un conflitto esplicito, non un logout.
Una cattura *esplicita* dopo la scadenza, con consenso valido, può ancora
riacquisire la chiave secondo la policy attuale. Questo non è editing,
versionamento né sincronizzazione offline; #125/#41 restano aperte.


## Revoca di consenso anche da un dispositivo rimasto indietro (#176 / ADR 0075)

Se un dispositivo conosce ancora un ID consenso precedente (G1)
ma un altro ha già sostituito quel consenso con G2 per la
**stessa fonte/finalità**, DELETE G1 ora revoca anche il grant
attivo G2 nel PostgreSQL primario. Il backend risolve lo scope
solo dalla riga del soggetto JWT verificato, sotto actor lock
e RLS; storico e scope altrui rimangono invariati.
Una nuova cattura dopo la revoca riceve 403, anche se il
device originario non aveva ancora visto G2. La revoca non
cancella dati precedenti, né disabilita una scelta di consenso
esplicita fatta dopo la revoca; non sostituisce la revoca
di accessi provider/IdP o le procedure GDPR sui sistemi esterni.

## Source IDs univoci per tentativo di cattura Web (#177 / ADR 0076)

Due nuove note manuali create nello stesso millisecondo non condividono più
l'`externalId`: ogni nuova cattura Web usa un UUID v4 Web Crypto sotto
HTTPS/loopback. Un retry dopo risposta incerta conserva invece la chiave
originale in memoria, per rispettare la semantica idempotente #175.
Browser senza generatore sicuro: salvataggio cloud negato, non
fallback al timestamp. Le chiavi del solo demo locale non sono identità
multi-device e nessuna sincronizzazione native/offline è dichiarata.

## Ledger consensi: visibilità grant manual anche oltre 200 eventi storici (#178)

La lista API dei consensi resta limitata a 200 elementi, ma dà
precedenza all'autorizzazione `manual/memory.capture` tuttora valida,
poi agli altri grant attivi e infine allo storico recente. In questo
modo 260 rinnovi/scadenze/revoche non possono far apparire "nessun
consenso" in Web quando la cattura manuale è tuttora autorizzata:
la voce resta visibile e revocabile. Il gate reale di consenso continua
a essere PostgreSQL con JWT/RLS. Il ledger completo è ora paginato lato API (#180) e consultabile da Web
autenticato (#181); #29/#41/#2 rimangono aperte.


## First-party consent history API (#180)

`GET /api/v1/consents/page?limit=25&before=...` now pages the
JWT-actor-scoped PostgreSQL consent history without the old 200-row
cutoff. It uses canonical keyset `granted_at DESC, id DESC`,
max 50 rows per call, explicit continuation and signed actor/RLS on
every SQL query. `GET /api/v1/consents` remains backward-compatible
and prioritizes active manual consent. The Web API adapter has a typed method and the authenticated UI
can now browse and withdraw first-party source grants (#181, ADR 0079).
No implication of complete GDPR/provider coverage or of production
readiness.


## Web registro dei consensi first-party (#181)

L'interfaccia Web autenticata permette di aprire esplicitamente
il registro consensi, caricare pagine successive da 25 grant,
distinguere attivi/scaduti/revocati e revocare una fonte
first-party per volta tramite API e JWT reali. Lo storico
non è troncato silenziosamente a 200: il cursore indica
altre pagine fino alla fine. Le richieste tardive sono
invalidate su logout/401, erase, rinnovo e revoca.
La revoca **non cancella** automaticamente le note già
conservate e non prova consenso di terzi. Restano
fuori scope OAuth provider, native cloud offline,
snapshot storico e valutazione di conformità GDPR.


**Evidenza TDD UI:** issue [#181](https://github.com/KeyserDSoze/Tao/issues/181), ADR [0079](adr/0079-signed-web-consent-ledger-lifecycle.md), test Vitest dei fence e browser Keycloak reale. La UI non inventa permessi di provider esterni né afferma conformità GDPR completa.


## Filtro sorgente sulla timeline Web (#182)

La timeline cloud autenticata offre **Tutte** o una sorgente
presente nelle note **già caricate**. Mostra il conteggio
visibile/caricato, un messaggio esplicito se un filtro non trova
risultati tra le righe attuali, e consente sempre di caricare
le pagine precedenti senza perdere il filtro. Nessuna ricerca
globale per fonte né scansione automatica dell'archivio:
la sorgente visualizzata non concede privilegi su servizi
esterni. Sessione, 401 ed erasure azzerano il filtro.
Vedi [docs/timeline-source-filter.md](docs/timeline-source-filter.md)
e ADR 0080. #79/#41/#2 restano aperte.


## Timeline Web per giornate locali (#183)

La timeline ora mostra intestazioni in italiano per la
giornata del dispositivo, calcolate dal timestamp originale
`occurredAt` (corrette anche intorno a mezzanotte e cambio
ora). Le note mantengono ora, sorgente e tag: quando una
giornata continua in una pagina successiva, la UI usa una
sola intestazione. Il filtro sorgente #182 agisce prima
dei gruppi e il logout/401/erase li cancella insieme
alle note. Non è un riepilogo AI, né una ricerca su
tutte le giornate o sincronizzazione native.
[Guida](docs/timeline-day-grouping.md) · ADR 0081; #79 aperta.


## Filtro tag combinato con sorgente e giornate Web (#184)

Nella timeline Web autenticata puoi selezionare **Tutti i tag**
o un'etichetta presente nelle note già caricate, insieme alla
sorgente (#182). I due filtri lavorano in **AND** e i
raggruppamenti giornalieri (#183) mostrano solo i record che
corrispondono; «Carica altre note» rimane disponibile anche
con zero risultati. Queste etichette derivano da regole di
parole chiave, non sono insight AI. Nessuna ricerca globale,
consenso provider o funzione native/offline è implicita.
[Guida](docs/timeline-tag-filter.md) · ADR 0082; #79 OPEN.
## Selezione delle giornate già caricate nella timeline Web (#185)

La UI autenticata offre **Tutti i giorni** e i giorni locali realmente presenti nelle pagine caricate, in aggiunta a sorgente e tag. Le tre scelte si combinano con AND; gli altri giorni diventano disponibili soltanto usando esplicitamente **Carica altre note**. Questa non è ricerca globale né inferenza di thread. La selezione è solo in memoria e viene azzerata con logout, 401 o cancellazione dal PostgreSQL primario. [Guida](docs/timeline-day-filter.md) · ADR 0083; le parent #79/#41/#2 restano aperte.
