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

## Feature #30 — PostgreSQL RLS (2026-11-17)

- [x] Migrazione forward-only `0003_rls_scope.sql` su memoria e consensi con `ENABLE` e `FORCE` RLS, policy select/write tenant+owner.
- [x] Ruolo DB runtime distinto e non privilegiato con bootstrap locale, migration role separato e validazione fail-closed in API.
- [x] Session actor scope transaction-local e test PostgreSQL reali per SELECT senza WHERE, INSERT/UPDATE fuori scope, ruolo amministrativo non consentito come runtime e pool reuse (run CI da confermare prima di dichiarare green).
- [ ] Azure managed identity/DB role provisioning robusto, IAM e secret rotation, security review injection e portabilità su pgvector Flexible Server; aspetti da validare prima di release con dati reali.

## Privacy #31 — export ed erasure primi confini (2026-11-18)

- [x] JSON streaming server-side con tutte le righe `memory_entries` e `consent_grants`, campi raw/normalizzati e vettori, no truncation, actor scope RLS.
- [x] Erasure atomica e idempotente nel PostgreSQL primario, con blocco concorrente su capture/consent e receipt con conteggi.
- [x] UI Web autenticata per export e cancellazione con conferma esplicita; tests PostgreSQL e browser Keycloak reali (evidenze CI da riportare nelle issue).
- [ ] Erasure/export degli object store, allegati/audio, cache, code, connettori, servizi AI e backup; job con prova di propagazione, tombstones, audit minimizzato e policy per eccezioni; resta #31 aperta.
- [ ] Gestione retention (#32), cifratura (#33), Azure production/procedure e audit legale prima di dati reali.

## Privacy #32 — prima retention server-side delle note (#136, 2026-11-18)

- [x] TTL 30 giorni da `received_at`, fissato dal server, backfill storico basato sulla data originale, niente estensione con retry idempotente.
- [x] Timeline senza note scadute, export con `expiresAt` e UI con informativa trasparente; consenso mantiene scadenze separate.
- [x] Worker schedulato 15 minuti, batch limitati, delete raw/normalized/embedding e ruolo DB dedicato senza accesso diretto alle tabelle.
- [x] Test PostgreSQL di expiry, isolamento utenti e privilegi, batch/idempotenza (CI finale da associare alla sotto-issue #136).
- [ ] #32 resta aperta: policy differenziate per finalità/source/categoria/tenant, consenso/audit, backup/WAL e provider esterni, worker isolato con identità di workload e osservabilità/SLO, parità Azure verificata, review GDPR specifica.

## Privacy #33 — primo componente crittografico (#137, 2026-11-18)

- [x] Porta `IEnvelopeEncryption` senza SDK cloud e codec Infra AES-256-GCM con per-payload DEK, KEK versionata, associated data tenant/subject/purpose/record e formato binario esplicito.
- [x] Keyring letta da file esterno (senza chiavi di default) con fail-closed su chiavi malformate o mancanti; rotazione tramite `Rewrap` e recupero con nuova chiave.
- [x] Test crittografici reali per roundtrip, randomizzazione, manomissione, cross-tenant/user, ID record/finalità, rotazione e reinizializzazione (link CI da riportare in #137).
- [ ] **#33 OPEN:** integrazione con storage raw/normalized/summary/media/token e derivati pgvector, backfill senza plaintext, vault Azure/OpenBao, gestione chiavi per ambienti/tenant, disaster recovery, audit e scansione segreti; nessun trattamento dati reali in assenza dei gate.


## #138 — fase 1 encryption-at-rest della memoria, sotto-issue #139 (2026-11-18)

- [x] Migrazione 0005 con `sealed_payload`, constraint redaction e trigger a rifiuto di nuove scritture plaintext.
- [x] Nuove note: raw/normalized/summary/tags dentro busta AES-256-GCM, AAD con UUID canonico, idempotent upsert e timeline decrittata sotto RLS.
- [x] JSON export di tutte le righe con `storageProtection` per distinguere legacy e cifrate; erasure e retention già eliminano l'intera riga.
- [x] Fail closed API senza key ring esterna, CI OIDC con file di chiave ephemeral random e Docker mount read-only (evidenze finali CI da registrare in #139).
- [ ] Backfill completo di tutti i record legacy, cifratura dei vettori pgvector e metadata, versione di chiave per oggetto e rotazione persisted, AI privacy, vault OSS/Azure e backup restore verificati restano in #138/#33.

## #140 — Read-only global legacy audit (2026-11-18)

- [x] Nuova slice implementativa: funzione SECURITY DEFINER a output solo
  `count(*)`, ruolo audit non-owner indipendente e CLI offline.
- [x] Strategia FORCE RLS owner non-superuser documentata in ADR 0013.
- [ ] Criteri di commit/Actions da allegare a #140 dopo GREEN verificato.
- [ ] Inventario soggetti completo, rollback/restore, test Azure e schema
  cutover con `sealed_payload NOT NULL` non ancora effettuati.
- [ ] Il numero globale zero non certifica la protezione di embedding,
  metadata e backup, né la conformità GDPR.
## #140 — Batch retry-safe per inventario di soggetti approvati (2026-11-18)

- [x] Slice implementativo con parser di manifest amministrativo JSON
  strettamente validato, file privato e zero identificatori nei log.
- [x] Worker per ciascun attore autorizzato con limiti su batch ed exit
  code di incompletezza; stesso Postgres RLS/actor-lock/crypto esistente.
- [x] Guard dedicato al ruolo data-backfill che rifiuta credenziali API e audit.
- [ ] Verificare stato verde dell'ultimo commit su Actions e collegare a #140.
- [ ] Non ancora effettuati inventario completo, restore drill, cutover schema,
  validazione Azure, vault/rotazione persisted, backup/WAL, embedding e legal gate.



## #140 — Logical restore rehearsal (2026-11-18)

- [x] Real synthetic PostgreSQL 17 pg_dump/pg_restore to a distinct
  disposable database, with restored RLS, ciphertext and data rights.
- [x] Retained old KEK allows recovery with a new active KEK; missing
  historical KEK fails closed, and restored erasure leaves source intact.
- [ ] Attach latest CI evidence to issue #140 after it actually finishes.
- [ ] Physical/WAL/PITR, separate clusters, managed Azure backup, vault
  restore, legacy global zero and sealed_payload cutover still missing.


## #109/#140 — Independent-cluster synthetic recovery test (2026-11-19)

- [x] Nuova CI obbliga `pg_dump -Fc` + `pg_restore` verso un secondo
  cluster PostgreSQL 17/pgvector e verifica `system_identifier` distinto.
- [x] Ruoli e password sono creati esplicitamente dopo che il restore
  con ruoli mancanti fallisce; test del vecchio login e RLS fail-closed.
- [x] Contenuto sealed, AAD/UUID, TTL, embedding, export, KEK
  retention e cancellazione isolata sono nuovamente verificati.
- [ ] Annotare in #109 e #140 il risultato della CI sul commit finale.
- [ ] Azure non-superuser, PITR/WAL, backup cifrati, vault recovery,
  data rights per copie storiche, medias e cutover restano da fare.


## #109/#140 — Backup fisico e PITR PostgreSQL 17 in CI (2026-11-19)

- [x] Aggiunto il gate sintetico obbligatorio `pg_basebackup`,
  `pg_verifybackup`, archivio WAL e recovery target nominale.
- [x] Scenario negativo: cancellazione sorgente **dopo** il restore
  point; il restore deve recuperare la nota ma non il marker successivo.
- [x] Asserzioni su ruolo runtime dopo rotazione, RLS, AAD/KEK,
  timeline/export e isolamento della copia.
- [ ] Registrare URL e risultati effettivi delle Actions del commit
  definitivo sulle issue #109 e #140; CI non equivale a DR in produzione.
- [ ] Azure, OpenBao/Key Vault, backup cifrati, WAL offsite, RPO/RTO,
  vecchi backup e cutover legacy restano bloccanti.


## #31/#109/#125 — Riconciliazione cancellazioni dopo PITR (2026-11-19)

- [x] Slice CI: ricevuta sintetica esterna a PGDATA, HMAC-SHA256 e
  test negativo su MAC assente o manomesso prima del replay.
- [x] Replay via API infrastrutturale già esistente con actor lock e
  RLS, idempotenza ed export vuoto dell'attore cancellato.
- [x] Test negativo di isolamento di un secondo tenant sintetico.
- [ ] Allegare run completo Actions sul commit finale prima della
  dichiarazione GREEN.
- [ ] Nessun journal indipendente durabile, watermark anti-rollback,
  vault, autenticazione reale del mittente, gate di traffic freeze
  o data rights sui provider/backups/device; issue P0 aperte.


## #141 — Recovery access-state gate of the API (2026-11-19)

- [x] Strict operator-controlled private file required at non-Development
  API boot before migrations/worker initialization; exact ready marker.
- [x] Missing, quarantined, malformed, symlink and broad Unix permissions
  deny startup, with unit/integration tests.
- [x] Per-request HTTP recheck denies health/personal-data routes with
  503 and no-store after operator quarantine.
- [x] Live OIDC/API test and CI startup-denial test added; verify actual
  Actions before asserting they passed on the latest HEAD.
- [ ] Durable signed external erasure journal, proof of completeness,
  high-watermark and immutable operator decision; coordinated stop
  of workers, in-flight requests and ingress across replicas.
- [ ] No production deployment or GDPR completion claim.


## #141 — Quarantena del worker retention (2026-11-19)

- [x] Nuovo wrapper `RecoveryGuardedRetentionCycle` riusa il gate
  dell'API e verifica il marker prima di ogni batch PostgreSQL TTL.
- [x] Test con revoca tra due batch e real PostgreSQL: expired
  note non rimossa in quarantena, rimossa dopo ready, live intatta.
- [ ] Verificare CI GREEN finale e collegarla alla issue #141.
- [ ] Restano aperti in-flight SQL/TOCTOU, offline workers, replica
  coordination, watermark anti-rollback, vault, journal indipendente
  e gating di ingress e provider.


## #141 — Offline HMAC recovery journal/checkpoint verification (2026-11-20)

- [x] Verified bounded JSONL sequence, event MAC, SHA-256 chain,
  exact v1 schema, distinct-key authenticated checkpoint and
  rejection of missing/truncated/reordered/forged or gapped events.
- [x] Read-only `Omnia.RecoveryAudit` and private file validation;
  no access to PostgreSQL or data mutation.
- [x] xUnit RED/GREEN and CI smoke for no-input fail-closed CLI.
- [ ] Record final Actions status and evidence on issue #141.
- [ ] External append-only publisher, crash-consistent outbox, signed
  authorization, durable anti-rollback sequence/watermark, HSM/vault
  custody, erasure replay, global traffic fence and Azure/on-prem
  equivalence; not production ready.


## #141 — Local recovery journal append writer (2026-11-20)

- [x] Added signed append-only local writer with verification of
  previous journal/checkpoint, strict private directory and exclusive
  lock; snapshot/checkpoint update after journal flush.
- [x] Tests for multiple writes and restart, fail-closed partial
  state / simulated crash, file permissions, lock conflicts and
  actor/key validation.
- [x] CI PITR source fixture now appends through the .NET writer
  instead of generating a single Python JSONL record directly.
- [ ] Record complete final Actions result and links on #141.
- [ ] Producer from authorized app erasures, transactional outbox,
  off-DB append-only ledger with independent monotonic checkpoint,
  multiple replica fencing/ingress, provider erasure, GDPR assessment
  and Azure/on-prem parity remain P0 OPEN.


## #141/#31 — Transactional PostgreSQL erasure outbox (2026-11-20)

- [x] Migration `0007_erasure_outbox.sql`: append-only column grants
  for the JWT-derived actor, FORCE RLS and no runtime SELECT/DELETE.
- [x] `EraseAsync`: event insert in SAME transaction as consent+memory
  erasure under actor lock; outbox insert failure rolls back erasure.
- [x] New role gate distinguishes API from backfill/count-only audit.
- [x] PostgreSQL tests for transactional rollback, role separation,
  actor scoping, repeated delete and limited row contents.
- [ ] Record final CI/Pages/OIDC status on issues #141/#31/#109.
- [ ] Unimplemented: off-DB publisher, durable ack, independent
  monotonically anchored checkpoints, stable request-id dedup,
  audit metadata GDPR retention/export, zero-trust vault keys,
  cross-store erasure, disaster-recovery traffic release.


## #141 — Bounded dispatcher and local at-least-once delivery slice (2026-11-20)

- [x] New PostgreSQL `SECURITY DEFINER` bounded outbox metadata reader
  (1–100 rows) with an optional EXECUTE-only, no-bypass dispatcher login.
- [x] Tests deny API/backfill/audit/retention callers and direct table access.
- [x] Synthetic local publisher preserves stable database event UUIDs,
  scans from the beginning, and avoids journal duplication on retries.
- [x] Test negative failures for corrupt checkpoint and wrong role;
  source outbox remains untouched on delivery failure.
- [ ] Verify complete Actions on latest main and update #141.
- [ ] Durable external/append-only producer, authenticated ACK and
  high-watermark, commit-order concurrency protocol, Key Vault/OpenBao,
  worker scheduling, global traffic fencing, GDPR metadata retention
  and on-prem/Azure staging remain unimplemented.



## #141 — Separate PostgreSQL erasure witness prototype (2026-11-20)

- [x] Second PostgreSQL 17 container with independent PGDATA and
  explicitly checked distinct system identifier.
- [x] EXECUTE-only witness writer and reader logins; bounded SQL
  read, stable UUID dedup and transactional sequence via FOR UPDATE.
- [x] Real PostgreSQL test: committed synthetic erase delivered to
  witness, source outbox test row removed, remote receipt persists.
- [ ] Production independent durability/backup across failure domains,
  signed off-cluster monotonic checkpoint, continuous publisher + ACK,
  host/ingress/worker fencing, Azure/on-prem IaC and key custody.
- [ ] Link final complete HEAD GitHub Actions evidence to #141.



## #141 — Same-event physical PITR and witness replay (ADR 0026)

- [x] Real source EraseAsync after named WAL point, atomic outbox insert.
- [x] Publish the committed event UUID to the second PostgreSQL witness.
- [x] Physical PITR before THAT SAME erasure: restored memory exists and
  source outbox event is absent.
- [x] Negative role denial, positive witness-read reconciliation,
  repeated replay, export/timeline erased and peer tenant untouched.
- [ ] Verify latest HEAD Actions and track on #141/#109/#31.
- [ ] Delivery completeness before crash, signed immutable external
  anti-rollback checkpoint, vault custody, production publisher,
  global quarantine/traffic coordination, GDPR cross-store rights.

## #141 — CI-only autonomous witness publisher and retry (ADR 0027, 2026-11-20)

- [x] Offline `Omnia.ErasurePublisher` process, separate from public API, rejects anything but fixed synthetic loopback CI identities; validates dispatch and witness EXECUTE-only SQL roles.
- [x] Continuous bounded rescans with 5-second normal interval, capped exponential backoff after failure, and no logs of actors/connection details. A one-shot CLI smoke is in CI.
- [x] Test RED specified witness outage, automatic retry, external receipt persistence across a fresh publisher invocation; witness dedup keeps the same UUID/sequence. Actual PostgreSQL CI GREEN evidence is required before marking test gates complete.
- [ ] Production-grade publisher deployment, independently durable/attested ACK and source lag alerting, monotonic completeness proof across primary failures, replay release authority, offsite WORM/vault, replicas/workers ingress fencing, and GDPR review remain OPEN in #141 / #31 / #109 / #125.

## #141 — Witness HMAC prefix checkpoint (ADR 0028, 2026-11-20)

- [x] New offline reader on the independent synthetic PostgreSQL witness: one REPEATABLE READ / READ ONLY snapshot, EXECUTE-only login, ordered sequence 1..N/UUID validation and a hard 10000-receipt bound (plus overflow probe).
- [x] Versioned canonical checkpoint with count, chained SHA-256 witness digest and HMAC-SHA256. Tests specify failure for truncated, reordered, mutated, repeated and tampered prefixes and for a wrong key. A later witness suffix remains UNATTESTED until a new independent checkpoint.
- [x] CI-only private-file issue/verify CLI; refuses absent, modified or overwritten checkpoint and wrong witness role; no API readiness promotion. RED test commit and CI evidence are recorded in #141 after verification.
- [ ] Still missing **independently durable and monotonic custody** (vault/offsite/WORM), authenticated issuer, commit→witness completeness across primary failure, fencing all services, end-to-end erasure across external stores, GDPR retention and Azure/on-prem parity. #141/#109/#31 remain OPEN.

## #141 — Third PostgreSQL witness checkpoint anchor (ADR 0029, 2026-11-20)

- [x] CI provisions a THIRD PostgreSQL 17 instance with distinct PGDATA, system identifier and random login secrets (127.0.0.1:15438, `omnia_anchor`), beyond the primary and the erasure witness. It stores only the signed checkpoint bytes/count, not raw actors.
- [x] SQL head lock and append-only checkpoint rows prevent regression and conflicting equal-count replay for restricted writer; exact retries are idempotent. Reader and writer get only their respective SECURITY DEFINER functions, never table rights. .NET verifies role inside standalone read/write operations.
- [x] Issuer verifies previously anchored HMAC prefix against current witness before advancing; verifier compares current witness with independently stored checkpoint, fails closed on wrong key or simulated witness prefix truncation/alteration. TDD RED/GREEN Actions evidence is tracked in #141.
- [ ] This is three DB **containers on one CI host**, not immutable/offsite/monotonic custody. Real witness PITR against independently surviving anchor, external signer/vault, key rotation, monitoring, failover, source delivery completeness, global release fence and GDPR data rights remain OPEN.

### Guard contro il bootstrap silenzioso dell'ancora — ADR 0029

- [x] TDD RED richiede che `IssueAsync` rifiuti head assente e che
  `BootstrapAsync` sia invocabile solo con `operatorApproved=true`
  e solo quando ancora inesistente. Un secondo bootstrap non sovrascrive.
- [x] GREEN aggiorna il servizio e lascia `IssueAsync` per i soli
  avanzamenti preceduti dalla verifica HMAC del vecchio checkpoint.
- [ ] Il flag booleano è SOLO un'assicurazione nel test sintetico,
  NON autenticazione reale di un operatore o autorizzazione di ripristino.

## #141 — Real witness PITR with surviving third-cluster checkpoint (ADR 0030)

- [x] TDD RED requires the new physical witness WAL/PITR rehearsal CI step.
- [x] PostgreSQL 17 witness WAL archiving, `pg_basebackup` +
  `pg_verifybackup`, restore point before synthetic erasure,
  restricted append+anchor advancement, and a physical clone promoted
  to the pre-erasure WAL point.
- [x] Exact markers and missing post-point receipt verified; original
  witness still authenticates, clone does not. Separate anchor PGDATA
  remains unchanged; restored reader password rotated, stale login
  denied; no API `ready` or user-facing recovery action.
- [ ] Witness+anchor disaster in different hosts/regions, offsite WORM
  and vault/HSM custodian, issuer authentication, independent
  anti-rollback high-watermark, primary commit→witness source
  completeness, global service fence, external store erase and GDPR
  review remain OPEN (#141/#109/#31/#33).

## #141 — Live source-to-witness acknowledgement audit (ADR 0031, 2026-11-20)

- [x] TDD RED: synthetic `EraseAsync` commits an outbox event; before
  independent receipt, the restricted read-only audit must deny
  acknowledgement. Additional tests reject wrong credentials, unknown
  UUID and event metadata mismatch without poisoning shared CI witness.
- [x] GREEN: `PostgresErasureLiveDeliveryAudit` scans bounded committed
  primary outbox in a single REPEATABLE READ / READ ONLY PostgreSQL
  snapshot through EXECUTE-only SQL and checks matching witness receipt
  UUID+tenant+subject+type. It never writes source cursor or receipt.
- [x] Separate CI-only offline `Omnia.ErasureDeliveryAudit --all` verifies
  *all* currently committed source outbox rows AFTER test publisher
  completes; denies missing opt-in and wrong witness/source roles.
- [ ] This is a LIVE delivery-health audit, not historical completeness
  proof after primary PITR. Pre-ACK primary crash can still lose
  committed erasures; no independent source commit counter, offsite
  WORM/vault, global gate, Azure/Docker parity or end-to-end GDPR
  erasure assurance. #141/#109/#31 remain open.

## #141 — Physical pre-ACK erasure loss and false-green source audit (ADR 0032, 2026-11-20)

- [x] TDD RED: physical primary PITR script requests a new synthetic
  actor before `pg_basebackup`; the earlier restore harness does not
  implement that phase, so the CI PITR step fails.
- [x] CI-only actor is captured BEFORE physical backup; real
  `EraseAsync` commits AFTER the named WAL point and AFTER the LAST
  witness publisher cycle. Original live source has no actor memory,
  committed outbox UUID but witness has no ACK.
- [x] Private runner UUID outside source PGDATA drives read-only
  `--live-gap` / `--recovered-gap` checks: PITR resurrects the
  actor's memory+consent but BOTH recovered source outbox and
  independent witness lack the committed event. Source-only full
  scan can pass on remaining witnessed events; the remembered UUID
  explicitly demonstrates why this is NOT release evidence.
- [x] Only restricted SQL dispatcher/reader, recovered dispatcher
  password rotation, missing opt-in and wrong role denied;
  quarantined API remains unavailable. Replay for another
  witnessed actor cannot silently resolve missing historical ACK.
- [ ] The private test UUID is NOT independently signed or
  monotonic, so the system still CANNOT reliably discover every
  pre-ACK erasure lost after a real crash. Need external per-commit
  ledger/WAL attestation before API/worker/replica release,
  offsite WORM/key custody, backup/AI/provider/device erasure,
  Azure/on-prem DR and documented GDPR review.

## #141 — Independent pre-erase intent preflight (ADR 0033)

- [x] **RED** integration test requires missing pre-erase SQL
  writer/reader and a separate request role (CI expected compile
  failure, plus corrected async assertion).
- [x] **GREEN** new CI-only `witness.erasure_intents` with
  serialized bounded idempotent UUID sequence, immutable
  tenant/subject intent scope and separate EXECUTE-only writer
  and reader SQL identities. Original completed-erase roles
  CANNOT write/read intents, and intent roles CANNOT append
  completed erasure receipts or access raw tables.
- [x] Live synthetic physical PITR scenario now checks a remote
  pre-erase intent before the DELETE; wrong role/opt-in deny
  before actor mutation. After the primary PGDATA rollback
  the remote intent is still visible even when both source
  outbox and post-commit witness ACK are absent.
- [ ] This is NOT an authenticated real-user erasure request,
  a signed commit, or full source→remote commit ordering:
  pre-intent may outlive a failed DELETE; missing intent
  is not proof there were no requests. No automatic replay,
  offsite WORM or third-cluster monotonic checkpoint of
  the intent stream, vault custody, traffic fence,
  data rights review, production API integration or
  Azure/on-prem parity. #141 remains OPEN.
