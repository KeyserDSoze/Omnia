# Omnia — architettura v0.1 (2026-11-17)

## Scelta architetturale

Monorepo ispirato a SoftwareFactory, con confini espliciti `domain → application → infrastructure → api`, test indipendenti, documentazione e ADR. Modular monolith **prima** dei microservizi. Su Azure un Container App .NET 10 e servizi PaaS. Expo Router ha un bundle statico per GitHub Pages e build native Android/iOS: il backend NON viene ospitato su Pages.

```text
Expo React Native (iOS, Android, GitHub Pages /Omnia/)
     | OAuth Authorization Code + PKCE [not yet implemented in UI]
     v
Omnia.Api ASP.NET Core .NET 10 (Bearer JWT/JWKS OIDC)
     | DI: IMemoryCatalog
     v
Omnia.Application (catalog, provenance, classification)
     | IMemoryRepository    | IEmbeddingProvider
     v                      v
Omnia.Infrastructure     None | Ollama | Azure OpenAI
     |
PostgreSQL 17+ pgvector (same engine dev/test/production)
```

## Bounded contexts (target)

1. **Identity & Tenancy** — OIDC provider, tenant, subject, OAuth consent registry, grant expiry and revocation.
2. **Connections** — connector manifests, OAuth tokens in managed secrets, MCP transport, capability discovery, per-source synchronization checkpoints and rate limits.
3. **Capture & Provenance** — microphone/import/webhook ingest; immutable source events, original payload in immutable object store (planned for binary), timestamps, source IDs, checksums.
4. **Memory** — canonical event schema, user taxonomy, tags, summaries, embeddings, thread stitching, temporal context and evidence links.
5. **Intelligence** — retrieval with references, personal trend analysis, observed vs inferred distinctions and correction workflow. No diagnosis or unquestionable personality verdicts.
6. **Agency** — plan → preview → approve → execute → audit; no write-capability grant by default.
7. **SaaS** — billing, quotas, residency, retention, encrypted backup, export and deletion: **not implemented**.

Initially only a thin slice of Capture/Memory is coded. Do not present the other contexts as implemented.

## Canonical event contract

```json
{
  "id": "uuid",
  "schemaVersion": 1,
  "tenantId": "trusted-jwt-claim",
  "subjectId": "trusted-jwt-sub",
  "source": "crm",
  "sourceItemId": "event-123",
  "occurredAt": "2026-11-17T10:00:00Z",
  "receivedAt": "server-generated",
  "contentType": "application/json",
  "raw": { "storage": "inline-text-v0.1", "value": "{}" },
  "normalized": { "text": "..." },
  "summary": "...",
  "tags": ["lavoro"],
  "embedding": { "dimension": 768, "model": "nomic-embed-text", "status": "ready-or-pending" },
  "threadId": null,
  "consentId": null,
  "provenance": { "checksumSha256": "planned", "connectorVersion": "planned" }
}
```

Current implementation uses `memory_entries` with raw text, JSONB normalized form, summary, text[] tags and nullable vector(768). The sample includes *future fields* explicitly marked as planned. Import is limited to text/plain, application/json and XML (as raw text). MIME allowlist, 64-KiB raw limit and PostgreSQL tenant/user filters apply. Idempotency key: `(tenant_id, owner_sub, source, external_id)`. Domain class validates, application classifies and invokes an injectable embedding provider, infrastructure persists via Npgsql parametrized SQL and a versioned migration protected by advisory transaction lock.

**Important:** keyword rules are placeholders, not generative summaries, inferred emotion or AI coaching. Embedding `null` is not an invented vector: the data awaits an indexing pipeline. No data is sent to AI when `Embeddings:Provider=none`. For `ollama` and `azure`, content is sent to the configured embedding endpoint with explicit operator setup.

## Sync + agent future shape

A connector implements `DiscoverCapabilities → ConnectWithConsent → Pull/Subscribe → Normalize → Emit`. Each event carries idempotency key, occurrence time, source, origin metadata and optional thread references. Do not assume that all integrations grant the same access: WhatsApp Business APIs, Microsoft Graph, devices, telephony, and MCP have distinct auth and permissions. MCP servers should run in isolation behind capability allowlists, human approval and rate limits (not present yet). Use inbox/outbox and durable queue for future async enrichment, retries, dead-letter handling, poison input isolation and deletion propagation.

