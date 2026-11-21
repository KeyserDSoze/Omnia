# Export e cancellazione personali — prima slice di #31

## Stato e perimetro

**Implementato:** download dei dati conservati oggi dal **database PostgreSQL primario** di Omnia e cancellazione delle righe personali in `memory_entries` e `consent_grants`. **Non implementato:** servizio completo per diritti GDPR e deletion propagation su archivi esterni/backups.

Le API non permettono a un chiamante di scegliere il tenant o l'owner: sono sempre derivati dai claim `tenant_id` e `sub` di un access token validato via OIDC/JWKS, e RLS è imposta anche a livello PostgreSQL.

## GET /api/v1/privacy/export

- JWT valido obbligatorio; rate limit per tenant+subject.
- Stream attachment `application/json`, `Cache-Control: no-store`, `Content-Disposition: attachment; filename=omnia-personal-data.json`.
- `schemaVersion=1`, `scope=omnia-postgresql-primary`, `exportedAt`, `tenantId`, `subject`, `memoryEntries[]` e `consentGrants[]`.
- Ogni memoria riporta id, source/externalId, contentType, rawContent, normalized JSON, summary, tags, occurredAt, receivedAt, embedding array (o null), embeddingModel (o null). Ogni consenso include id, source, purpose, policyVersion, grantedAt, expiresAt, revokedAt.
- Tutte le righe dell'attore corrente, senza limiti di pagina. `PostgresActorLock` mantiene il snapshot operativo rispetto alle scritture dell'API; `Utf8JsonWriter` limita il buffering server-side, ma il client Web costruisce un Blob intero prima del download (ottimizzazione successiva per archivi grandi).
- Il file contiene dati privati in chiaro: custodirlo, non condividerlo né caricarlo su tracker/issue/CI.

## POST /api/v1/privacy/erase

```json
{"confirmation":"ERASE_MY_DATA"}
```

- Un'altra stringa oppure valore assente viene respinto (400); senza bearer valido (401), con token privo di subject/tenant univoco (403). La UI prevede una spunta esplicita e la parola `ELIMINA` prima di inviare la richiesta.
- Sotto unica transazione RLS, con advisory lock del tenant+subject condiviso con grant/revoca/ingest/export, il server elimina **prima tutti i consensi e poi tutte le memorie** del soggetto, inclusi embeddings e campi derivati inline.
- Risposta 200 con `deletedMemories`, `deletedConsents`, `completedAt`, `scope=omnia-postgresql-primary` e `backupAndExternalPropagation=not_included`. Seconda chiamata produce conteggi zero; non si cancella l'account OIDC e si può, con un nuovo grant esplicito, ricominciare a salvare.
- Nessuna ricevuta duratura GDPR o audit immutabile è ancora disponibile; non interpretare la risposta come prova di erasure da sistemi non inclusi.

## Concorrenza e sicurezza

Tutte le scritture personal-data in API corrente, incluso consenso e capture, acquisiscono il medesimo `pg_advisory_xact_lock` per attore prima di agire. La cancellazione si serializza con richieste in volo. Un'operazione AI già inviata prima della revoca/erasure non può essere cancellata dal provider con questa operazione. Il DB impone RLS per tenant+subject in tutte le query, oltre ai filtri di repository. L'export è in sola lettura, ma può essere avviato durante richieste di cancellazione: il lock ne definisce l'ordine di completamento.

## Gap obbligatori prima di un reale trattamento dei dati

1. Inventario di ogni fonte di personal data oltre PostgreSQL (device audio, upload, object storage, cache, log, backup, external AI/provider, Keycloak, futuri connector), cancellazione e report di esito di ogni copia/replica.
2. Backup purge e controllo dei ripristini con tombstone/replay dell'erasure; gestire scadenze e obblighi di conservazione senza promettere eliminazione fisica immediata.
3. Job asincroni durevoli, idempotenti con retry, verifiche, audit minimizzato e DSAR workflow; verifica/step-up identità per richieste distruttive ad alto impatto.
4. Valutazione documentata delle eccezioni art. 17 GDPR, diritti di terzi, finalità, basi giuridiche, policy retention e obblighi del titolare.

Riferimenti ufficiali: Regolamento UE 2016/679 artt. 15, 17, 19, 20, https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/ ; EDPB report 2025 right to erasure, https://www.edpb.europa.eu/system/files/documents/2026-02/edpb_cef-report_2025_right-to-erasure_en.pdf .

