# Omnia — threat model / privacy gates (draft)

## Data classes

- Sensitive: third-party conversations, recordings, calendar/CRM content, behavioral inferences, location, biometric/voice characteristics. Data may belong to other people, not only the account owner.
- Core threats: cross-tenant leaks, prompt injection from external messages/MCP responses, fraudulent autonomous communication, lateral movement from an untrusted connector, OAuth token theft, unwanted recordings and model-provider data exfiltration.
- Trust boundaries: device → OIDC token → API → external connector/AI provider → PostgreSQL/storage. GitHub Pages assets and `EXPO_PUBLIC_*` values **must never** contain confidential material.

## Non-negotiable product requirements

1. Consent record before each data class/integration; visible indicator during any recording; record applicable party consent, context and local law. Do not silently record phone calls.
2. Explicit scope for *who*, *what*, *where*, *how long*, *which provider*; provide pause, revoke, retention limit, delete and export.
3. Never use unrelated third-party conversations to impersonate a contact. Voice cloning requires separate explicit authorization and anti-impersonation controls.
4. External messages, MCP tool metadata and retrieved content are untrusted input, **never instructions**. Isolate executable tools, restrict network/file scopes, quota, timeout and secrets per connector.
5. No autonomous send, purchase, publication or destructive action without an appropriate user-authorized policy; provide audit and revocation.
6. Dedupe by trusted user+source+external ID, tenant ID derived only from signed JWT. Test authorization at API and repository boundary.
7. Use encryption at rest and in transit, secret vault, token rotation, deletion propagation and retention expiries **before production**.
8. AI feedback is non-diagnostic, explainable, easy to correct and opt-in. Avoid claims of objective assessment of emotions/personality.

## Platform reality

- Native recording of the **app's microphone** is feasible with permission. Recording two sides of ordinary iOS/Android cellular calls is not generally available to third-party cross-platform apps through a universal public API; OS, device, market and consent rules differ. Import existing recordings or integrate authorized conference providers as separate flows.
- WhatsApp personal inbox access cannot be assumed equivalent to approved WhatsApp Business Platform integration.
- GitHub Pages only hosts the static frontend. Azure API requires HTTPS, OIDC, regulated PaaS setup and CORS allowlisting.

## Current foundation limitations

The MVP stores raw text unencrypted by the application (transport and managed-disk protections must be configured separately), has no retention/delete or full audit, no API throttling, no RLS, no consent ledger, no PKCE client integration, no deployed Azure topology and no automated real OIDC browser E2E. **Use synthetic data only.**

## Scoped consent gate (first vertical slice of #29, 2026-11-17)

La tabella SQL `consent_grants` e i servizi DI permettono `memory.capture` solo dopo una richiesta autenticata contenente esplicita accettazione, policy version e scadenza limitata a 90 giorni; ogni grant è delimitato dal `tenant_id` e `sub` verificati. Una nuova concessione revoca l'eventuale grant precedente nello stesso scope (mantenendo la storia). La revoca è idempotente e filtrata per tenant/subject. La pipeline controlla il grant **prima** delle chiamate AI e nuovamente sotto `FOR SHARE` nella transazione SQL di scrittura. Le nuove acquisizioni senza grant o con grant scaduto/revocato falliscono con HTTP 403.

**Rischi ancora aperti:** il booleano di accettazione API deve essere collegato a un'interfaccia OIDC/PKCE autenticata con testo informativo reale; non prova presenza fisica né consenso dei terzi; non esiste ancora un ledger dei consensi per destinazioni terze/OAuth, enforcement in job di sync/agent, cancellazione dei raw/derived, RLS, per-categoria consent, audit append-only o garanzie su AI in-flight prima della revoca. La sezione baseline sopra che descrive "no consent ledger" si riferisce allo stato precedente alla slice #29. Il progetto resta inadatto a contenuti reali fino agli altri gate privacy.

## API application rate limiting (slice of #37)

Rate limiter fixed-window per signed JWT tenant+sub (default 120 requests/60s; no queue) su `/api/v1/memories` e `/api/v1/consents`, eseguito dopo autenticazione. Anonymous/invalid claim requests share a fallback peer-IP partition; forged `X-Forwarded-For` header is not used. HTTP 429 with JSON `rate_limited`, `Retry-After: 60`. `/health` remains probe-friendly. Native test HTTP uses a fixture auth handler **not enabled in production**, while real OIDC/JWKS browser end-to-end security remains #17. Missing: distributed store/shared quota across Container Apps replicas, connector limits, upload/media quotas, reverse proxy/WAF/DDoS, audit and load tests. Feature #37 remains open.

## Web OIDC and scoped capture UI (first slice #39/#41)

Adottato `oidc-client-ts` per state/nonce validation e OAuth PKCE S256 (non ad hoc crypto). Refresh/access tokens sono in **memory-only** userStore: un reload richiede nuova autenticazione; il PKCE request state per il redirect è in sessionStorage con TTL gestito dalla libreria. Browser callback ripulisce il codice dalla URL; cliente non conserva password o client_secret. Le fetch API non seguono redirect, non usano cookie, e usano un'origine con HTTPS salvo loopback. Il grant manual user-first viene scelto dopo testo informativo con accepted=true; niente consenso automatico. Backend continua a validare JWT e consenso prima di invocare AI.

Limiti: non ancora certificata la user experience del rinnovo token/SSO e logout globale IdP; il logout locale tenta revoca token ma non chiude necessariamente la sessione SSO del provider. La Pages pubblica non dispone di backend Azure e resta solo demo. Native iOS/Android usa ancora demo e non un login fittizio. La UI non rende legale registrare conversazioni di terzi e non sostituisce DPIA, delete/export e RLS.

## PostgreSQL Row-Level Security (feature #30, 2026-11-17)

