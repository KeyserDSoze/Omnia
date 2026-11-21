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

## RLS integration gate (#30)

The CI runs xUnit against disposable pgvector PostgreSQL with a randomized schema-owner password and a separate randomized `omnia_app` password. Tests use the real non-owner connection for all repository queries, never the migration admin. `RlsIsolationTests` deliberately sends SELECT without tenant WHERE and checks fail-closed SELECT/INSERT/UPDATE, no bypass of `row_security`, and no transaction actor-context leakage across a single pooled connection. The full OIDC browser workflow provisions the same app role and confirms JWT → consent → persisted note and cross-owner isolation. An API process with privileged runtime credentials must refuse to start.

## GitHub Pages artifact handoff reliability (#142, 2026-11-20)

Failure injection evidenced by [run 44824935287](https://github.com/KeyserDSoze/Omnia/actions/runs/44824935287):
upload succeeded, but deploy immediately observed zero artifacts; rerunning the
whole job uploaded a second same-named `github-pages` artifact, making deploy
fail with `Multiple artifacts named ... count is 2`. The Expo build was
successful; this is an artifact metadata/deploy race.

Pages now uploads under `omnia-pages-<run_id>-<run_attempt>`, and the
deploy action targets exactly that name. With read-only Actions permission,
a bounded wait checks that exactly one unexpired artifact is indexed by
GitHub before deployment. Zero until timeout or more than one is
fail-closed. No secret is displayed and no publication is authorized when
Pages is disabled. The workflow is still tested by real GitHub Actions,
not a mocked local runner. Consult issue #142 for observed GREEN evidence;
do not infer deployment health solely from a successful static export.

- Official upload action: https://github.com/actions/upload-pages-artifact
- Official deploy action: https://github.com/actions/deploy-pages
- GitHub Pages custom workflows:
  https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages

## Negative security regression: dual claim physical PITR (#141 / ADR 0042)

Run the CI-only shell gate after the separate THIRD and FOURTH
PITR tests. A passing test intentionally observes **both
post-target claims lost and re-admitted**; the drill CLI
returns exit **42**, not zero, with a fixed no-release
sentinel. A missing sentinel, unexpected success or any
other error must fail the workflow. This captures a known
common-mode vulnerability without mistaking a successful
replay for a safe recovery. All source actor/tenant values
are hardcoded synthetic test fixtures; neither the primary
personal data DB nor the REST endpoint is modified.

### Pages artifact-service consistency follow-up (#142)

The initial single-job REST polling was insufficient. Run
[44828446417](https://github.com/KeyserDSoze/Omnia/actions/runs/44828446417)
showed REST `Pages artifact visible` while
`actions/deploy-pages@v4` immediately observed 0 artifacts through
the internal `@actions/artifact` artifact-service API. The workflow
therefore separates `build` and `deploy` jobs, ensures upload
finishes before deploy job starts, passes the build job's exact artifact
name through a job output (important for partial reruns), and applies
a bounded metadata propagation grace period. Permissions are split:
build has read access; deployment alone has `pages:write` and
`id-token:write`. This is not a guarantee of GitHub infrastructure
availability; verify the REAL latest Actions run before closing #142.
Source: https://github.com/actions/deploy-pages/blob/v4/src/internal/api-client.js

## #143 — Provider-neutral WORM evidence contract (ADR 0043)

`ImmutableAttemptEvidenceContractTests` esercita ricevute
sintetiche canonical JSON + HMAC dello scope + un secondo
HMAC di evidence, immutable version ID esterno, storage
identity, retained-until e metadata di lock. Rifiuta
strictly empty/forged/mutable/expired/mismatched/missing
o I/O error. Un successo è **solo contract test con
reader mock**: real S3 Object Lock e Azure Blob immutable
policy/identity/version pin NON sono qui integrazione,
e nessun permission token di cancellazione esiste.

### RED/GREEN verificata per immutable attempt version pinning (#143)

- [CI RED 44882236030](https://github.com/KeyserDSoze/Omnia/actions/runs/44882236030): compilazione senza modello/provider contract;
- [CI RED 44882440793](https://github.com/KeyserDSoze/Omnia/actions/runs/44882440793): 1 di 80 test fallito per versioni `latest` e `null`;
- [CI GREEN 44882456199](https://github.com/KeyserDSoze/Omnia/actions/runs/44882456199): test xUnit/restore/PITR/Docker e mobile verde sul commit `390331a4`;
- [OIDC E2E 44882456209](https://github.com/KeyserDSoze/Omnia/actions/runs/44882456209) e [Pages 44882456200](https://github.com/KeyserDSoze/Omnia/actions/runs/44882456200) green stesso SHA. Questa è evidenza solo del **contratto con stub**, non di uno storage WORM reale.

## AWS SDK S3 immutable object version provider rehearsal (#145)

Il nuovo progetto xUnit `tests/Omnia.S3ObjectLock.Tests`
esercita l'API S3 reale di un OSS temporaneo:
bucket con Object Lock, versione COMPLIANCE,
GET con VersionId, GetObjectRetention stessa versione,
GOVERNANCE/unlocked/alias/foreign pin negati,
tentativo DELETE versione COMPLIANCE negato.
I log NON devono stampare segreti o contenuti.
SeaweedFS usa lo stesso runner della primaria;
l'assenza di offsite/WORM effettivo e di test Azure
rimane esplicitamente nelle issue aperte.

### Evidenze TDD e compatibilità S3 Object Lock (#145)

Il test-first ha prodotto [RED CI 44884847236](https://github.com/KeyserDSoze/Omnia/actions/runs/44884847236) (provider S3 non ancora implementato) e [RED CI 44884878720](https://github.com/KeyserDSoze/Omnia/actions/runs/44884878720) (enum AWS SDK PUT da correggere). Dopo test-first, adapter e aggiustamenti del bootstrap S3 CI con uid/gid reale e file privato `0400`, [GREEN CI 44885831789](https://github.com/KeyserDSoze/Omnia/actions/runs/44885831789) su `649e12ab` ha eseguito **2/2 test REALI SeaweedFS**, **19/19 Domain** e **80/80 Integration**, PITR fisici, Docker/mobile; [OIDC 44885831790](https://github.com/KeyserDSoze/Omnia/actions/runs/44885831790) e [Pages 44885831828](https://github.com/KeyserDSoze/Omnia/actions/runs/44885831828) GREEN. Il test provider verifica una semantica S3 reale ma non può attestare l'indipendenza dallo stesso runner, né validità giuridica della retention.

## Azure Blob SDK version-level WORM contract tests (#146 / ADR 0045)

Il progetto `tests/Omnia.AzureBlobWorm.Tests` esercita il
client reale Azure.Storage.Blobs usando un HTTP transport
fake: HEAD+GET pinned with `versionid`, risposta con
policy Locked vs Unlocked/missing/expired, mismatch
VersionId o payload/ETag, e failure HTTP, senza passare
da una istanza Azure. Azurite NON deve essere usato
come prova di version-level WORM. Test reale in tenant
Azure staging con Locked immutability + Managed Identity
e 403 DELETE sono ancora da implementare (#144).

### Azure Blob SDK HTTP provider read contract — TDD verifica finale (#146)

- [RED 44888345925](https://github.com/KeyserDSoze/Omnia/actions/runs/44888345925) test-first prima dell'adapter, errore CS0234/CS0246 come atteso.
- [RED 44888375998](https://github.com/KeyserDSoze/Omnia/actions/runs/44888375998): policy sconosciuta causa eccezione SDK; il reader è stato reso fail-closed e aggiunti test di HEAD/GET mismatch.
- [CI GREEN 44888632016](https://github.com/KeyserDSoze/Omnia/actions/runs/44888632016) sul commit `df498bc7`: **4/4 test Azure SDK HTTP sintetici**; 2/2 real S3 SeaweedFS, 19/19 Domain, 80/80 PostgreSQL Integration, PITR reali e Docker/mobile; [OIDC 44888631958](https://github.com/KeyserDSoze/Omnia/actions/runs/44888631958) e [Pages 44888631988](https://github.com/KeyserDSoze/Omnia/actions/runs/44888631988) SUCCESS sullo stesso SHA.
- Questa è una prova di SDK contro risposte HTTP simulate, non di version-level Azure WORM realmente Locked su tenant/account Azure o resistenza al DELETE. Mantenere #144/#141 aperte.

## S3 conditional COMPLIANCE publisher negative/real tests (#147)

`tests/Omnia.S3ObjectLock.Tests/S3ObjectLockAttemptSubmissionTests.cs`
verifica AWS SDK PutObject condizionale su SeaweedFS
Object Lock reale e rilettura COMPLIANCE della versione
con HMAC; non deve sostituire il publisher con un
semplice stub nel test positivo. Fault injection
sul port write simula lost acknowledgment dopo PUT,
timeout, 412, `null` VersionId e readback governance;
nessuno può produrre retry o ready. Payload MAC o
retention non validi sono negati *prima* del PUT.
Questi test non dimostrano atomicità distribuita,
physical offsite WORM né source DELETE commit.

## TDD #147 — modo/retention S3 dopo ACK

Il test `WrongProviderOrShorterThanRequestedRetentionCannotBeObserved`
deve essere RED se il publisher accetta una policy AzureLocked
o una S3Compliance con scadenza abbreviata. Il GREEN
richiede `ImmutableAttemptObservation` con modalità
e retention verificate e confronto con la richiesta.
`AwsClientThatCanRetryIsRejectedBeforeAnyWrite` respinge
configurazioni SDK con retry nascosti prima di una PUT.
Le suite continuano a usare il backend SeaweedFS
effimero, non authority offsite.

### Risultati TDD #147 PUT conditional + retention mode/readback

- [RED 44891077685](https://github.com/KeyserDSoze/Omnia/actions/runs/44891077685): assenza publisher/port prima della sua implementazione.
- [GREEN 44891269611](https://github.com/KeyserDSoze/Omnia/actions/runs/44891269611): 5/5 S3 suite, altri test e PITR SUCCESS.
- [RED hardening 44891542059](https://github.com/KeyserDSoze/Omnia/actions/runs/44891542059): 1/6 S3 fallito per accettazione di provider mode AzureLocked o retainUntil effettiva inferiore alla richiesta.
- [GREEN 44891839494](https://github.com/KeyserDSoze/Omnia/actions/runs/44891839494) sul commit `5b237395`: **7/7 S3 real protocol + fake faults**, **4/4 Azure SDK fake HTTP**, **19/19 Domain**, **80/80 PostgreSQL Integration**, PITR e Docker/mobile SUCCESS; [OIDC 44891839549](https://github.com/KeyserDSoze/Omnia/actions/runs/44891839549), [Pages 44891839509](https://github.com/KeyserDSoze/Omnia/actions/runs/44891839509) SUCCESS. Test dual PITR dimostra ancora replay NON sicuro, quarantena mantenuta.
- CI SeaweedFS è sullo stesso runner e non prova offsite immutable custody. #144/#141 OPEN.

## S3 bounded history reconciliation test-first (ADR0047 / #148)

`tests/Omnia.S3ObjectLock.Tests/S3UncertainAckHistoryTests.cs`
esercita AWS SDK v4 reale su bucket SeaweedFS effimero:
PUT COMPLIANCE realmente accettata dal server con ACK
artificialmente perso nel processo caller; scansione
`ListVersions` e verifica HMAC/retention esatta;
successivo delete marker latest non occulta la versione.
Cataloghi senza oggetti, con due versioni COMPLIANCE,
soggetti errati, alias, chiavi estranee o cursori ciclici
non danno mai retry o release. La scansione è
2/versioni per pagina, massimo otto pagine; malformed
metadata è un result Uncertain, non una prova di
inesistenza. Ancora nessuna verifica offsite e
source commit primario.

### TDD evidence #148: S3 uncertain ACK history

- [RED 44896553267](https://github.com/KeyserDSoze/Omnia/actions/runs/44896553267) sul commit `5b6aa349`: CS0246 su reconciler e port test-first mancanti.
- [GREEN 44896756880](https://github.com/KeyserDSoze/Omnia/actions/runs/44896756880) su `d218100f`: **10/10 S3 provider protocol + negative history**, 4/4 Azure SDK fake HTTP, 19/19 Domain, 80/80 PostgreSQL Integration, PITR reali/Docker/mobile; [OIDC 44896756790](https://github.com/KeyserDSoze/Omnia/actions/runs/44896756790), [Pages 44896756867](https://github.com/KeyserDSoze/Omnia/actions/runs/44896756867) SUCCESS.
- La detection del replay pericoloso dopo dual-PGDATA PITR continua a richiedere quarantena, non può essere sbloccata dal nuovo reconciler. Zero/multiple/incomplete S3 version listing non prova l'assenza di tentativi. #144/#141 P0 OPEN.

## S3 signed exact-version pin on second Object Lock bucket (ADR 0048 / #149)

La suite `S3ObjectLockVersionPinTests` usa
AWS SDK v4 reale, 2 bucket SeaweedFS locked
effimeri sullo stesso host: verifica ricezione
COMPLIANCE, pin firmato con 3 MAC key distinte,
retention versioned e GET+GetObjectRetention,
version ID exact-match anche dopo latest
delete marker; una versione GOVERNANCE
successiva non può sostituire la pin del test.
Negativi: attore diverso, signature pinKey
diversa, VersionId null/latest/mismatched,
lost ACK dopo PUT confermata dal server
senza auto-retry e nuovo controllo positivo
con un VersionId esternamente noto simulato.
TDD RED prima delle classi; non attribuire
a questa suite garanzie di monotonicity,
provider independence o legal GDPR.

### Evidenze GitHub Actions TDD #149 — cross-bucket signed pin

- [RED 44904227083](https://github.com/KeyserDSoze/Omnia/actions/runs/44904227083): commit test-first `009bd742`, fail CS0246 reader/service mancanti; fix visibilità scope con HMAC domain-separated.
- [GREEN 44904518296](https://github.com/KeyserDSoze/Omnia/actions/runs/44904518296): commit `5c9b7d88`, 13/13 S3, 4/4 Azure HTTP, 19/19 Domain, 80/80 PG Integration, PITR/Docker/mobile.
- [GREEN finale codice 44904790204](https://github.com/KeyserDSoze/Omnia/actions/runs/44904790204): `3db924a0`, 14/14 S3 (two-bucket pinned WORM + lost ACK + negative source mode/pin retention/store/keys), 4/4 Azure HTTP fake, 19/19 Domain, 80/80 PG Integration, PITR reali/Docker/mobile; [OIDC 44904790199](https://github.com/KeyserDSoze/Omnia/actions/runs/44904790199), [Pages 44904790212](https://github.com/KeyserDSoze/Omnia/actions/runs/44904790212) SUCCESS su stesso commit.
- La simultanea physical-PITR continua a osservare unsafe replay e quarantena. Un pin locale firmato non sostituisce un catalogo monotono o una custodia amministrativamente separata (#144/#141).

### Negative pin-catalogue regression S3 (#150 / ADR 0049)

I test AWS SDK + SeaweedFS su due bucket Object Lock devono
confrontare la singola versione COMPLIANCE firmata con la storia
visibile `ListVersions`, quindi introdurre un delete marker e una
seconda versione COMPLIANCE. `VerifyPinnedAsync` può ancora
confermare il vecchio pin: l'audit read-only deve dare `Uncertain`
senza inferire assenza, autorizzare retry, replay, DELETE o `ready`.
Test finti negano chiave/versione estranea, cursor non valido e
catalogo mancante prima di un GET. Questi test non sostituiscono
provider offsite reali, account/IAM isolati o un watermark monotono.

### Azure Blob scripted WORM PUT/lock fail-closed (#151 / ADR 0050)

I contratti xUnit su Azure.Storage.Blobs verificano PUT condizionale
con opzione Retry.MaxRetries=0, x-ms-version-id, PUT immutability
policy Locked sulla versione esatta, e readback HEAD/GET bloccato
con MAC/retention. Lost ACK dopo PUT, lock 403, versione mancante,
readback Unlocked/expired/foreign e actor MAC invalido devono
produrre solo `Uncertain`. Un caso positivo NON autorizza DELETE,
retry, recupero o rilascio. Il responder HTTP **non è Azure WORM**;
prove reali managed identity, network, policy e account offsite
restano negli acceptance criteria della parent #144.

### Azure Blob version-history ambiguity audit (#152 / ADR 0051)

xUnit usa il vero Azure.Storage.Blobs SDK per la richiesta List Blobs
Version|Deleted|DeletedWithVersions|Snapshots attraverso un responder HTTP
sintetico. I contratti fail-closed includono second version,
duplicate, missing, soft-deleted, snapshot, foreign key, unsafe
VersionId, cursor loop/truncation, list 403, wrong actor e HMAC.
Verifica poi tramite lo stesso reader HEAD+GET version-pinned Azure
Locked con retention futura. Anche la singola versione valida è SOLO
una observation del catalogo visibile; non prova storia completa,
monotonicità, root offsite, DELETE COMMIT o diritto a retry/recovery.
Real Azure staging e RBAC vanno testati separatamente prima di
chiudere le issue P0 parent #144/#141.

### Azure lost-ACK WORM forensic readback (#153 / ADR 0052)

Un test xUnit crea una ricevuta sintetica HMAC e simula un PUT
accettato dal remoto ma senza ACK al client: il publisher conserva
`Uncertain` e una sola invocazione write. La riconciliazione
read-only può osservare la versione Locked già esistente ma
NEVER autorizza un secondo PUT, retry, DELETE, replay o READY.
Prove negative: duplicate/second version, delete/snapshot/noncurrent,
missing/foreign/unsafe VersionId, cursor loop/scan cap/page empty,
provider denied, actor MAC sbagliato, wrong store/mode/retention,
input chiavi/UTC invalido. La history è un mock, mentre la suite
preesistente valida la serialization HTTP del vero Azure SDK. Nessuna
prova Azure offsite real, independent monotonicity, source DELETE
COMMIT o privacy legal compliance.

### Azure cross-container HMAC signed exact-VersionId PIN (#154 / ADR 0053)

Nuovi xUnit: pin firmato canonico e actor-bound; fake readers/writer
per MAC tamper, actor mismatch, store/mode/version/retention errati,
lost ACK dopo commit sintetico, duplicazione delle chiavi HMAC e
nessuna seconda PUT. Il vero Azure SDK v12 viene esercitato con
responder HTTP sintetico: upload `If-None-Match: *`, policy Locked
esatta VersionId, HEAD+GET pinned e limiti max1024; 403/lost ACK,
Retry.MaxRetries e HTTPS fanno fail closed. La prova non è Azure live,
non valida la separatezza degli amministratori né source DELETE COMMIT,
antirollback globale, privacy legal review o release dei recovery gate.

### Azure signed PIN version-list ambiguity test (#155 / ADR 0054)

La nuova suite xUnit esegue una enumerazione read-only su
`erasure-pins/v1/` usando il vero Azure SDK con un responder HTTP
sintetico; include versioni/deleted/deletedwithversions/snapshots,
prefisso canonico e maxresults=2. Il verificatore fail-closed
rifiuta catalogo empty/multi-version/duplicate, snapshot, deletion,
foreign, invalid pin ID, paginazione ciclica/troncata e provider 403
PRIMA di qualsiasi HEAD/GET. Quando la singola versione è valida
ricontrolla due versioni AzureLocked e la firma HMAC attore;
nessuna porta di write è invocata. Non costituisce custodia
indipendente o autorizzazione a riaprire dati dopo PITR.

### JSON request-body budget 413/415 anche senza Content-Length (#156)

Test `ApiJsonBodyBudgetTests` usa TestServer + vera istanza PostgreSQL
con ruoli separati e auth header **solo test**; controlla 413 con
dimensione dichiarata e body chunked/streamed di dimensione superiore
al limite, 415 per gzip, small JSON ancora disponibile al model binder,
`GET /health` e consensi non toccati, configurazioni invalide
rifiutate. Eseguire con `OMNIA_TEST_DB`, `OMNIA_TEST_APP_DB`,
`OMNIA_TEST_RETENTION_DB`, `OMNIA_TEST_KEYRING_FILE` disponibili.
Non inviare contenuti reali; nessun log di payload. Questo test
non garantisce WAF/ingress production, slowloris, quote distribuite,
upload audio o vera autenticazione OIDC, che ha la sua E2E.

### JSON body total read timeout — slow/chunked negative tests (#157)

`ApiJsonBodyReadTimeoutTests` uses only synthetic
`DefaultHttpContext` and cooperative cancellation-aware streams:
blocking upload, slow trickle, genuine `RequestAborted` client
cancellation and valid small JSON stream restoration. Tests assert
no downstream invocation after 408, no content echo, no-store, and
config validation (100..30000ms). These are not full network
slowloris/header tests. The integration project still runs against
real disposable PostgreSQL; the OIDC E2E and Kestrel/ingress
boundaries remain independent. Do not claim 408 delivery to
an already disconnected client or safe distributed rate limiting.


## JSON Content-Type early-rejection test-first slice (#158, ADR 0057)

`tests/Omnia.Integration.Tests/ApiJsonContentTypeTests.cs` introduces
a synthetic fail-on-read request body to prove that unsupported/missing
`Content-Type`, invalid charset, duplicate charset and malformed
media type yield **415 before any read or handler invocation**.
Valid `application/json` with/without UTF-8 and
`application/vnd.omnia+json` must pass unmodified to the handler and
restore the original body stream. `/health` remains untouched.
These are middleware contract tests, **not** live ingress/network,
provider-issued OIDC, database mutation, or protection against
distributed DDoS. Existing real PostgreSQL 413/415, 408 and 429
integration tests plus OIDC, Pages and mobile CI remain mandatory.

Test-first commit: [ad5f9d0f](https://github.com/KeyserDSoze/Omnia/commit/ad5f9d0f8944b225cf1589995f17c0d7434222ff).
Implementation commit: [ee7c598d](https://github.com/KeyserDSoze/Omnia/commit/ee7c598d75acec8c1591d6b0ac18f89499777f58).
CI RED/GREEN evidence must be recorded in issue #158 once checked; no
unobserved workflow success is implied by these links.

## Concurrent JSON intake TDD gate (#159 / ADR 0058)

New xUnit middleware contract ApiJsonReadConcurrencyTests uses
synthetic controllable, cancellation-aware streams and a shared
one-permit limiter. The second request must immediately receive 503
before its first ReadAsync and must NOT invoke the handler; permit
returns on normal buffering, 408 deadline and client cancellation.
A completed body must release its permit even if a downstream
application handler is blocked. 413 known length and unsupported
415 media type take precedence while a reader slot is occupied.
Config permits 1..256, refuses 0/257. Contract tests make NO
network, PostgreSQL, OIDC, distributed-node or production availability
claims; existing TestServer real PostgreSQL, Keycloak browser OIDC,
Docker, S3/Azure, mobile and Pages workflow gates remain mandatory.
CI evidence and final SHA must be captured on child #159 before
closure; parent #37 stays OPEN for fair tenant quotas, WAF and load
tests of real network connections.

## Kestrel header parser real socket gate (#160 / ADR 0059)

ApiKestrelTransportLimitsTests is a .NET 10 test suite independent
from PostgreSQL and TestServer. Test-first RED calls the not-yet-
implemented Configure API, verifies strict startup configuration
bounds, exact configured KestrelServerLimits, positive complete GET
and an incomplete HTTP/1.1 header on a *real loopback TCP socket*.
The connection must be closed or receive 408 before dispatch; the
endpoint invocation count must remain unchanged. This is one
transport-level functional test, NOT a performance test or a
validated Azure gateway/HTTP2/HTTP3/TLS slow-header claim.
Retain all existing Domain, real PostgreSQL, S3/Azure provider,
physical PITR, Docker, Playwright/OIDC, Pages and mobile CI gates.
Record RED and GREEN SHA/run URLs in #160; close only after all
required workflows succeed on the same final main SHA.