Actions are **read-only by default**. For sending messages, require user-approved per-recipient scopes; never silently send as the user. Suggested drafts must expose underlying evidence and distinguish observed facts from AI inference.

## Azure PaaS ↔ local open source by DI (target matrix)

| Port / capability | Azure PaaS | Docker/test |
|---|---|---|
| IMemoryRepository | Azure Database for PostgreSQL Flexible Server + pgvector | pgvector/pgvector:pg17 |
| IEmbeddingProvider | Azure OpenAI embeddings | Ollama nomic-embed-text |
| IObjectStore (planned) | Azure Blob Storage | MinIO |
| IEventBus (planned) | Azure Service Bus | RabbitMQ |
| Identity/OIDC | Entra External ID (planned) | Keycloak |
| ISecretStore (planned) | Azure Key Vault | OpenBao (open source, planned) |
| Observability | Azure Monitor / Application Insights (planned) | OpenTelemetry Collector + Jaeger (planned) |\n| ICache (optional) | Azure Managed Redis (planned) | Valkey OSS (planned) |\n| Realtime notifications (optional) | Azure Web PubSub (planned) | Self-hosted SignalR (planned) |
| Images/runtime | ACR + Azure Container Apps | Docker Compose |

Only PostgreSQL, JWT/OIDC validation and embedding adapters are actually implemented. All other mappings are design decisions, not production-ready integrations.

## Architecture gates

- No unauthenticated data endpoints. Identity requires **exactly one non-empty `sub` and `tenant_id`** in valid signed OIDC bearer claims. Public `/health` is not readiness.
- Mobile/web UI is disconnected from the secured API until a real native/web PKCE flow exists; it must never fake cloud persistence.
- Local audio capture requires explicit microphone permission and records locally only. Phone call capture remains platform/restriction dependent and is **not implemented**.
- Production requires private network, TLS, issuer, least privilege, tenant isolation tests, signed consent, content retention, deletion/export, rate limiting, threat model, secret rotation and observability before accepting real personal data.

> **Parità non significa disponibilità corrente:** OpenBao/Valkey/SignalR sono contratti di progetto, non servizi avviati dal Docker Compose attuale. Ogni nuovo port richiede test contrattuali contro la variante OSS e staging Azure, prima di essere dichiarato supportato.

## Consent-first ingest (vertical slice of #29)

`ConsentGrant` è un'entità di dominio immutabile con `tenantId`, `ownerSubject`, `source`, `purpose`, `policyVersion`, `grantedAt`, `expiresAt` e `revokedAt`. `ConsentService` in Application ammette solo scopo `memory.capture` con acknowledgement esplicito e TTL massimo 90 giorni; `IConsentGuard` è controllato prima di `IEmbeddingProvider`. PostgreSQL usa migrazione `0002_consent_grants.sql`, index unico sul grant non revocato per scope, transaction advisory lock per re-grant; `PostgresMemoryRepository` fa un ulteriore controllo atomico `SELECT ... FOR SHARE` prima dell'UPSERT raw. La revoca dell'accesso e la persistenza non possono attraversarsi senza lock transaction.

Il grant non conferisce automaticamente il permesso di registrare terzi, accedere a CRM/WhatsApp, effettuare profilazione oppure eseguire strumenti MCP. L'accesso ai dati storici rimane disponibile finché feature separate di export, retention e cancellazione non saranno implementate. Contratto: [docs/consent-api.md](consent-api.md). Release readiness: parziale, non per dati reali.

## API per-identity throttling (first slice #37)