- `0003_rls_scope.sql` abilita `ENABLE ROW LEVEL SECURITY` **e** `FORCE ROW LEVEL SECURITY` su `memory_entries` e `consent_grants` con policy su ogni operazione `USING`/`WITH CHECK`. Senza `omnia.tenant_id` e `omnia.owner_sub` (o con stringhe vuote) non è visibile nessuna riga e INSERT fuori scope è rifiutato.
- Ruolo runtime `omnia_app`: non è table owner, superuser, BYPASSRLS o membro del ruolo owner; possiede unicamente i grant DML richiesti. Ruolo distinto di migrazione owner/admin. `PostgresRuntimeGuard` blocca l'avvio se l'account runtime può bypassare la policy o se non è forzata.
- Nessuna query personal-data senza `PostgresActorScope.OpenAsync`: il contesto tenant+subject deriva esclusivamente dal JWT verificato dall'API, viene impostato con `set_config(..., true)` **all'interno** della transazione e sparisce a COMMIT/ROLLBACK. I repository continuano ad applicare WHERE espliciti: difesa a strati.
- Tests: `RlsIsolationTests` usa PostgreSQL reale, connessione senza WHERE, ruolo diverso per query amministrative, due owner nello stesso tenant, un altro tenant, tentativi INSERT/UPDATE cross-owner, `row_security=off` e riutilizzo del pool con MaxPoolSize=1.
- **Limitazioni:** la policy basata su GUC custom non è una barriera a SQL arbitrario controllato da un attaccante (tali parametri possono essere modificati dalla stessa sessione SQL); rimangono necessari comandi parametrizzati e controllo delle query/injection. Superuser e BYPASSRLS *bypassano anche FORCE RLS*, quindi l'API non può usarli. Il ruolo schema-owner è riservato alla procedura di migrazione; in produzione isolarlo operativamente fuori dal processo API, gestire le credenziali con Key Vault ed evitare logging di connection string. Una migrazione malevola/compromessa rimane privilegiata.

## Export e cancellazione del database primario (slice #31)

Le nuove API privacy sono JWT/OIDC-authenticated, rate-limited e delimitate da RLS su `tenant_id` e `sub`, senza possibilità per il client di indicare l'owner. Export con `Content-Disposition: attachment`, `Cache-Control: no-store`, JSON streaming e nessun log dei contenuti. L'export include raw, normalized, summary, tags, embeddings (se presenti), data di ricezione e concessioni di consenso, anche revocate. La risposta contiene dati sensibili: il browser ne avvia il download solo su azione dell'utente, ma l'utente resta responsabile del file esportato.

`POST /api/v1/privacy/erase` richiede un body `confirmation: "ERASE_MY_DATA"` oltre alla sessione OIDC, e la UI richiede un consenso informato checkbox e frase esatta. La cancellazione elimina le due attuali tabelle personali in una transazione e un lock condiviso per attore con ingest/grants/revoke/export; il ritorno attesta **solo l'eliminazione nel database primario**. Nessun id/owner personale è inserito in una receipt persistente in questa fase. **Non** dichiarare eseguita la cancellazione da backup/WAL, device audio locali, identità Keycloak, log, archivi di fornitori di AI o futuri object store/connettori. Non è implementato un servizio di gestione delle eccezioni di legge, verifica identità rafforzata per azioni distruttive o audit di erasure persistente.

Legal analysis: GDPR art. 15 (accesso/copia), 17 (cancellazione ed eccezioni), 20 (portabilità, con campo di applicazione distinto), 19 (comunicazione ai destinatari). Fonti ufficiali: https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/ ; EDPB 2025 right-to-erasure coordinated enforcement report: https://www.edpb.europa.eu/system/files/documents/2026-02/edpb_cef-report_2025_right-to-erasure_en.pdf . L'export tecnico non costituisce da solo una risposta formalmente completa a tutte le richieste ex GDPR: il titolare deve verificare diritti di terzi, basi legali, obblighi di conservazione e procedure documentate.

## Retention e ruolo minimo di pulizia (#136, 2026-11-18)

La migrazione `0004_memory_retention.sql` registra `expires_at` per ciascuna nota (30 giorni da `received_at`, tempo del DB), includendo le righe storiche in modo da non estenderne la conservazione al momento della migrazione. `RecentAsync` nega la lettura via API delle righe scadute prima del purge. La cancellazione fisica è affidata a una stored function `public.omnia_purge_expired_memories(integer)` con testo SQL statico, `SECURITY DEFINER`, ricerca funzioni solo su schema trusted, limite 1..500 e `FOR UPDATE SKIP LOCKED`. È revocato `EXECUTE` da PUBLIC, `omnia_app` non lo possiede; il solo ruolo `omnia_retention` ha `EXECUTE` e **nessun SELECT/DELETE sui dati**. Il worker ha credenziale autonoma distinta da API e migratore, verificata a startup con fail-closed. L'API background service elimina fino a 5.000 note/ciclo, prima all'avvio e poi ogni 15 minuti; logga soltanto conteggi e tipi di errori, mai testo o identificativi di utente. Test SQL reali verificano permessi, scadenza, batch e altri tenant.

**Rischio residuo:** la funzione SECURITY DEFINER opera con privilegio di schema-owner, quindi la sua definizione, migration pipeline e i ruoli che possono alterarla sono codice ad alta fiducia. Le tre credenziali restano nello stesso processo API: separare il worker in workload/identità isolata su Azure. La pulizia da backup/WAL, le copie AI, i consensi, l'audit, il logging e le regole per finalità sono ancora non implementati. La scelta provvisoria di 30 giorni richiede una giustificazione documentata specifica (GDPR art. 5(1)(e)) e non implica che ogni tipo di dato debba avere lo stesso periodo.

## Envelope AES-256-GCM (prima base #137 per #33, non integrata nel DB)

`AesGcmEnvelopeEncryption` usa chiavi dati casuali (DEK) 256-bit per ogni payload, una KEK 256-bit per ID/versione, due nonce casuali distinti di 96 bit e tag di 128 bit per payload e wrapping. Il contesto AEAD usa codifica UTF-8 con campi delimitati da lunghezza per tenant, owner, finalità e record ID: un ciphertext non deve aprirsi con un altro soggetto, record o scopo. Il wrapping autentica anche il key ID, la versione del formato è esplicita e i dati manomessi vengono rifiutati. I buffer KEK/DEK temporanei vengono azzerati con `CryptographicOperations.ZeroMemory`; non loggare materiali crittografici, plaintext né ciphertext. Le chiavi vecchie devono rimanere disponibili sino alla conclusione e verifica di un rewrap di TUTTI gli oggetti; la sola modifica della key attiva non elimina la dipendenza dalle vecchie. Per file JSON locali usare mount sicuro fuori git, permessi minimi, no chiavi in variabili EXPO_PUBLIC, e non consegnare mai file di chiavi tramite assistente/pubblico/issue.