## Test

- `tests/Omnia.Integration.Tests/PersonalDataTests.cs`: pgvector PostgreSQL reale con runtime non owner, export con embedding e grant history, isolamento same-tenant/cross-tenant, erasure idempotente, nessuna riacquisizione senza nuovo grant.
- `src/mobile/e2e/auth-sync.spec.ts`: provider Keycloak reale, JWT autentico, UI conferma e download, verifica di cancellazione e persistenza delle note dell'altro utente.
- `tests/mobile-unit/web-cloud.test.ts`: no cookies/redirects nella fetch, error handling e body di conferma.

## Retention primaria delle note (#136)

Le note hanno `expires_at` a 30 giorni da `received_at`. In timeline non si vedono oltre scadenza; il purge automatico cancella dal database principale raw, normalized, summary e vettori. Finché una riga non è stata fisicamente rimossa, l'export include anche i suoi dati e il timestamp di scadenza, a tutela della completezza dell'accesso. Se un utente elimina l'intero account dal DB via API, la cancellazione immediata #135 prevale senza attendere lo scheduler. Nessuna garanzia di cancellazione istantanea alla scadenza; i cicli sono ogni 15 minuti e si fermano durante outage, con backlog eventualmente limitato a 5.000 record per ciclo.

Questo non determina il periodo adeguato per dati di origine diversa né elimina duplicati su device, backup, log o provider. I consent grants mantengono uno storico separato e non vengono eliminati dal purge note.


## Export e cancellazione di note sealed (#139)

Le note nuove salvano i campi `rawContent`, JSON normalizzato, summary e tags in un blob autenticato cifrato. L'export JSON decifra **solo dopo** verifica JWT e RLS e indica `storageProtection: "sealed-v1"`. Le vecchie note ancora non migrate sono esportate con `storageProtection: "legacy-plaintext"`. La cancellazione immediata e la retention eliminano la riga `memory_entries` completa, inclusi il blob cifrato e il vettore eventualmente presente. La cancellazione non rimuove backup, provider AI né altri sistemi esterni; vecchie note persistono in chiaro sino al backfill della #138.


## Eventi di cancellazione nella outbox primaria (#141 / ADR 0023)

L'endpoint `POST /api/v1/privacy/erase` ora scrive anche un evento
`erase-actor` nella tabella `public.erasure_outbox`, nello stesso
COMMIT dei DELETE di `consent_grants` e `memory_entries`.
Se non è possibile scrivere l'evento, nessuna delle cancellazioni
viene confermata. La tabella contiene UUID dell'evento, tenant ID,
subject ID, tipo e data/ora server; NON il contenuto delle note.
Il login applicativo dispone solo di INSERT su quattro colonne,
con RLS obbligatoria: non può leggere, aggiornare, cancellare
o alterare il timestamp delle righe di outbox.

**Questa outbox contiene comunque dati personali identificativi.**
L'export attuale v1 espone soltanto `memoryEntries` e `consentGrants`,
non le ricevute della nuova tabella. L'endpoint di erase non rimuove
i record di outbox, per evitare di perdere eventi prima di una
futura pubblicazione. Sono necessari inventario DSAR, base giuridica,
retention minima, procedure di accesso/cancellazione documentate e
decisioni conformi alle eccezioni GDPR applicabili.

Un retry esplicito che non trova più righe genera un nuovo evento:
manca la chiave di idempotenza associata alla richiesta. L'outbox
è dentro il database primario e può scomparire ripristinando un
backup antecedente al COMMIT; non è un journal esterno e non
dimostra replay/tombstone su provider, device, storage o backup.
Vedi [ADR 0023](../adr/0023-transactional-erasure-outbox.md).


## Dispatch della outbox di cancellazione (ADR 0024)

Un login separato dai ruoli di API, retention e backfill può
ottenere, **solo dopo opt-in esplicito dell'operatore di migrazione**,
permesso `EXECUTE` su una funzione SECURITY DEFINER che espone
per batch al massimo 100 metadata: UUID evento, tenant, subject,
tipo `erase-actor` e timestamp. Nessun payload di memoria.
La funzione permette lettura globale dei metadata personali,
necessaria per l'eventuale delivery cross-tenant: va custodita
come una credenziale sensibile e sottoposta a minimizzazione,
audit, monitoraggio e retention specifici.