ASP.NET Core built-in rate-limiter middleware is placed **after JWT authentication** and before authorization. Both v1 API groups require a fixed-window per authenticated tenant and subject partition (120 req/min default) or fallback peer IP for unauthorized requests. Endpoint rejects with 429 and no waiting queue. The limiter is **instance-local**: it does not establish a cluster-wide SaaS quota and is not a replacement for the Azure network edge WAF, per-tenant budget, or durable metering (#102).

## OIDC web client + sync slice (2026-11-17)

Il Browser Web (Expo static `/Omnia/`) usa `oidc-client-ts` come public client per Authorization Code + PKCE S256, valida il callback via state/nonce/issuer, mantiene JWT access e refresh **in RAM** (nessun storage persistente) e memorizza solo le transazioni OAuth necessarie in `sessionStorage`. Non affida al browser il controllo finale: .NET 10 verifica sempre firma JWT/JWKS, issuer, audience e claim unici `sub`/`tenant_id`. Il client non invia tenant/owner nel corpo. I token sono inviati esclusivamente in header `Authorization: Bearer` verso una API HTTPS configurata (HTTP solo localhost/loopback per sviluppo) con redirect vietati; errori HTTP/timeout non generano falsi salvataggi. Consent `manual/memory.capture` richiede spunta esplicita; revoca impedisce nuovi memo. La UI è solo Web, non una prova di autenticazione nativa.

## Tenant/subject Row-Level Security — prima implementazione di #30

`0003_rls_scope.sql` definisce `FORCE RLS` in PostgreSQL 17 su `memory_entries` e `consent_grants`: row owner = `current_setting('omnia.tenant_id',true)` e `current_setting('omnia.owner_sub',true)`, verificati da `USING` e `WITH CHECK`. I valori mancanti/empty negano l'accesso. `PostgresActorScope` apre connessione e transazione, imposta GUC locali con parametri prima di ogni statement e ne governa commit/rollback; **non ci sono GUC di sessione globali**. Il runtime connection pool usa account `omnia_app` senza poteri bypass/owner, mentre `PostgresMigrator` usa una connessione DI distinta per DDL + GRANT. `PostgresRuntimeGuard.VerifyAsync` rifiuta avvio per credenziali runtime privilegiate o RLS disabilitata. `docker/db/bootstrap.sh` crea/ruota account locali senza password hardcoded. CI e browser OAuth reali avviano lo stesso runtime role.

Questa è difesa in profondità contro query che dimenticano il filtro tenant, non una sostituzione della validazione OIDC né una protezione contro SQL arbitrario ottenuto per injection. Ruoli admin di migrazione e superuser restano confini ad alto privilegio: Azure deve separarli e gestirli fuori dal servizio pubblico.

## Personal data access + erasure — slice #31

`IPersonalDataService` (Application) ha due operazioni nel PostgreSQL corrente: export JSON via streaming e cancellazione transazionale. `PostgresPersonalDataService` (Infrastructure) usa `PostgresActorScope` e `PostgresActorLock` (advisory transaction lock, actor = tenant+subject). L'export tiene il lock durante la lettura e comprende tutte le righe e i derivati delle tabelle personali, senza limiti artificiali di 100/200 record. La cancellazione ottiene lo stesso lock e rimuove prima i consensi, poi le note, nella stessa transazione; `GrantAsync`, `RevokeAsync`, `UpsertAsync` acquisiscono il lock prima di scrivere per serializzare i tentativi concorrenti. Tutto rimane sotto FORCE RLS e ruolo DB limitato. Le API non ricevono tenant/owner dal body, solo dai claim JWT validati.

`GET /api/v1/privacy/export`: risposta JSON attachment no-store. `POST /api/v1/privacy/erase` con conferma esplicita: receipt con conteggi e limitazione dichiarata a `omnia-postgresql-primary`. La UI browser espone download e cancellazione con checkbox + frase esatta. Nessuna revoca di token IdP o propagazione a provider/backups in questa slice; native resta da implementare.

## Memory retention worker — M1 slice #136

- Migrazione `0004_memory_retention.sql`: `expires_at` NOT NULL e default DB `now() + 30 days` per le note; backfill dei legacy da `received_at`, non dal momento della migrazione. Index su `expires_at`; se un retry idempotente avviene prima di scadenza il TTL resta immutato, mentre una nuova esplicita capture di un record già scaduto inizia un nuovo periodo. Consensi hanno lifecycle e scadenze separate.
- `PostgresMemoryRepository.RecentAsync` filtra `expires_at > now()` oltre a tenant+owner e RLS. L'export JSON include `expiresAt` e resta completo su **tutte le righe non ancora rimosse fisicamente**, anche se già scadute (diritto di accesso), sino al purge.
- `PostgresRetentionRepository` esegue solo `SELECT public.omnia_purge_expired_memories(@batch)` con parametro numerico; nel DB il ruolo `omnia_retention` è separato dall'account API `omnia_app` e dal migratore, non possiede permessi su tabelle e non può bypassare RLS. Il migratore concede esclusivamente EXECUTE sulla routine privilegiata, rimuovendo il default PUBLIC; `PostgresRetentionRepository.VerifyWorkerRoleAsync` blocca lo startup in caso di privilegio eccessivo.
- `RetentionSweepService` è un `BackgroundService` nell'attuale processo API (worker separato previsto in produzione), con prima esecuzione all'avvio e intervallo 15 minuti. `FOR UPDATE SKIP LOCKED` consente più istanze concorrenti, max 500 righe per batch e 10 batch per ciclo. Un errore è registrato con il solo nome del tipo e ritentato al ciclo successivo. Il limite batch impedisce monopolio/DoS del worker, ma non rappresenta una garanzia SLO o uno scheduler affidabile durante un outage.

## Cryptography boundary — slice #137

`Omnia.Application.EncryptionScope` e `IEnvelopeEncryption` definiscono un port provider-neutral per proteggere futuri payload persistenti. Infra implementa `EnvelopeKeyRing` (file JSON esterno, API `FromBase64` per trusted providers) e `AesGcmEnvelopeEncryption` (v1 binario con key ID, AES-GCM payload e wrap). L'operazione `Rewrap` verifica il tag del payload e sostituisce il solo DEK-wrapping, mantenendo immutati ciphertext e tag dati. Sono supportate fino a 16 KEK per key ring e payload in memoria fino a 1 MiB; non sono supportati media grandi in streaming. Per una futura persistenza il caller dovrà ricavare l'attore da JWT verificato e non dal corpo della richiesta, e associare il ciphertext al record canonical ID. Per il piano future data-store servono RLS, export, erase, retention e contract test aggiornati. **Il port non è ancora iniettato nell'API e non protegge i dati attuali del database.**


## Persisted sealed notes — phase 1 (#139)

Migration `0005_memory_sealed_payload.sql` adds nullable `memory_entries.sealed_payload bytea`; NULL identifies **historical unencrypted rows**, never a new write. A trigger rejects new plaintext inserts and sensitive updates to legacy rows; an additional table constraint ensures that sealed rows have `[sealed]`, `{}`, `[sealed]` and an empty tag array in the old columns. `PostgresMemoryRepository` first serializes under actor-wide transaction lock and retrieves the **canonical UUID** for upsert retries. `SealedMemoryPayload` wraps raw, normalized JSON, summary and tags into a single envelope with AAD: `tenant_id, owner_sub, "memory.payload", id.ToString("N")`. A repeated capture retains UUID/retention semantics, while using new randomized AEAD. RLS remains required for all reads.

`RecentAsync` decrypts summary/tag only for authorized current rows; `PostgresPersonalDataService` decrypts full payload for an authenticated user's JSON export and marks rows `sealed-v1` or `legacy-plaintext`. Data from older DB records is read by the legacy decoder **only when sealed_payload IS NULL**; a corrupted ciphertext must never use that fallback. Erasure and expiration DELETE whole rows, so encrypted payload and pgvector vector are removed together in the DB primary. API startup requires `Crypto:KeyRingPath` external key-ring (none in code/repository); Docker mounts local secret and CI generates random per-job KEK.

**This is not a complete encrypted database:** `embedding vector(768)`, model, IDs, source/type, timestamps and old rows are visible as previously. Backfill, key rotation of persisted notes, encrypted vector search tradeoff, read/write migration cutover, provider vault and backup recovery remain in the open parent #138/#33.

## Offline witness prefix audit — synthetic security slice (ADR 0028)

An additional **offline** .NET 10 tool `Omnia.WitnessAudit` validates a
bounded and snapshot-consistent prefix of erasure receipts from an
independent PostgreSQL witness using the EXECUTE-only reader, and an
HMAC checkpoint outside that PGDATA. This is not an application
service, API endpoint, production vault/ledger, or readiness
authorization. A checkpoint is evidence of a known prefix ONLY
if external custody preserves a newer authoritative version;
neither source transaction completeness nor rollback resistance of
both stores is established by the prototype.

## Synthetic third-cluster checkpoint custody (ADR 0029)

The recovery-protection prototype now separates primary PostgreSQL,
erasure witness and checkpoint anchor into THREE distinct disposable
PG17 databases and roles. SQL-only function permissions implement a
nonregressing appendable checkpoint count inside the third cluster;
an offline .NET issuer verifies the old HMAC prefix before advancing.
A read-only verifier detects simulated rollback of a witness prefix.
This remains on ONE CI runner and is NOT an independent offsite
monotonic authority, vault/HSM, release signer, production worker, or
complete DR/erasure gate.

## CI witness physical PITR safety proof (ADR 0030)

The three-PostgreSQL synthetic architecture now includes a real
physical PITR regression of the witness: restore a basebackup/WAL
clone to before a signed deletion while leaving the third-cluster
checkpoint untouched, then reject that clone via a dedicated read-only
auditor. The clone retains the witness's physical system identifier;
the anchor cluster has another. No API integration, real offsite
custodian, immutable store, global traffic release signer, or
cross-provider erase guarantee is supplied by this test.

## Live erasure delivery observation — offline CI slice (ADR 0031)

A new read-only infrastructure audit compares currently committed
primary `erasure_outbox` rows against independent witness receipts,
requiring exact UUID and actor scope. Its primary scan is bounded
inside one `REPEATABLE READ` transaction using the dispatch
SECURITY DEFINER function; the remote witness uses its separate
EXECUTE-only reader function. The standalone `Omnia.ErasureDeliveryAudit`
process is CI-only, never an HTTP API or recovery-release authority.
The check cannot establish erasure history lost after primary PITR
and cannot substitute for the independently anchored witness or
global traffic fencing.

## Source commit→witness ACK loss demonstrated with physical PITR (ADR 0032)

The synthetic DR architecture now tests a *negative* window:
primary `EraseAsync` commits to its own transactional outbox
AFTER the last witness publish and AFTER the WAL recovery point.
The physical primary clone loses both that erase transaction
and outbox event, while the independent witness never saw it.
A source-only read-only outbox audit can consequently return a
misleading success. A CI-private remembered UUID demonstrates
the gap but does NOT establish an independent authenticated
source commit timeline. The API stays quarantined; no automatic
`ready` or cross-provider delete is permitted. A real solution
needs commit provenance beyond the failure domain of the
primary, with proper source/remote failure handling and GDPR.

## Separated pre-erase intent stream (ADR 0033)

Within the SECOND disposable PostgreSQL witness PGDATA,
a separate `erasure_intents` table and narrow,
independently authenticated writer/reader roles
store a durable pre-delete request marker. It is
distinct in schema, UUID, roles and semantics from
the previously implemented post-commit erasure witness
receipt. The physical primary PITR test now shows
the pre-delete intent survives a primary rollback
that loses the source outbox before post-commit
witness publication. No production API integrates
the handshake; it does not guarantee primary commit
atomicity, authorize automatic DR re-deletion, or
serve as external offsite/immutable evidence.

## Request UUID → source outbox UUID linkage (ADR 0034)

CI-only `PostgresPersonalDataService.EraseWithWitnessedIntentAsync`
reads an immutable pre-erase request from the SECOND PostgreSQL
witness through a restricted intent reader and checks its
UUID and actor scope before executing the source RLS erasure.
The source outbox row is inserted in the same transaction
under that identical request UUID; the existing publisher
uses it as the independently persisted post-commit
`erasure_events.event_id`. Uncorrelated `EraseAsync`
remains unchanged. Replaying an old request UUID cannot
delete subsequently re-acquired data, because the second
source transaction aborts on unique_violation.
Physical source PITR still drops pre-ACK committed outbox
and DELETE and leaves only an uncertain request intent,
so this is not a production recovery release protocol.

**Stale-request bound:** an old request UUID is rejected only while its original source outbox survives. Post-PITR a lost original row removes this local uniqueness memory, so recovery MUST NOT replay a pre-intent automatically. This experimental path is CI-only and is not registered by the API.

## Read-only evidence triangulation for correlated erasure (#141 / ADR 0035)

The CI-only infrastructure reconciler reads a pre-DELETE intent from
the SECOND witness PGDATA, a correlated source outbox UUID using a
bounded REPEATABLE READ source snapshot, and a post-commit completed
receipt using the distinct witness reader. Each connection's
EXECUTE-only role is checked independently. The result is only
one of four visibility states; there is no cross-cluster snapshot,
signer, monotonic source commit certificate or authorization to
re-delete an actor. `MayReleaseRecovery` and `MayAutoReplay`
are false for every observation. The source primary physical PITR
drill shows that a previously observed source commit can appear
as remote-intent-only after rollback, which necessarily remains
`IntentOnlyCommitUnknown` rather than "never committed".

## Independent anchored PRE-intent prefix, distinct from erasure ACK (ADR 0036)

The witness's pre-delete intent stream now has a dedicated
`IntentCheckpoint` HMAC domain and consistent EXECUTE-only
snapshot reader. It is anchored in a SECOND database within
the THIRD physical PostgreSQL 17 cluster, distinct from
the completed-erasure HMAC anchor database. The real
physical PITR regression of the WITNESS now loses a new
pre-intent while the THIRD database stays alive, so the
read-only verifier rejects the recovered clone and
accepts the original stream. All three clusters still
share one disposable CI host; no independent offsite
custody, OIDC requester identity, source COMMIT evidence,
global ingress/worker/replica fence or legal GDPR guarantee
is provided.

## Gated synthetic pre-erase: independent anchor ACK first (ADR 0037)

The strict CI-only synthetic erasure method verifies
a matching pre-intent within the domain-separated
HMAC prefix held in a THIRD-PGDATA checkpoint database
before opening any actor-scoped primary transaction.
It refuses a remote intent that has not yet reached
the externally stored checkpoint. After validation,
primary DELETE+outbox are atomic with identical UUID,
but witness intent, third anchor and source transactions
are NOT globally atomic. The real primary WAL PITR
rehearsal demonstrates the pre-checkpoint denial and
post-checkpoint commit, and that an intent may survive
as `CommitUnknown` when the primary rolls back.
The production public erasure endpoint and API
interface remain unchanged. This is not global DR
release authorization, offsite signer custody or
authenticated GDPR request handling.

## Independent one-shot PRE-source-DELETE attempt record (ADR 0038)

The experimental CI-only erasure path validates an
HMAC-anchored intent across SECOND/THIRD PostgreSQL
clusters, then consumes a per-request UUID in the
THIRD PGDATA `admission.execution_claims` before
the primary actor RLS DELETE/outbox transaction.
The claim database uses separate EXECUTE-only
writer and read-only identities; same-UUID
concurrent attempts are serialized through a
unique index and never silently retried. An
attempt can remain committed without the
source transaction succeeding. The physical
primary PITR harness demonstrates that after
source rollback the recovered outbox can lose
the old UUID while the independently consumed
attempt still denies a second erasure. There
is no historical source COMMIT certificate,
OIDC requester proof, offsite WORM/vault or
global DR release authorization. The existing
REST/API erasure path remains unchanged.

## Read-only authenticated scope of one-shot attempt (ADR 0039)

The synthetic third-cluster claim store now exposes a
second EXECUTE-only reader function returning only the
stored 32-byte scope HMAC for a request UUID. A restricted
reader recomputes the canonical, domain-separated HMAC
of UUID+tenant+owner and compares in constant time;
wrong-scope, missing or tampered claims fail closed.
Writer and reader SQL capabilities remain separate
with no raw SELECT grants, and the previous boolean
lookup remains a non-authoritative diagnostic.
The physical primary PITR test verifies the scoped
claim across source rollback without mutating the
resurrected actor. It remains a receipt for an
*attempt*, not a source commit, legal erasure request
or production recovery authorization.

## Four-PGDATA synthetic one-shot admission fence (ADR 0040)

The experimental erasure method now gates a source
DELETE on: authenticated pre-intent in SECOND/THIRD,
one-shot attempt written to FOURTH physical PostgreSQL,
then one-shot THIRD claim, then primary actor delete
and transactional outbox. The FOURTH's physical PGDATA
and `system_identifier` are distinct, though all four
live on one disposable CI host; it is NOT offsite WORM.
The real third-PGDATA WAL PITR test restores to before
a claim and proves fourth-PGDATA HMAC still blocks reuse.
All admission records prove an ATTEMPT, not historical
source COMMIT, global GDPR erasure or release permission.
Production REST/OIDC integration remains untouched.


## CI-only rollback symmetry of third/fourth claim authorities (ADR 0041)

Le claim di ammissione sintetiche sono conservate in PGDATA
separate: la QUARTA è verificata anche come *target di PITR fisico*,
non solo come superstite del rollback della TERZA. Il test verifica
che il TERZO rifiuti l'UUID riaccettato sul clone QUARTA con `23505`
prima di qualunque mutazione primario. L'implementazione REST non
adotta questa catena; non esiste commit attestation, consenso reale,
custodia WORM/offsite né garanzia in caso di rollback simultaneo.
Non disegnare la topologia di CI come architettura produttiva.

## Common-mode rollback of claim authorities (ADR 0042)

Le due PGDATA CI possono retrocedere **insieme** prima della
stessa claim. Il test fisico simultaneo mostra che i vincoli
UNIQUE di entrambe possono riaccettare l'UUID precedente:
la separazione dei `system_identifier` non è un
confine di custodia anti-rollback. Qualsiasi progetto
di authority produttiva richiede controllo esterno
indipendente e attestazione del commit del primario.
La prova non espone un'API e non rende effettiva
la cancellazione di dati reali.

## Boundary di lettura per evidence versione immutabile (ADR 0043)

Nuovo port `IImmutableAttemptObjectReader`: il reader è
obbligato a ottenere una versione esatta di un oggetto esterno
e la sua policy di retention, non la versione latest.
`ImmutableAttemptEvidence.VerifyAsync` vincola un expected
store ID e expected version ID (da fornire da trust domain
separato), uno scope HMAC UUID/tenant/owner e un HMAC
domain-separated di evidence; restituisce esclusivamente
un'osservazione del tentativo precedente senza release/replay.
Il solo contratto non rende reale Azure Locked o S3 COMPLIANCE.
Nessun adapter cloud e nessun control plane è implementato:
l'architettura non contiene ancora una offsite/WORM authority.

## Adapter S3 version-pinned (ADR 0044)

Il nuovo adapter `Omnia.ObjectStore.S3` implementa il port
`IImmutableAttemptObjectReader` con AWS SDK v4, ma non
viene registrato nella API SaaS o nel processo di DELETE.
Legge `GetObjectRetention` della stessa versione letta
via `GetObject` e rifiuta GOVERNANCE/no-retention,
latest/null, versionId differente, body over-limit.
Il test temporaneo usa SeaweedFS per il **protocollo**,
non una authority esterna reale. L'architettura richiede
ancora version pin indipendente, RBAC/vault e Azure parity.

## Azure version-scoped object-lock reader (ADR 0045)

Il port `IImmutableAttemptObjectReader` ora dispone anche
di un adapter separato Azure.Storage.Blobs. Un client
`BlobClient.WithVersion` legge HEAD e GET della stessa
versione e ne verifica la policy Locked, la scadenza UTC,
VersionId, size e ETag. Il core applica HMAC/scope.
Il progetto non registra l'adapter nell'API né usa un
account Azure reale: i contract test HTTP non attestano
immutabilità in Azure o custodia indipendente.

## S3 conditional attempt evidence publisher (#147, ADR 0046)

Il modulo S3 ora include una porta singolo-PUT, un adapter
AWS SDK v4 e un publisher fail-closed, NON registrati
nell'API. Prima del PUT il publisher verifica la ricevuta
HMAC canonica; invia COMPLIANCE conditional `IfNoneMatch=*`
con client no-retry; dopo ACK verifica la stessa
versione pin con il reader ADR0044. Se manca conferma
o le API di lettura sono indisponibili segnala
`Uncertain`, mai “assente” o “sicuro da ripetere”.
È protocollo CI su storage effimero, non authority
esterna al primario PostgreSQL.

## S3 version history reconciliation for lost ACK (ADR0047)

`Omnia.ObjectStore.S3` ora espone una porta paginata
`IS3AttemptVersionHistory`, un adapter AWS SDK v4
`ListVersions` e un reconciler read-only. Questo
ultimo ricerca una possibile versione COMPLIANCE scritta
anche quando l'ACK di PUT è stato perso, **senza
usare latest**, senza rieseguire PUT e senza toccare
le tabelle del PostgreSQL ripristinato. Solo una
versione positiva HMAC/retention/scope può produrre
`PreviouslyObserved`; cataloghi vuoti o ambigui sono
`Uncertain`. La lista non è un'autorità monotona
offsite e non si collega a REST o release.

## S3 exact-version signed pin witness candidate (ADR 0048)

La slice S3 aggiunge `S3ObjectLockVersionPinService`
e `S3ObjectLockPinReader`. Il primo verifica la ricevuta
firmata HMAC ADR0043 in COMPLIANCE, poi scrive una
**seconda versione firmata** nel namespace pin WORM,
sull'archiviazione S3 configurata separatamente.
Il readback richiede due VersionId espliciti, verifica
metadata reali e signature; non si fida di `latest`
o di uno stato DB post-PITR. Una versione positiva
non attesta DELETE COMMIT e nessuna route/worker
ottiene capacità di ripresa. La CI usa due bucket
dello stesso SeaweedFS, perciò non prova custody
offsite indipendente o monotonicità globale.
undefined

## ADR 0056 — API JSON read deadline after ADR 0055

`ApiJsonBodyBudget` includes a bounded TOTAL body-receive timeout
(`RequestBody:ReadTimeoutMilliseconds`, default 10000; range
100..30000). A slow/blocked authenticated JSON POST/PUT/PATCH
ends with 408/no-store before binding or DB work; genuine client
cancellations do not fabricate a 408. It does not replace Kestrel
request-header/body-rate policies, reverse proxy/WAF, connection
limits or distributed quota. See ADR 0056 and #157; #37 remains open.

## Actor-scoped JSON receive admission (ADR 0060)

At the REST entrypoint, after JWT/OIDC authentication + per-actor
request rate limits and cheap 413/415 media checks, the shared
ApiJsonBodyReadConcurrencyLimiter atomically controls both global
(active reads; default 32) and per-tenant+subject active reads
(default 4). A lock-protected dictionary retains keys only while
reads are active and removes them upon release, preventing idle
principal cardinality growth. The read lease is released before
the Minimal API handler/DB work. No new personal-data persistence,
no distributed quota and no tenant-wide fair sharing are claimed.

## OIDC tenant JSON receive isolation (ADR 0061)

An additional tenant-level intake bound applies ONLY while the API
receives authenticated JSON POST/PUT/PATCH /api/v1 bodies, after
OIDC/rate limiting/authorization and byte/media rejection. The
single process-local limiter atomically controls global (default
32), signed tenant (default 16) and signed actor (default 4)
concurrent reads. Tenant partition keys are derived from uniquely
validated tenant_id claims and are independent of sub; the existing
actor key remains tenant+sub length-framed. Both dictionaries evict
idle keys under one lock and retain no persisted quota identity.
This layer does not protect DB/AI handler concurrency, cross-replica
limits or reserve capacity for other tenants if the global
limit has already been reached.

## JSON buffering across slow API handlers (ADR 0062)

Authenticated JSON POST/PUT/PATCH under /api/v1 has two distinct
admission layers after 413/415: 32/16/4 process/tenant/actor read
permits that release before handlers, and an independent per-instance
buffered-request permit (default 64) held until bounded MemoryStream
and downstream handler are finished. Both are nonqueueing, 503 on
saturation and release on all error paths. This only bounds the
number of middleware-owned live buffers; no DB/AI cap, distributed
quota, per-tenant processing fairness or total memory bound.