**Security boundary incompleto:** non esiste ancora una cifra applicativa dei dati in `memory_entries`, `consent_grants`, embedding pgvector, recording device, OAuth tokens o backup. La keyring da file è un adapter di sviluppo, NON un secrets vault con audit, rotazione, revoca, ACL e HSM. Senza un layer persistente cifrato, un dump PostgreSQL continua a contenere testo in chiaro. Necessari KMS/Key Vault ↔ OpenBao con identità workload, migrazione dei dati preesistenti, error handling dei key outage, design di ricerca semantica su embedding sensibili, purge di chiavi dopo restore con tombstone. Per fonti: OWASP Cryptographic Storage https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html e Key Management https://cheatsheetseries.owasp.org/cheatsheets/Key_Management_Cheat_Sheet.html , Microsoft .NET 10 AesGcm https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.-ctor?view=net-10.0 .


## Primary database: encrypted new note bodies only (slice #139)

The new note body (raw, normalized JSON, summary and tags) uses the #137 `AesGcmEnvelopeEncryption` with randomized per-object DEK and AEAD AAD binding to tenant/subject/canonical UUID, wrapped with a KEK version kept external to the database. SQL placeholders replace **all four** columns for new writes. Migration 0005 installs a CHECK constraint + trigger refusing unsealed new INSERTs, modifications of plaintext legacy payloads without encryption, and downgrade from sealed to plaintext. An idempotent retry retains stable canonical row ID and expiration; export/timeline authenticate and decrypt under actor scope/RLS. Key ring missing/unreadable/invalid => API fails startup; decryption or tampering => CryptographicException, never falls back to placeholder or legacy reader.

This **does not** encrypt embeddings, external identifiers, source/type, timestamps, old legacy rows, consents, backup/WAL, AI provider copies, audio nor logs. The SQL owner and privileged migration identity can still read old data and metadata. The file KEK adapter is not an HSM/vault and must have restricted mount access. Avoid interpreting this work as complete GDPR compliance or blanket encryption-at-rest.

References: https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html ; https://cheatsheetseries.owasp.org/cheatsheets/Key_Management_Cheat_Sheet.html ; https://www.postgresql.org/docs/17/sql-altertable.html .

## Global legacy backfill audit boundary (#140 / ADR 0013)

`omnia_count_legacy_memories()` is an exact, count-only SECURITY DEFINER
function with a controlled search path and revoked public EXECUTE.
Only a dedicated, non-owner, no-table-permission auditor can invoke it;
the API and data backfill worker are denied. The schema owner is
intentionally allowed RLS SELECT exclusively as a privileged migration
identity for FORCE RLS portability; never use schema-owner credentials
for regular HTTP requests. The operator-visible count is sensitive
operational metadata and must not be published. Zero is a point-in-time
check, not a release sign-off or a backup erasure receipt.
## Offline multi-actor backfill inventory boundary (#140 / ADR 0014)

An offline maintenance operator can provide an explicit local approved
actor manifest to the backfill CLI. The file contains sensitive tenant/
subject identifiers: use private permissions, keep it out of Git/logs,
audit access, and dispose of it under a defined policy after the task.
The `operatorApproved` boolean does not prove consent, lawful basis,
completeness or operator identity. Strict parsing and bounded processing
prevent accidental extra actors and unbounded processing. The CLI's
role guard denies API (INSERT/DELETE) and count-only auditor credentials.
The per-manifest zero count remains separate from the privileged global
audit: neither constitutes GDPR certification or backup erasure.



## Restore rehearsal threat boundary (#140 / ADR 0015)

Real pg_dump archives may contain legacy plaintext and unencrypted
metadata/embeddings despite the sealed write path. The CI uses
synthetic data only, temporary files, never artifacts, and a test-only
CLI rejecting remote/prod database targets. A same-cluster logical
restore does not prove cross-cluster roles, encrypted managed backups,
physical WAL/PITR, vault recovery, or deletion from backup replicas.
See [restore runbook](restore-rehearsal.md).


## Independent-cluster logical restore risk boundary (#109/#140)

CI now checks distinct PostgreSQL system identifiers across two
disposable pgvector containers. A source logical archive is copied
between them using private ephemeral files. Destination roles are
re-provisioned with new random secrets; original runtime password
must fail. Non-owner RLS, no-context query denial, encrypted memory,
historical KEK, export/TTL/vector and source/clone erasure isolation
are checked. The script cannot safely be used on any real dataset.
Neither the container cluster nor its secrets constitute an Azure DR
environment. Backup plaintext metadata/legacy may exist in an
archive. PITR/WAL, vault restore and backup retention/deletion
policies require separate design and tests; no regulatory readiness
assertions follow from this check. See ADR 0016.


## Physical PITR backup threat boundary (#109/#140, ADR 0017)

The new CI physical `pg_basebackup` and named WAL recovery gate tests
a synthetic encrypted record that is deleted only **after** the recovery
target on the source. A post-basebackup marker must be recovered, and
the later destructive mutation must be excluded. Restored credentials
are rotated because unlike logical backup, a physical backup copies
global role catalogs. Source and recovery stay on the same disposable
runner, and the disk copy and WAL are private temporary files, not
encrypted durable backups. The test does not establish compliant backup
storage, recovery from cloud vault, deletion from old snapshots, or
disaster recovery in Azure. No real data, vendor tokens or backups
may be fed to this script.


## PITR erasure receipt replay — test-only boundary (#31/#109/#125)