Il test locale mostra consegna degli eventi confermati dall'API
a un journal HMAC con UUID stabile e retry idempotente.
Non è ancora in esecuzione un worker di pubblicazione verso
un sistema esterno, né sono garantiti completezza, custodia
indipendente, deduplica persistente o cancellazione da altre copie.
Come precisato in ADR 0023, gli ID di outbox non rientrano
ancora nell'export personale v1; non dichiarare la gestione
DSAR e art. 17 completata.



## Separate recovery witness — only CI (ADR 0025)

A new synthetic CI test delivers a committed erase event from
the source PostgreSQL outbox to a second PostgreSQL cluster.
The witness retains UUID, tenant, subject, type and a local
append sequence. Tenant/subject identifiers remain PERSONAL DATA
even without memory text or embeddings. This is NOT a live
production service or a compliant deletion receipt. There is
no reviewed legal basis, vault key custody, data rights/DSAR
procedure, retention, exporter, provider erasure or geo-independent
availability for these records. #31, #32 and #141 remain OPEN.



## Post-PITR recovery of erased actor (ADR 0026)

A test-only physical restore now checks the same synthetic consent,
memory and outbox erasure that was committed on the source and
independently witnessed before the PITR. A restored copy lacks the
event and resurrects the actor's note until the constrained witness
replay deletes it via RLS/actor lock. A peer actor remains unaffected.
This does not ensure delivery of erasures prior to system failure,
nor erase backups, provider stores, local devices, caches or
analytics, nor establish GDPR completeness, legal basis, retention
of witness metadata, or a signed API release decision.

## Outbox-to-witness automatic retry — synthetic CI only (ADR 0027)

A separate test-only CLI now retries delivery of already-committed
erasure-outbox events to a second PostgreSQL instance. The witness
stores receipt UUID/sequence with tenant and actor metadata: those
identifiers remain personal data and their retention, lawful basis,
access, export and deletion/exception policy are still unresolved.
Retry and idempotency do not prove that every deletion was transmitted
before a disaster. The primary and witness are not an independent
WORM/offsite ledger. No GDPR art. 17 or backup-erasure completion claim,
no automatic release of the recovery quarantine, and no real-person
data handling is authorized by this implementation. #31/#141 OPEN.

## Witness prefix checkpoint — CI-only (ADR 0028)

The offline test checkpoint contains count plus authenticated hashes,
not plaintext tenant or owner identifiers. Its digest is still derived
from personally identifying metadata held by the PostgreSQL witness;
pseudonymization and HMAC do not automatically remove obligations
for storage limitation, access control, erasure and lawful basis.
The witness retains tenant/owner metadata with no implemented
retention/export/erasure exception policy, and the new checkpoint is
not a legal erasure receipt. Both PostgreSQL databases and the
checkpoint files are on a disposable CI host. No proof of all
deletions delivered before PITR or of GDPR Article 17 end-to-end
completion; #31/#32/#141 remain OPEN.

## Third independent checkpoint SQL cluster (ADR 0029)

The synthetic third PostgreSQL database only stores checkpoint
count and authenticated digest/MAC bytes; it does not store plaintext
tenant/subject identifiers. Those digests are still derived from
personal metadata and may remain regulated/pseudonymous information.
The witness still stores actor identifiers and has NO completed
retention/export/erasure and legal-exception design. Database and
checkpoint key are confined to synthetic CI. This proof does not
establish lawful basis, storage limitation, or exercise of GDPR
Article 17 across backups, witness, provider data or devices.
#31/#32/#141 remain OPEN.

## Witness physical PITR with separate saved HMAC checkpoint (ADR 0030)

The restore rehearsal operates on a single synthetic fixed actor and
checks that a witness receipt created after a WAL restore point is
missing in the recovered physical witness clone while the third
cluster retains its HMAC checkpoint. The checkpoint stores count,
digest and MAC, not raw subject/tenant. This is a technical rollback
detection proof; no real personal data, user authorization, provider
deletion, backup retention policy or legal exception for retained
witness metadata has been validated. Three PostgreSQL PGDATA and the
key still share a temporary CI machine. #31, #32, #109 and #141 stay
OPEN; no declaration of GDPR erasure completion.

## Live erasure delivery audit is NOT GDPR completion (ADR 0031)