An authenticated HMAC receipt held outside the ephemeral PostgreSQL
backup simulates erasure events created after a physical restore point.
Only a hard-coded synthetic actor can be deleted by the offline harness.
Missing or altered MAC fails closed before erasure, replay is idempotent,
and the unrelated synthetic tenant stays isolated. The receipt is NOT
an independently authenticated, durable, complete, anti-rollback ledger.
Signing and replay happen on the same disposable runner without any
production traffic re-opening control. Without an external monotonic
high-watermark, lost events remain undetectable. Backup encryption,
vault-held signing keys, legal exceptions, audit, backups/WAL and
distributed erasure remain outside this scope; see ADR 0018.


## External file recovery gate, not signed journal (#141 / ADR 0019)

Non-Development API startup requires a private external
Recovery__AccessStateFile whose only accepted state is exact `ready\n`.
Missing, malformed, symlink or group/world-accessible files fail closed;
quarantine is checked again on EVERY HTTP request with 503/no-store.
The explicit Development-only no-file exception is for local tests.
The marker is **not cryptographically authenticated**, durable,
monotonic or proven independent of every recovered backup. A malicious
or mistaken operator may create a fake ready marker. Other workers,
in-flight requests, replicas and ingress must be fenced separately;
there is no end-to-end signed release/ledger yet. See ADR 0019 and #141.


## Quarantine and retention worker (#141 / ADR 0020)

The in-process `RetentionSweepService` now uses
`RecoveryGuardedRetentionCycle` to re-read the operator's external
recovery marker before every bounded PostgreSQL purge batch. Invalid,
missing or quarantined state stops **future** batches. An ongoing
SQL transaction may still commit after revocation (TOCTOU).
Non-Development still fails at API bootstrap without a private
ready marker. The privileged SQL routine and any offline/external
workers are NOT fenced at the database layer by this adapter.
No independent ledger or verifiable authorization to promote a
restored replica exists yet. See ADR 0020 and #141.


## Recovery journal integrity is NOT journal completeness (#141 / ADR 0021)

Read-only offline audit now checks per-record HMAC-SHA256 and hash
chain order against a separately authenticated checkpoint with a
distinct 32-byte HMAC key. Strict JSONL field and count validation,
bounded inputs, private-path requirements and no PII logs reduce
tampering/accidental release risks. A checkpoint is independent
ONLY if its custody, freshness and monotonicity are independently
enforced by an external system. Without that, attacker rollback of
both checkpoint and journal remains undetectable. No production
issuer, external journal store, durable anti-rollback authority,
proof of full erasure capture, replay or API traffic authorization
is implemented. The API recovery marker remains an unsigned
operator assertion. #141 stays OPEN.


## Local recovery journal append prototype (#141 / ADR 0022)

The new append-only prototype verifies the existing HMAC chain and
separately keyed checkpoint before every local write, creates one
new signed erase-actor event under an exclusive 0700 directory/0600
files, fsyncs the journal and atomically renames a same-directory
checkpoint temp. A crash between the append and the checkpoint
leaves an inconsistent pair and new writes fail closed. A leftover
lock also prevents continued writing until operator intervention.
Tests prove these properties with synthetic actors. This is NOT
a production deletion publisher: no durable external custody,
authoritative request identity, DB transaction/outbox, trust in
file paths against a privileged attacker, directory fsync, remote
witness, full host recovery, Azure/OpenBao or high-watermark service.
Both files can be rolled back together undetected. See ADR 0022.


## Erasure transactional outbox boundary (#141 / ADR 0023)

PostgresPersonalDataService now writes a minimal `erase-actor` row
in `public.erasure_outbox` before committing the actor-scoped
transaction that removes consent and memory. An outbox insert failure
must undo both deletions. Runtime can INSERT only four specified
columns with actor-match FORCE RLS; no runtime reads, updates, deletes
or timestamp control. Backfill/audit/retention identities cannot
read the table or issue events. This prevents the classic local
DB-delete-without-an-event crash window, not delivery loss after
commit. The outbox is in the restored PostgreSQL failure domain:
PITR can erase the record along with the historical deletion.
Actor IDs, event type and timestamps are personal metadata subject
to retention, data-rights handling and minimization review.
No publisher/offsite immutable ledger, signed high-watermark,
verified event provenance, request idempotency, vault custody or
release authority is provided yet. ADR 0023 and #141 remain gating.


## Isolated erasure dispatcher identity (#141 / ADR 0024)

An optional, explicitly provisioned PostgreSQL identity receives
EXECUTE only on a bounded SECURITY DEFINER erasure-outbox read
function. The function reads metadata across tenants, so the
identity is security-sensitive and has no direct table privileges.
All other runtime, retention, backfill and count-audit identities
must be denied. The test-only delivery bridge writes events to
the local signed journal preserving source UUIDs, and verifies
the existing checkpoint before each append; repeats with the
same actor are idempotent and conflicts fail closed.
CRITICAL: timestamp/id keyset is not commit-order and cannot
be used for a persistent ACK. The bridge scans from the beginning
on each invocation, with a hard cap. A privileged operator could
rollback both journal and checkpoint. There is no producer host,
independent trust-domain storage, durable delivery receipt,
vault-held keys, anti-rollback anchor or application release
authorization; #141 is OPEN. Actor IDs are personal metadata.



## Separate erasure witness PostgreSQL cluster (#141 / ADR 0025)

CI uses an independent PG17 database with a different
system_identifier and dedicated writer/reader credentials. Roles
only have EXECUTE on narrowly scoped SECURITY DEFINER functions;
neither can read or mutate raw witness tables. A transactional
singleton-row lock allocates commit-consistent local witness
sequence numbers and stable UUIDs deduplicate retries. The
integration test proves a synthetic event survives source outbox
row deletion, not offsite regional failure or source PITR before
delivery. A privileged witness owner can still restore/rewrite
its rows and its local sequence; there is NO trusted external
anti-rollback anchor, signature from legal erasure authority,
complete capture, remote backup, automatic publisher, or release
authorization. Actor IDs remain regulated personal metadata.



## Actual outbox event reconciliation after physical PITR (ADR 0026)

A synthetic CI scenario now commits a REAL EraseAsync on the primary
after a named WAL restore point, atomically producing the outbox
event. The separate PGDATA witness receives the event before PITR.
The physical restored primary contains resurrected personal memory
but NOT the post-target outbox record. The runner denies an invalid
witness reader and requires fixed actor and a bounded sequence scan
before replaying erasure under existing RLS and actor lock. Peer
isolation, ciphertext KEK failure, and empty export are verified.
This is NOT evidence that every deletion would reach the witness
before disaster, or that its database is immutable/offsite, nor
a production recovery release token or GDPR conformity assessment.

## CI-only autonomous erasure witness publisher (#141 / ADR 0027)

An independent offline .NET process now repeatedly scans the committed
source outbox and appends each bounded event through the restricted
witness writer. It explicitly rejects non-CI usage and anything other
than the fixed synthetic loopback test databases and roles. Both SQL
role guards are checked before dispatch. Failures are logged only as
generic pending retries; no personal actor data, SQL exceptions or
connection strings. Backoff never exceeds one minute. The stable event
UUID and locked witness append return a persistent witness sequence
after SQL commit; a repeated delivery or worker restart does not create
a second receipt. This ACK is durable only within the second CI
PostgreSQL instance; an uncertain network response causes safe replay.

There is no local persisted cursor/ACK, source-pruning, commit-order
watermark or independent source-event completeness proof. A crash after
primary erasure commit but before witness append can permanently lose
the deletion during PITR. Exceeding the 1000-event scan bound is
reported as an unsuccessful cycle and requires intervention; no
unbounded scans or data exposure. Privileged witness owners can still
roll back receipts, and both clusters share a single temporary runner.
No offsite WORM, vault signer, production worker, alert SLO, fully
coordinated quarantine/release, external erasure or GDPR readiness.

## Synthetic witness HMAC prefix checkpoint (#141 / ADR 0028)

A separately provisioned EXECUTE-only witness reader now obtains a
bounded read-only REPEATABLE READ snapshot, preventing mixed-page reads
during concurrent witness appends. The auditor rejects sequence gaps,
duplicate UUIDs, overflow and wrong SQL roles. A versioned SHA-256
hash chain over each event's UUID, actor scope and type is bound to
an HMAC-SHA256 checkpoint with a separate 32-byte key. The checkpoint
itself contains only count, digest and MAC; no raw actor identifiers.
Canonical JSON and fixed-time MAC comparison reject malformed,
forged or ambiguously encoded anchors. An older valid prefix is
accepted even if the witness has later events: the newer tail is
explicitly NOT audited by that checkpoint.

The CLI is offline/CI-only, with strict loopback reader identity,
private external files and create-new-only issuance. It never emits
a recovery-ready token. **It is not a production anti-rollback
authority:** checkpoint and key are ephemeral files in the SAME
CI runner as both PostgreSQL clusters. A privileged rollback of
witness plus checkpoint, compromised signing key, stale checkpoint,
or erasure lost before witness append remains undetectable.
There are no vault/HSM, offsite WORM, true independent monotonic
checkpoint, global traffic fence, complete third-party erasure,
legal basis/retention review, or production key rotation. See ADR
0028; #141 remains a blocker.

## Third-cluster witness checkpoint anchor — synthetic only (ADR 0029)

CI now checks distinct PostgreSQL system identifiers across primary,
independent witness and a THIRD anchor cluster. The anchor has no
plaintext actor IDs; it stores a bounded HMAC-protected canonical
checkpoint count/digest/MAC from ADR 0028. Restricted EXECUTE-only
reader/writer roles cannot SELECT or mutate raw anchor tables.
`anchor.advance_checkpoint` locks a singleton head to serialize
writers; count regression and same-count hash conflicts fail, while
exact retries remain idempotent. Each .NET adapter guards its own
login, denying owner/admin and wrong roles before any SQL operation.

The issuer first verifies the *previous* independently stored prefix
against a consistent witness snapshot using the external 32-byte HMAC
key; it refuses to "repair" a witness rollback by signing anew.
The auditor verifies an anchored prefix without changing primary,
witness, or API recovery readiness. Integration tests exercise direct
table denial, owner-credential rejection, conflicting reissue, and
truncated/tampered witness snapshots.

**Boundary:** all 3 clusters share one ephemeral Actions runner, and
the HMAC key is not protected by a vault/HSM. PostgreSQL owner can
rollback the anchor itself. A malicious writer can attempt false
checkpoint advances; without independent signer/key custody this
remains a DoS/trust risk. The test does not perform physical witness
PITR or prove offsite immutable/monotonic custody, complete source
outbox publication, traffic fencing across replicas, or GDPR erasure.
The #141 production gate remains OPEN.

### Anchor bootstrap must be explicit (ADR 0029 hardening)

A missing third-cluster anchor head is ambiguous after disaster.
`IssueAsync` now fails closed on absence; an explicit CI-only
`BootstrapAsync(operatorApproved: true)` must be invoked for first
synthetic initialization, rejects a preexisting head, and is NOT a
production authorization mechanism. Neither method promotes API
readiness. Operator approval/identity, independent monotonic custody
and recovery-release signature remain unresolved.

## Physical witness PITR detected by surviving third-cluster HMAC anchor (ADR 0030)

CI now uses a real PostgreSQL 17 witness physical basebackup and
archived WAL recovery to a named pre-erasure restore point. A restricted
writer appends a synthetic deletion after that point; a separately
stored HMAC checkpoint is advanced in an *unrestored* database on the
third PGDATA. Recovery of witness PGDATA on another loopback port
resurrects the older witness prefix. The test verifies WAL markers,
distinct anchor system identifier, matching physical clone identifier,
missing post-point receipt, rotation of the recovered reader credential,
and rejection by the read-only checkpoint verifier; the unmodified
live witness is accepted against the same checkpoint.

A successful negative verification MUST NOT advance or bootstrap a
checkpoint, authorize the `ready` marker or mutate the restored
source's actor data. The test CLI is CI-only and restricted to fixed
127.0.0.1 ports/database names, an approved synthetic erasure, a
private temporary HMAC key and independent EXECUTE-only SQL logins.
Witness/anchor bytes and backup artifacts must not be logged or
published.