The read-only preflight compares a synthetic committed primary
deletion UUID and actor scope to the witness receipt; it persists
no additional actor metadata or new source ACK. Its CI output
contains only generic success/denial, never tenant/subject IDs.
The original primary outbox and witness still retain personal
metadata under unresolved retention, export, correction/deletion
exception and lawful-basis policies. The preflight cannot account
for an erasure commit that was lost by primary PITR BEFORE the
first remote witness receipt. This is not GDPR Article 17 end-to-end
assurance; #31/#32/#141 remain OPEN.

## Unwitnessed committed erasure lost during primary PITR (ADR 0032)

The new test demonstrates an important Article 17 failure mode:
a real synthetic `EraseAsync` can have committed successfully,
including deletion and outbox, while physical rollback resurrects
the actor's sealed memory and consent and loses the outbox UUID
BEFORE independent witness acknowledgement. The existing
source-only delivery audit can then appear clean because it sees
only surviving rows. The scenario uses no real user data; test
output contains only generic result tokens, and its local private
UUID evidence is destroyed with the runner. No lawful-basis,
notification, device/provider purge, retention, backup deletion
or independent authorization proof has been established.
#31/#32/#141 remain OPEN; do not claim GDPR compliance.

## Pre-erase intent provenance and GDPR limits (ADR 0033)