This is an actual PITR **of the witness** (previously only simulated
in-memory), but all three clusters share one disposable CI host and
admin domain. Attacker rollback of both witness+anchor, key
compromise, offsite disaster, primary erasure before first ACK,
in-flight workers/replicas and external provider data remain
unresolved. It proves neither source journal completeness nor GDPR
Art. 17 compliance or safe release of production traffic.

## Live outbox-to-witness read-only receipt reconciliation (ADR 0031)

The new `PostgresErasureLiveDeliveryAudit` requires the EXECUTE-only
primary dispatcher AND a separate witness reader (never owner, app,
writer or BYPASSRLS). It scans all currently committed source outbox
events in one bounded `REPEATABLE READ, READ ONLY` transaction and
looks up matching independently persisted receipts. The audit denies
missing, duplicated, overflowing, unknown or actor/type-conflicting
events. A known-event audit can be performed without mutating either
store; no source ACK/cursor exists or is moved.

The test-only `Omnia.ErasureDeliveryAudit --all` is disconnected
from API traffic/recovery readiness, accepts only fixed 127.0.0.1
CI connection roles, fails closed on missing opt-in or mismatch and
does not log SQL details or actor metadata. Post-publish CI verifies
all source-visible committed erasures have a witness receipt.

This is a useful operational lag/scope check **only while the
original primary source outbox still exists**. The source snapshot
is not an independently durable commit watermark; concurrent commits
after the snapshot are out of scope. A PITR that rolls back unacked
source commits can make the apparent backlog empty. Therefore this
does NOT close the crash-before-ACK, anti-rollback, production global
fencing, WORM/offsite signer, vault or GDPR obligations of #141.

## Pre-ACK primary PITR: a false-green source-only delivery check (ADR 0032)

The real PostgreSQL 17 physical primary PITR rehearsal now covers
TWO erasures on distinct synthetic actors: one delivered to the
independent witness, and one committed AFTER the final publisher
cycle and NEVER delivered. The second actor's memory and consent
were included in the earlier physical basebackup. Its committed
primary outbox UUID is observed live before PITR with a remote
witness receipt proven ABSENT. Named-WAL-point recovery resurrects
that actor's ciphertext and consent but removes the primary UUID;
the witness still has no matching event.

The new isolated `Omnia.UnwitnessedPitrRehearsal` test CLI
keeps only the synthetic UUID in a private ephemeral runner file,
requires CI and exact restricted 127.0.0.1 SQL reader identities,
and performs NO writes or release operations. It shows how the
existing LIVE-source `VerifyAllAsync` may pass on other witnessed
events on the restored primary while a previously committed erasure
is missing from BOTH source and witness. Recovered dispatcher
credentials are rotated; the stale password and witness-writer
role are rejected. API quarantine must remain active after this
misleading clean scan, and replay for another witnessed actor must
not erase the unacknowledged actor automatically.

**Trust boundary:** a UUID file on the SAME CI host is not a
signed independent per-commit journal, has no source coverage
guarantee and can be lost or forged. Even an HMAC witness anchor
cannot authenticate an event that never reached the witness.
No independent source commit authority/offsite WORM, global release
fencing, crash-atomic cross-cluster protocol or production GDPR
data-erasure guarantee exists. #141 remains OPEN P0.

## Independent pre-erase intent, distinct from committed ACK (ADR 0033)

A new CI-only, second-PGDATA `witness.erasure_intents`
table separates a **request/intention before a primary
erase** from post-commit `witness.erasure_events`.
Only narrow EXECUTE-only intent writer/reader logins can
access their dedicated SECURITY DEFINER functions.
The intent writer cannot write a post-commit receipt and
the existing completed witness writer cannot forge an
intent. PostgreSQL FOR UPDATE serializes an idempotent
UUID/tenant/subject request sequence; SQL owner/admin
and direct table permissions fail inside .NET adapters.

The physical PITR rehearsal now records a distinct
intent UUID BEFORE committing a synthetic source erase.
Wrong SQL identity and absent explicit test opt-in
fail before the source actor is touched. When the
primary PITR loses its committed deletion/outbox and
resurrects data, the independent witness intent still
exists and is readable. The completed witness erasure
receipt still does NOT exist, and the API remains
quarantined. This supports a conservative investigation
signal, not a completed-delete claim or permission to
replay arbitrary actor deletions.

The intent may exist when source erase never committed;
a missing intent may reflect a lost request or rollback
of witness itself. Source API is NOT wired to require
this preflight, and synthetic CI secrets are not vault
credentials. No signer identity, externally immutable
intent retention, monotonic third-cluster anchor for
intent, crash-atomic two-DB transaction, global fencing
or GDPR readiness is provided. #141 remains OPEN.

## Opt-in correlated pre-intent erasure request (ADR 0034)

An explicit CI-only `EraseWithWitnessedIntentAsync` checks
the independent role-gated witness pre-intent and exact UUID,
tenant, owner, operation before any actor RLS transaction.
The DELETE of consent and sealed memory and primary outbox
INSERT with **the same request UUID** occur atomically.
The existing publisher preserves the UUID in the completed
witness receipt. Invalid remote credentials, absent intent
or foreign tenant/subject are denied without source mutation.

The source outbox primary key is also a stale-request guard:
a second use of the same request ID cannot delete a later
re-grant/capture; the PostgreSQL unique violation 23505
rolls back all earlier DELETEs in that transaction.
Do not treat a replay as idempotent erasure of future data,
swallow PK errors or automatically retry ambiguous commits.
The normal `EraseAsync` API method remains unchanged.

The CI physical PITR deliberately loses a committed
pre-ACK DELETE despite the matched remote intent UUID.
After recovery the same remote request exists but cannot
prove commit, and the API remains quarantined. This
experimental method is explicitly blocked outside
GitHub CI; it is NOT wired to OIDC/JWT/HTTP. The intent
role is an SQL capability, not an authenticated GDPR
erasure request or signer. The third checkpoint doesn't
yet anchor the pre-intent stream, all clusters share
the CI runner, and immutable offsite custody, global
fencing and complete provider/device erasure remain open.