The synthetic independent intent ledger stores request
UUID, tenant and subject before the primary erase.
These values are personal metadata even though no note
text is stored. They are accessed by separate
EXECUTE-only PostgreSQL accounts and restricted to
disposable CI. An intent record might exist even if
the requested erase never commits; it MUST NOT be
reported as a completed GDPR Article 17 action or
used to delete a real user without independent
authorization. No retention period, right-of-access,
rectification, exception handling, data controller
sign-off or multi-provider erasure propagation has
been verified. All such requirements stay OPEN
(#31/#32/#141).

## Correlation does not confer GDPR erasure authorization (ADR 0034)

The synthetic CI path now uses the same UUID for a
pre-erase witness intent and a source transaction's
outbox event. Tenant and subject metadata are validated
through restricted PostgreSQL roles. This removes
ambiguity **within the test** about which source event
was intended, but it does not identify the legal
requester, prove consent or lawful basis, or certify
that a DELETE committed after a crash/PITR.
The public HTTP/API erasure endpoint still uses its
existing behavior and has no production external
intent handshake. No new user data may be processed
by the CI-only operation. Retention, access/export
and deletion of intent metadata, providers/backup/
device propagation and GDPR Art.17 review remain
unresolved in #31/#32/#141.

## Classificazione visibilità erasure non equivale a diritto all'oblio (ADR 0035)

Il nuovo verificatore CI legge in memoria request UUID, tenant,
subject e i soli metadati di delete già conservati nei registri
pre-intent/outbox/witness, senza creare nuove tabelle o retention
e senza stamparli nei log. Può descrivere l'osservazione in
quattro stati tecnici, ma NON certifica autenticazione OIDC,
consenso/base giuridica, completamento del DELETE,
cancellazione dei backup e dei provider o diritto di accesso
ai metadati conservati. In particolare pre-intent presente
senza source outbox dopo PITR significa **commit sconosciuto**:
non significa cancellazione mai avvenuta. Nessuno stato
autorizza replay automatico o riapertura dei servizi.
#31/#32/#141 restano OPEN.

## Third-cluster intent checkpoint contains no direct actor metadata (ADR 0036)

The separate synthetic HMAC intent checkpoint stores count,
prefix SHA-256 digest and HMAC only, with no plaintext
tenant, owner, UUID or note fields. The canonical digest
is computed from those fields on the SECOND PGDATA:
reduced exposure is not anonymous by declaration and its
retention, access, erasure and legal bases still require
GDPR Article 5/17 examination. The test detects rollback
of an ALREADY ANCHORED pre-erase request, not the
authorization, performance or legal finality of a DELETE.
All storage and keys remain on a temporary CI host.
No production consent, retention, offsite custodian,
provider/cache/media/device deletion or global recovery
release is implemented. #31/#32/#141 remain OPEN.

## Pre-erase anchor acknowledgement is not GDPR Article 17 completion (ADR 0037)

In the synthetic GitHub CI flow an attempted DELETE is
denied unless request UUID, tenant and subject already
appear in a verified independent pre-intent HMAC
high-watermark. This protects against one class of
lost-unanchored request preceding deletion in the
test but proves neither the requester's legal identity
nor that the later primary DELETE committed; after
physical PITR the record may disappear despite an
earlier success. The checkpoint stores only count,
digest and HMAC, but the underlying witness contains
personal metadata and still requires documented
retention, access/export and deletion policies.
No customer data, real OIDC authorization, provider/
cache/backup/device erasure, offsite custody or
GDPR certification is established. #31/#32/#141 OPEN.

## Pre-source-DELETE attempt custody is not GDPR erasure proof (ADR 0038)

The synthetic PostgreSQL third-cluster claim records
only a random request UUID, a domain-separated HMAC
of tenant/owner and an attempt timestamp, not raw
subject or memory content. The attempt may be consumed
even when the DELETE transaction fails or never
commits. It cannot prove legal requester identity,
consent, right-to-erasure completion or the scope
of provider/backup/device deletion. Claim identifiers,
MACs and timestamps remain personal-data metadata
requiring a documented lawful basis, retention,
export/access, deletion and exception handling;
none is yet settled for production. A real
physical source PITR test demonstrates denial
of request reuse but no GDPR conformity.
#31/#32/#141 stay OPEN.

## Ambito autenticato della claim e metadati personali (ADR 0039)

La nuova query read-only sul terzo cluster restituisce
soltanto l'HMAC per UUID, non tenant o subject in
chiaro; il verificatore ne controlla il confronto
con tenant/owner attesi e chiave sintetica privata.
Il digest non è anonimo per definizione: identifica
un tentativo correlabile e necessita comunque di
policy su accesso, conservazione, cancellazione e
base giuridica. Anche un MAC corretto non dimostra
chi abbia richiesto la cancellazione o se essa
sia effettivamente avvenuta; OIDC/consenso,
provider/media/backups/device, retention reale e
GDPR artt. 5/17 restano fuori dal perimetro CI.

## Fourth synthetic erasure attempt custody, still no GDPR proof (ADR 0040)

The fourth PostgreSQL CI instance stores only a random
request UUID, domain-separated HMAC of tenant/subject,
and admission timestamp. Metadata remains correlated
to a data subject and requires retention/access/export
and legal-basis review. Admission can occur even if
subsequent third or primary transactions never commit.
The fourth PGDATA supplies no right-to-erasure verdict,
nor real OIDC identity, consent, third-party media/backup/
device erasure, immutable offsite storage or GDPR Art.17
compliance. A true third-PGDATA physical PITR rehearsal
does not modify the public REST API. #31/#32/#141 OPEN.


## Scope del test FOURTH-PGDATA PITR (ADR 0041)

Un secondo scenario di restore fisico usa dati sintetici e mantiene
la claim della TERZA PGDATA mentre retrocede la QUARTA. Il rifiuto
`23505` dimostra solo la non riutilizzabilità dell'UUID sulla
TERZA sopravvissuta, non il commit della cancellazione sul primario,
non il soddisfacimento dell'art. 17 GDPR e non la propagazione a
backup, dati derivati, connettori o provider. Nessuna rimozione di
record personali reali né apertura del gate recovery. #31/#141 OPEN.

## Controesempio di erasure intent dopo doppio PITR (ADR 0042)

Con il rollback di due claim authority nella stessa
area amministrativa, i rispettivi UUID one-shot possono
andare entrambi persi: non è dimostrata impossibilità
di replay. Il test PostgreSQL è limitato a dati sintetici,
mantiene la quarantena e non invoca il DELETE reale.
Una claim validata o nuovamente scritta NON è evidenza
del commit della richiesta di cancellazione GDPR Art.17.
Restano obbligatorie fonte esterna affidabile, revisioni
privacy/legal, dati derivati, backup e provider.

## Evidence di tentativo pseudonimizzata, non certificato di erasure (ADR 0043)

Il contratto non scrive tenant/subject in chiaro nel
body JSON: solo UUID, scope MAC e HMAC separato.
UUID e MAC possono comunque costituire metadati
personali pseudonimizzati; non assumere anonimato.
Prima di un vault offsite, documentare finalità,
base giuridica, retention limitata, modalità di
cancellazione al termine della retention, DPA,
consensi applicabili e accessi dei responsabili.
Il risultato attesta al massimo un tentativo visto,
non la cancellazione ex art.17 GDPR.