**Source PITR caveat:** duplicate UUID protection relies on the original outbox row. Once WAL PITR removes it, the request may appear new again in the recovered primary. Do NOT auto-retry the pre-intent or use successful SQL execution as proof of historical authorization; global recovery quarantine and independent commit provenance remain mandatory.

## Three-way correlated erasure evidence classifier (ADR 0035)

A CI-only read-only inspector independently verifies the SQL capability
of the source `omnia_dispatcher`, remote `omnia_witness_intent_reader`,
and remote `omnia_witness_reader`. Intent UUID and scope MUST match both
the source outbox event and the witness completed receipt when visible.
Malformed intent, missing authorization to read, inconsistent actor/type,
privileged connections, or source scan overflow are denied. The source
snapshot is bounded and REPEATABLE READ READ ONLY; witness reads happen
in a different PostgreSQL domain and are not transactionally coherent
with source, so no result is a historical commit attestation.

State is only a finite read-only observation: intent-only
(commit unknown, including source PITR), source pending remote receipt,
source+witness visible, or witness visible without source. ALL return
`MayReleaseRecovery=false` and `MayAutoReplay=false`, including fully
matching visible source+witness. No data mutation, user-facing endpoint
or recovery marker is issued. Only the disposable CI runbook invokes
this code. A live incident still needs independently authenticated
source commit custody and a global ingress/worker/replica fence before
considering any actor replay or traffic release.

## Intent-prefix HMAC anti-rollback rehearsal (ADR 0036)

A distinct HMAC domain (`pre-erase-intent-prefix`, independent
genesis, canonical JSON and secret key) now protects a bounded,
contiguous prefix of the independent witness's pre-erase intents.
The read-only source snapshot is PostgreSQL REPEATABLE READ,
READ ONLY with separate EXECUTE-only identity and 10000-event
plus one-row overflow detection. The THIRD PGDATA stores only
the count, prefix digest and MAC in a SEPARATE anchor database,
not the witness actor metadata; its SQL head advances only
monotonically and rejects conflicting equal-count reissue.
No missing/unverifiable anchor may silently be replaced.

The CI physical witness PITR now deliberately rolls back an
intent added after a named WAL recovery point while retaining
its signed prefix on the third cluster. The recovered intent
reader login is rotated and its old credential is denied;
the read-only verifier rejects the cloned history and still
accepts the live witness. The checkpoint count and exact
bytes in the third DB are unchanged by recovery. This is
NOT a proof of source DELETE commit or user authentication,
not an immutable offsite high-watermark and not a license to
release API/worker traffic or auto-replay erasure. New intents
remain vulnerable before being signed/anchored. All clusters
share CI admin and failure domain; #141 stays OPEN.

## Signed pre-intent high-watermark as a synthetic PRE-DELETE gate (ADR 0037)

A new opt-in `EraseWithAnchoredIntentAsync` path rejects a
remote pre-erase intent unless it is included in the
previously authenticated, monotonic HMAC prefix stored
in the THIRD PostgreSQL database, not just appended to
the witness's SECOND PGDATA. The verifier reads the
exact UUID+tenant+subject from the SAME
`REPEATABLE READ READ ONLY` snapshot used to validate
the full prefix, and requires sequence <= anchored count.
A missing anchor, wrong 32-byte key, invalid/tampered
payload, wrong EXECUTE-only identities or an unanchored
suffix returns denial BEFORE the source actor lock,
DELETE or transactional outbox insert.

The physical primary WAL PITR drill now asserts a
rejected DELETE after intent append but BEFORE
independent anchor acknowledgement, then a successful
correlated source DELETE only after the independent
HMAC checkpoint is acknowledged. The test proves no
source memory/outbox mutation on denial, protects the
HMAC key in private CI temporary files and checks the
independent checkpoint is identical after restoring the
primary. The post-commit witness receipt is intentionally
absent, so source-commit provenance remains UNKNOWN
after physical PITR and the API remains quarantined.

**Trust boundary:** `CI=true` and `GITHUB_ACTIONS=true`
are only test-mode guards, not identity proof or real
permission controls. The weaker prior CI-only
`EraseWithWitnessedIntentAsync` continues to exist
for older tests; the public API remains UNCHANGED and
does not enforce anchored preflight. Multiple PGDATA
still share admin/host/runner; external WORM, signer
custody, authenticated real requester, global
ingress/API/worker/replica fence and cross-store GDPR
rights are not implemented. The source UUID PK
does NOT protect retries after physical rollback.

## One-shot pre-erase admission on third PostgreSQL PGDATA (ADR 0038)

A CI-only `EraseWithOneShotAnchoredIntentAsync` first validates
the exact UUID, tenant, subject, type and prefix MAC of an
already-anchored witness pre-intent, then obtains a strictly
non-repeatable attempt claim in a DIFFERENT database on the
THIRD PGDATA *before* touching the source actor transaction.
The narrow SQL writer can only EXECUTE an insert-once function;
a separate reader can only EXECUTE a boolean membership check.
SECURITY DEFINER functions and PUBLIC EXECUTE revocation share
one transaction. A domain-separated HMAC stores no raw
actor/tenant in the claim table; UUID/digest nevertheless
remain personal metadata in privacy scope.

Concurrency for the SAME UUID serializes via the PostgreSQL
unique primary key: exactly one insert may commit; every
duplicate, including after a primary-only PITR, raises
23505. Once claimed, the request is *permanently uncertain*
until external reconciliation: losing the primary commit,
failure before commit or timeout after claim all require
investigation and MUST NOT lead to automatic retry/re-delete.
A recovered primary without the old source outbox must
still consult this independent attempt claim. The test
checks this against a real physical WAL PITR and denies
re-deletion of resurrected memory/consent.

**Trust and availability caveats:** any caller holding the
claim-writer capability can pre-consume a UUID and cause
denial of service; this is not an OIDC consent signature.
If the THIRD PGDATA, its keys or administrators roll back
or become compromised, the one-shot property can be lost.
The public API retains its existing behavior; no external
WORM, separate host, vault/HSM, cross-DB atomicity, global
ingress/worker/replica fencing, source commit attestation,
GDPR completion or automatic recovery release is provided.

## Exact actor-scope MAC for one-shot attempt readback (ADR 0039)

A bare `HasClaimAsync(requestUuid)` is not enough to
establish which tenant/owner was admitted before a source
DELETE. The restricted third-cluster reader now executes
only `admission.claim_scope(uuid)` and recomputes the
EXACT HMAC-SHA256 over version, purpose, UUID, tenant
and owner, using the same single canonical serializer as
the writer. Missing, tampered or foreign-scope entries
and a wrong signing key fail closed with
`InvalidDataException`; wrong SQL identity fails
with `InvalidOperationException`. HMACs are compared
in constant time; transient bytes are zeroed.

SECURITY DEFINER `claim_scope` is created and its
default PUBLIC execute revoked in the same transaction;
the function has trusted `search_path` and returns only
`bytea`. Writer has no EXECUTE read and neither
role may directly SELECT or UPDATE the claim table.
The CI test demonstrates that a privileged TEST database
administrator CAN mutate a stored MAC, but the restricted
verifier rejects the substituted value. This is not
tamper-proof custody against a privileged adversary
with the HMAC key.

The physical primary PITR test checks authenticated
scope and negative foreign scope both before and after
recovery, still rejecting reuse of a one-shot UUID.
A valid claim is evidence of *admission* only, NOT of
a source DELETE commit, OIDC user authority,
cross-store erasure or a production DR release. All
PGDATAs and keys remain on one ephemeral CI runner;
offsite WORM, vault/HSM, global fencing and GDPR
review remain open.

## Fourth-PGDATA one-shot anti-rollback, third-PGDATA physical PITR (ADR 0040)

The synthetic CI-only fourth fence is an additional
EXECUTE-only attempt ledger with its own physical PGDATA,
system identifier and SQL credentials on loopback:15440.
An HMAC-anchored pre-intent is validated first, followed
by a non-idempotent fourth claim, then the third claim,
then the primary transactional actor erase. The fourth
claim survives a rollback of the third PGDATA alone and
denies repeat UUIDs with 23505. A stranded fourth claim
is a fail-closed uncertain attempt, NOT evidence of
source COMMIT or lawful authorization.

A real `pg_basebackup`/archived WAL PostgreSQL 17 PITR
regression creates a restored third clone before a
post-target claim while fourth PGDATA stays untouched.
The clone has no matching claim, while authenticated
fourth reader verifies exact synthetic tenant/owner.
A duplicate fourth claim fails; recovered reader
credentials are rotated, and inherited credentials
are denied. No deletion or API release is triggered.

**Threat model limitation:** all four PGDATAs and keys
share a disposable GitHub-hosted CI runner and an
administrative/failure domain. There is no remote
geographically independent immutable authority,
synchronous cross-cluster commit or signed source
COMMIT attestation; an attacker with access to the
fourth admin/key can rewrite its history, and timeout
after fourth claim causes indefinite uncertainty.
No OAuth/OIDC real-user authorization, global API/
worker/replica/ingress fence or cross-provider GDPR
deletion has been integrated. This is not production
readiness or a `ready` certificate.


## #141 — Third-survivor when FOURTH claim PGDATA is physically rolled back (ADR 0041)

La regressione CI ora ripristina fisicamente la QUARTA authority
mentre la TERZA sopravvive. Verifica UUID e HMAC, sistema PostgreSQL,
marker WAL pre/post restore point, rotazione dei login ereditati e
rifiuto del secondo tentativo dal TERZO tramite UNIQUE `23505`.
Il clone QUARTA può aver già riaccettato la richiesta: non equivale
ad authorization né prova di DELETE. **Non estende la protezione a
rollback simultaneo di TERZA+QUARTA**, errore incerto del COMMIT
primario, attacco allo stesso runner/amministratore, compromissione
del key material, provider/device/media/backup, o recovery dell'API.
Nessuna fiducia offsite WORM, attestazione source o OIDC viene
aggiunta. Rimangono le issue di privacy/security P0.

## #141 — Dual-claim authority common-mode rollback (ADR 0042)

Lo scenario CI ripristina TERZA e QUARTA alla stessa
fase pre-claim e prova che i due writer ristretti
riaccettano il vecchio UUID. Il risultato positivo
del test è una **segnalazione di vulnerabilità**:
entrambi i vincoli `23505` non bastano se entrambi
gli archivi vanno persi. Il processo DEVE terminare
con stato fail-closed exit 42 e quarantena;
nessun DELETE o apertura dei servizi. Rotazione
credenziali e scope HMAC non costituiscono
immutabilità, non sostituiscono un high-watermark
offsite e non provano il commit di cancellazione
sul primario. #141/109/31/32 OPEN.

## #143 — External immutable-attempt evidence read contract (ADR 0043)

Il verifier impone due chiavi HMAC da 32 byte, distinte,
JSON canonico e bounded, scope attore esatto, expected
store identity/immutable version ID, policy S3 COMPLIANCE o
Azure LOCKED e retention UTC non scaduta. Missing/error,
GOVERNANCE, versione differente o MAC alterato: deny.
**Forte limite:** un reader fake può mentire su WORM/retention;
né enum né chiave HMAC certificano indipendenza amministrativa.
Occorrono credenziali workload identity, IAM/RBAC e version
pin esterno, clock sicuro, test veri Object Lock su provider
e una ricevuta COMMIT distinta. Non aprire recovery o
abilitare DELETE sulla base di `ImmutableAttemptObservation`.

## S3 real Object Lock read adapter — #145 / ADR 0044

L'adapter verifica modalità COMPLIANCE e retain-until
per lo stesso `VersionId` richiesto al backend,
con identità del bucket configurata; le dimensioni
sono limitate anche sullo streaming. Chiavi HMAC/tenant
e stato osservativo restano nel verificatore ADR0043.
La rehearsal usa SeaweedFS su un runner CI temporaneo:
non è prova WORM resistente ad admin host,
non è prova di independent offsite custody, IAM minima,
account owner esterno, conservazione GDPR o fonte
temporale affidabile. Per AWS richiedere
`ExpectedBucketOwner`, S3 IAM GetObjectVersion e
GetObjectRetention; on-prem impone un dominio di
amministrazione realmente differente. No REST wiring.
