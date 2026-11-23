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

## #141 — Correlazione opt-in intent UUID → COMMIT outbox (ADR 0034)

- [x] RED reale: test PostgreSQL richiede `EraseWithWitnessedIntentAsync`
  inizialmente inesistente, segnala metadati invalidi prima della
  cancellazione e test del retry su nuova acquisizione.
- [x] GREEN: verifica sul secondo PGDATA il pre-intent con login
  solo EXECUTE e ambito UUID/tenant/soggetto, poi cancella memoria
  e consenso e scrive esattamente lo STESSO UUID dell'intento
  nell'outbox transazionale PostgreSQL primario.
- [x] Il publisher esistente trasmette un completamento
  `erase-actor` con il medesimo UUID alla tabella erasure witness;
  un UUID già consumato fallisce con 23505 e rollback di nuovi
  consensi/memorie, non permette un erase retroattivo.
- [x] PITR fisico del primario usa il percorso correlato in
  scenario CI: il pre-intent sopravvive nel secondo PGDATA
  mentre il commit dell'outbox e dei DELETE sparisce dopo WAL
  recovery e non c'è ACK post-commit. Quarantena invariata.
- [ ] Il percorso è SOLO CI, non autenticazione/OIDC utente,
  non integrazione API, non durable proof che il DELETE abbia
  committato dopo crash, non ancoraggio degli intenti sulla
  terza PGDATA/offsite WORM, non fencing globale/repliche/
  worker/media/provider/backup o GDPR compliance.

- [ ] **Warning:** after primary PITR removes the original outbox UUID, source PK uniqueness no longer prevents repeated use of that request; recovery MUST remain quarantined and never automatically replay an intent.

## #141 — Triangolazione read-only stato intent / source outbox / witness (ADR 0035)

- [x] RED test PostgreSQL richiede un nuovo reconciler, prima inesistente;
  prova le 4 possibilità di osservazione senza autorizzare release/replay.
- [x] GREEN: verifica 3 identità SQL EXECUTE-only distinte,
  prefisso dell'intent di stesso UUID/tenant/subject e l'evento
  source tramite snapshot primario unico REPEATABLE READ READ ONLY
  (limit 1000 + overflow probe) e receipt witness post-commit.
  Ogni mismatch/utente privilegiato fallisce.
- [x] Classi di visibilità: `IntentOnlyCommitUnknown` (nessuna
  distinzione tra non commit e commit perso nel PITR),
  `SourceEventPendingWitness`,
  `SourceAndWitnessVisible`,
  `WitnessVisibleWithoutSource`.
  Tutti negano permanentemente `MayReleaseRecovery` e
  `MayAutoReplay`; nessuna mutazione, nessun endpoint API.
- [x] Nel PITR fisico PostgreSQL il nuovo inspector vede
  l'outbox pre-ACK prima del crash, poi vede soltanto
  l'intent dopo il recovery mentre l'outbox non esiste più:
  risultato esplicito `CommitUnknown`, API quarantinata.
- [ ] Non è una prova di commit indipendente, immutabile/offsite;
  non copre authority OIDC, nuovi eventi subito dopo
  lo snapshot, 2PC, gating globale, cache/backup/provider,
  vault/HSM e valutazione privacy. #141 resta OPEN P0.

## #141 — HMAC checkpoint indipendente per rollback intenti (ADR 0036, 2026-11-20)

- [x] TDD RED: test PostgreSQL richiede `IntentCheckpoint`,
  `PostgresPreEraseIntentSnapshot` e issuer/verifier del
  terzo cluster inizialmente mancanti.
- [x] GREEN: checkpoint HMAC-SHA256 con PURPOSE distinto dai
  completed erasure receipts, prefisso sequence/UUID/scope
  continuo, max 10000 e overflow probe nello snapshot
  `REPEATABLE READ READ ONLY` della SECONDA PGDATA.
- [x] Terza PGDATA PostgreSQL 17, database separato
  `omnia_intent_anchor`, controllo EXECUTE-only con
  SQL monotonic high-watermark; bootstrapping primo
  esplicito, nuovi checkpoint solo dopo verifica
  autenticata del prefisso precedente, reissue/regressione
  o chiave errata negati.
- [x] PITR FISICO witness aggiunge una seconda database
  `omnia_witness_pitr_intent_anchor` e chiave indipendente:
  checkpoint antesignano su intent pre-backup,
  nuovo intent dopo restore point WAL, ripristino clone
  prima del nuovo intent, credenziali reader ruotate,
  verifica contro ancora superstite con rifiuto del
  rollback e conta+byte HMAC invariati.
- [ ] Tutto condivide runner GitHub e operatore DB;
  non è ancora custodia offsite/WORM, key vault/HSM
  o prova dell'INTENT arrivato al terzo cluster prima
  di una perdita del witness, né prova di COMMIT
  primary o di legal authorization del richiedente.
  Nessun release gate globale/API/worker/replica e
  provider/backup/device erasure. #141/#109/#31 OPEN.

## #141 — Gating sintetico dell'erase su pre-intent già ancorato (ADR 0037)

- [x] TDD RED: nuovo integration test PostgreSQL richiede
  `EraseWithAnchoredIntentAsync` e `VerifyRequestAsync`
  assenti; compilazione volutamente negativa.
- [x] GREEN: su SECONDA PGDATA il reader EXECUTE-only
  legge snapshot `REPEATABLE READ READ ONLY`, il verifier
  autentica il prefisso HMAC tramite checkpoint sulla
  TERZA PGDATA e controlla UUID/tenant/owner e sequence
  entro il count ancorato. Intent mancante, fuori ambito,
  chiave errata, ruolo admin/writer o record successivo
  all'anchor → DELETE e outbox negati.
- [x] Fonte primaria: memoria e consensi vengono
  cancellati e lo stesso request UUID inserito in
  outbox in una singola transazione actor-scoped/RLS.
  Replay UUID con record ancora presente → unique
  violation 23505 e rollback di dati più recenti.
- [x] Rehearsal fisico del PITR primario: database
  `omnia_pitr_preflight_anchor` separato sul terzo
  PGDATA, chiave HMAC privata temporanea, intento
  remoto registrato ma NON ancora ancorato → tentativo
  `erase-unwitnessed` rifiutato senza cancellazione
  e senza outbox; dopo checkpoint ACK il DELETE
  sintetico può committare, ma manca l'ACK witness
  post-commit. Il PITR fa riapparire i dati e scomparire
  l'outbox; l'anchor HMAC resta identico, il source
  commit è INDETERMINATO e API resta quarantinata.
- [ ] Metodo confinato ai test GitHub CI, non integrato
  nell'API/OIDC, non garanzia atomica cross-cluster,
  non attestazione del commit source dopo un rollback,
  non custodia WORM/offsite/vault/HSM né possibilità
  di riaprire ingress/worker/repliche. L'intento può
  esistere anche se la cancellazione fallisce; nuove
  acquisizioni dopo PITR richiedono decisioni autorizzate
  fuori banda. #141/#109/#31/#32 ancora OPEN.

## #141 — One-shot attempt sulla TERZA PGDATA contro retry post-PITR (ADR 0038)

- [x] TDD RED: nuovo test PostgreSQL richiede
  `PostgresPreEraseExecutionClaimWriter/Reader` e
  `EraseWithOneShotAnchoredIntentAsync`, prima assenti.
- [x] Schema CI `admission.execution_claims` in
  database separato della TERZA PGDATA: UUID primario
  unico + HMAC di scope con separazione di dominio,
  timestamp di tentativo; funzioni SECURITY DEFINER
  con REVOKE PUBLIC in stessa transazione,
  credenziali EXECUTE-only e nessun privilegio
  diretto su tabelle.
- [x] Percorso sintetico: verifica l'intent già ancorato
  sulla seconda/terza PGDATA, poi ottiene una
  **CLAIM una tantum** persistita sul terzo database
  PRIMA del DELETE + outbox nella transazione source;
  duplicati 23505 e richiesta consumata senza source
  COMMIT rimane INCERTA, non ritentabile automaticamente.
- [x] Test reale PostgreSQL: concorrenza stessa UUID,
  cambio di attore, SQL role enforcement, rifiuto
  retry con memoria e consenso acquisiti dopo, e
  lettura read-only delle claim senza raw SELECT.
- [x] PITR fisico del primario: tentativo consumato
  salvato fuori dal source PGDATA, outbox e DELETE
  source persi dal rollback; il clone promosso tenta
  davvero lo stesso UUID ed è bloccato dalla claim,
  lasciando intatte memoria e consenso risorti;
  API mantiene quarantena e non emette ready.
- [ ] **Non è commit proof**: una claim precede il
  COMMIT e può rimanere orfana, nessun commit
  atomico fra tre PGDATA, niente offsite WORM
  o vault/HSM, identità OIDC dell'interessato,
  api/workers/repliche global fencing o
  cancellazione provider/media/backup/device.
  Il vecchio metodo sintetico non-gated e il
  REST pubblico non adottano ancora questo
  protocollo; #141 e dipendenze restano OPEN.

## #141 — Verifica autenticata scope della claim one-shot (ADR 0039)

- [x] TDD RED con `VerifyClaimAsync` inesistente:
  nuove asserzioni PostgreSQL di assenza UUID, tenant/owner
  errati, chiave errata, ruolo SQL writer/reader improprio
  e digest alterato dal superuser TEST.
- [x] Funzione SECURITY DEFINER `admission.claim_scope(uuid)`
  espone SOLO HMAC attraverso account EXECUTE-only reader,
  senza SELECT sulla tabella claim. REVOKE PUBLIC +
  GRANT nella stessa transazione di CREATE. Writer non
  può chiamarla. Ruolo guard verifica anche questo ACL.
- [x] Payload HMAC canonico unico per issuer e verifier,
  confronto a tempo costante e azzeramento di buffer
  temporanei. `HasClaimAsync` booleano resta solo
  diagnostica, non attestazione di scope.
- [x] PITR fisico PostgreSQL: autenticazione read-only
  dell'esatto tenant/soggetto e rifiuto di scope estero
  e writer usato come reader PRIMA e DOPO recovery;
  il clone perde UUID source ma la claim su TERZA
  PGDATA conserva digest atteso. Nessuna mutazione.
- [ ] Claim autenticata significa tentativo, NON prova
  storica di COMMIT source. DB superuser può modificare
  HMAC, assente WORM/offsite/vault; la chiave è
  privata ma nel runner CI. API pubblica e OIDC
  reale invariati; niente global fence o
  completamento GDPR di backup/provider/device.

## #141 — Quarta PGDATA one-shot, PITR fisico della TERZA (ADR 0040)

- [x] TDD RED: xUnit PostgreSQL richiede un nuovo metodo
  `EraseWithFourthFenceAsync`, inizialmente mancante.
- [x] Percorso CI sintetico: validate HMAC pre-intent
  sul witness/third; consumare request UUID una volta
  sulla QUARTA PGDATA fisicamente distinta; consumarlo
  poi sulla TERZA; solo dopo DELETE + outbox nello
  stesso commit source. Entrambe le claim sono
  HMAC-bound a tenant/owner con SQL roles EXECUTE-only.
- [x] Test PostgreSQL: simula perdita di claim TERZA
  dopo primo commit, QUARTA sopravvive e blocca
  riutilizzo dell'UUID anche con nuova memoria/
  consenso; verifica caso di claim QUARTA orfana.
- [x] Recovery fisico: basebackup della TERZA PGDATA,
  restore point WAL nominato PRIMA della claim;
  tentativo firmato QUARTA → TERZA, poi vero PITR
  e promozione di clone TERZA senza claim. Il
  lettore QUARTA autenticato conferma il consumo
  e la nuova scrittura duplicata 23505 viene
  negata. Credenziali del reader recuperato
  ruotate e password precedente rifiutata.
  Nessuna modifica ad API, DELETE, ready.
- [ ] La QUARTA può essere ripristinata/alterata
  dagli stessi amministratori del runner, e
  nessun commit cross-cluster è atomico.
  Non è WORM/offsite, né attestazione che DELETE
  sul source abbia committato, né richiesta
  legalmente autorizzata via OIDC. Nessun
  fencing di ingress/workers/repliche e
  gestione backup/device/provider. #141 OPEN P0.


## #141 — PITR della QUARTA PGDATA con claim TERZA sopravvissuta (ADR 0041)

- [x] Aggiunto il test TDD RED sulla QUARTA PGDATA (inizialmente privo
  di `--claim-fourth` e di WAL archiving); harness C# ed archiviazione
  WAL implementati dopo il test.
- [x] In CI è configurato un nuovo rehearsal fisico con
  `pg_basebackup`, `pg_verifybackup`, restore point nominato e
  promozione del clone QUARTA su 127.0.0.1:15442, distinto dal TERZO.
- [x] Il test richiede MAC tenant/owner/UUID su TERZA, assenza della
  vecchia claim nel clone QUARTA, rotazione reader+writer e rifiuto
  delle credenziali ereditate. Il retry deve fallire con `23505`
  sulla TERZA dopo la nuova claim su QUARTA.
- [x] **GREEN verificata**: [Omnia CI 44824173620](https://github.com/KeyserDSoze/Omnia/actions/runs/44824173620), [OIDC browser E2E 44824173641](https://github.com/KeyserDSoze/Omnia/actions/runs/44824173641) e [Pages 44824173601](https://github.com/KeyserDSoze/Omnia/actions/runs/44824173601) tutti SUCCESS al commit implementativo `274f574a`; entrambi i gate PITR terza/quarta sono success.
- [ ] Autorità offsite WORM indipendente, prova commit del primario,
  eventuale rollback simultaneo TERZA+QUARTA, autenticazione OIDC e
  fencing globale ancora APERTI. **Nessuna cancellazione source in test.**

## #141 — PITR fisico contemporaneo TERZA+QUARTA (ADR 0042)

- [x] RED: aggiunto scenario CI prima dei nuovi comandi
  `--dual-claim` e `--dual-probe-recovered`.
- [x] GREEN implementativa: due veri basebackup fisici/pg_verifybackup,
  restore point nominati PRE-claim, WAL archiviato POST-claim,
  due cloni promossi su 15443/15444 con credenziali ruotate,
  role guard EXECUTE-only e HMAC tenant/owner/UUID.
- [ ] Eseguire e verificare GREEN nella **CI dell'ultimo SHA**:
  l'esito voluto è che entrambe le claim nei cloni scompaiano e
  i due adapter possano riaccettare lo stesso UUID, rendendo
  la procedura **NON sicura per la riapertura** (exit 42 richiesto).
- [ ] Costruire una vera authority esterna non riscrivibile,
  una prova di commit DELETE sul primario e la release
  operativa/legale autenticata prima di usare dati reali.
  Il test non esegue DELETE né cambia lo stato recovery.

## P0 #143 — Contratto read-only di evidence immutabile version-pinned (ADR 0043)

- [x] RED: test fail-closed per UUID/tenant/owner e due chiavi,
  store identity, exact version ID, modalità, retention,
  JSON/MAC, missing e I/O prima della classe implementativa.
- [x] GREEN implementativa: `ImmutableAttemptEvidence` e
  `IImmutableAttemptObjectReader` senza dipendenze cloud,
  senza inserimento in API/DELETE; solo `MayAutoReplay=false`,
  `MayReleaseRecovery=false`, `ProvesSourceDeleteCommit=false`.
- [x] **RED/GREEN GitHub Actions verificate**: [RED initial 44882236030](https://github.com/KeyserDSoze/Omnia/actions/runs/44882236030) per tipi mancanti; [RED alias 44882440793](https://github.com/KeyserDSoze/Omnia/actions/runs/44882440793) per `latest`/`null`; GREEN `390331a4` con [CI 44882456199](https://github.com/KeyserDSoze/Omnia/actions/runs/44882456199), [OIDC 44882456209](https://github.com/KeyserDSoze/Omnia/actions/runs/44882456209) e [Pages 44882456200](https://github.com/KeyserDSoze/Omnia/actions/runs/44882456200) tutte SUCCESS. #143 chiusa; #144 e #141 aperte.
- [ ] **#141**: implementare adapter reali Azure Blob e S3 Object
  Lock (on-prem realmente indipendente), controllare lock e
  retention per versione, version pin esterno monotono,
  upload + ACK e attestazione post-commit primario, con
  test reali provider. Nessuna prova WORM o GDPR prod in #143.

## #145 — S3 Object Lock read adapter e test protocollo OSS (ADR 0044)

- [x] RED test-first: suite AWS SDK S3 con version lock reale
  prima dell'adapter, con runner effimero e nessun dato utente.
- [x] GREEN implementativa: `S3ObjectLockAttemptReader`, SDK
  AWS v4 pinning per GetObject e GetObjectRetention, COMPLIANCE
  soltanto, 512 byte max, version e actor-scope verificati
  dal precedente contratto, errori falliscono chiusi.
- [x] **RED/GREEN verificata su GitHub Actions**: [RED adapter mancante 44884847236](https://github.com/KeyserDSoze/Omnia/actions/runs/44884847236), [RED enum SDK 44884878720](https://github.com/KeyserDSoze/Omnia/actions/runs/44884878720). GREEN sullo SHA `649e12ab`: [CI 44885831789](https://github.com/KeyserDSoze/Omnia/actions/runs/44885831789) (2/2 S3, 19/19 Domain, 80/80 PostgreSQL, PITR/Docker/mobile), [OIDC 44885831790](https://github.com/KeyserDSoze/Omnia/actions/runs/44885831790), [Pages 44885831828](https://github.com/KeyserDSoze/Omnia/actions/runs/44885831828) tutti SUCCESS; #145 limitata al test S3 chiusa.
- [ ] **#144 OPEN**: verifica di MinIO/on-prem in ambiente
  indipendente, Azure Blob immutabile, IAM/RBAC, HSM/vault,
  version pin monotono, disaster multi-site e S3 provider
  reali fuori dal runner; #141 per commit primary+release.

## #146 — Azure Blob WORM SDK read-only version-scoped adapter (ADR 0045)

- [x] RED: Azure Storage SDK transport HTTP contract tests created
  before provider implementation and introduced into CI.
- [x] Reader Azure.Storage.Blobs separate from API: exact
  BlobClient.WithVersion for HEAD+GET, version-level Locked
  ImmutabilityPolicy and UTC retention, size and ETag checks.
- [x] **RED/GREEN verificata**: [RED adapter absent 44888345925](https://github.com/KeyserDSoze/Omnia/actions/runs/44888345925), [RED unknown enum 44888375998](https://github.com/KeyserDSoze/Omnia/actions/runs/44888375998), [GREEN CI 44888632016](https://github.com/KeyserDSoze/Omnia/actions/runs/44888632016) (4/4 Azure SDK HTTP, 2/2 S3 reali, 19/19 Domain, 80/80 Integration, PITR/Docker/mobile), [OIDC 44888631958](https://github.com/KeyserDSoze/Omnia/actions/runs/44888631958), [Pages 44888631988](https://github.com/KeyserDSoze/Omnia/actions/runs/44888631988) tutte SUCCESS sul commit implementativo `df498bc7`. #146 chiudibile solo per test HTTP SDK. #144 e #141 aperte.
- [ ] #144 P0: Azure REAL locked version-level WORM staging
  + Managed Identity/RBAC + separate administration,
  S3 offsite, persistent monotone pin authority, delete 403
  and operational approval. #141 P0 for post-commit
  primary evidence + full recovery gate. No GDPR-ready claim.

## #147 — S3 COMPLIANCE conditional PUT e ACK incerto (ADR 0046)

- [x] RED xUnit: API assente, conditional single PUT
  e lost-ack/untrusted readback negative matrix.
- [x] Implementazione: `VerifyPayload` prima del PUT
  (HMAC 2-key e scope), S3 SDK `MaxErrorRetry=0`,
  conditional `IfNoneMatch=*`, COMPLIANCE, MD5 trasporto,
  retainUntil UTC, VersionId pinned e readback
  COMPLIANCE con actor MAC. Solo `Observed`/`Uncertain`,
  mai un token di DELETE/replay/recovery.
- [x] **RED/GREEN verify**: [RED initial 44891077685](https://github.com/KeyserDSoze/Omnia/actions/runs/44891077685), [RED provider/retention 44891542059](https://github.com/KeyserDSoze/Omnia/actions/runs/44891542059), [GREEN CI 44891839494](https://github.com/KeyserDSoze/Omnia/actions/runs/44891839494), [OIDC 44891839549](https://github.com/KeyserDSoze/Omnia/actions/runs/44891839549), [Pages 44891839509](https://github.com/KeyserDSoze/Omnia/actions/runs/44891839509) tutte SUCCESS su `5b237395`; 7/7 S3 e 4/4 Azure SDK contract, 19/19 Domain, 80/80 PostgreSQL Integration, PITR reali.
- [ ] #144/#141 ancora OPEN: offsite admin indipendente,
  Azure WORM staging, external monotonic pin, source
  COMMIT certificate, writer OIDC e global DR quarantine.

## #147 — hardening semantica ACK S3 (ADR0046)

- [x] RED per provider mode diverso da S3 e retention
  effettiva abbreviata rispetto al PUT richiesto;
  il publisher ora ritorna `Uncertain` in entrambi i casi.
- [x] XUnit impedisce adapter AWS con retry SDK attivi.
- [x] **TDD RED/GREEN confermata**: [RED 44891542059](https://github.com/KeyserDSoze/Omnia/actions/runs/44891542059) per provider/retention errati, [GREEN CI 44891839494](https://github.com/KeyserDSoze/Omnia/actions/runs/44891839494) su `5b237395` (7/7 S3, 4/4 Azure, 19/19 Domain, 80/80 PostgreSQL), [OIDC 44891839549](https://github.com/KeyserDSoze/Omnia/actions/runs/44891839549), [Pages 44891839509](https://github.com/KeyserDSoze/Omnia/actions/runs/44891839509) tutte SUCCESS.
- [ ] #144/#141: pin version ID e authority esterna
  indipendente dal runner e da tutti i PGDATA, fonte
  autenticata COMMIT primaria, Azure WORM staging.

## #148 — S3 lost-ACK history reconciliation (ADR 0047)

- [x] TDD RED: nuove prove sul reconciler assente
  e real S3 lost ACK, delete marker, versioni multiple
  e scansioni paginate malformed.
- [x] Adapter AWS SDK `ListVersions` e
  `S3ObjectLockAttemptHistoryReconciler`, bound 8×2,
  HMAC+COMPLIANCE+store/actor, soltanto
  `PreviouslyObserved` o `Uncertain` non autorizzativi.
- [x] TDD verificata: [RED CI 44896553267](https://github.com/KeyserDSoze/Omnia/actions/runs/44896553267) mancava l'adapter; [GREEN CI 44896756880](https://github.com/KeyserDSoze/Omnia/actions/runs/44896756880) **SUCCESS** (10/10 S3, 4/4 Azure HTTP, 19/19 Domain, 80/80 PG Integration, PITR/Docker/mobile), [OIDC 44896756790](https://github.com/KeyserDSoze/Omnia/actions/runs/44896756790) e [Pages 44896756867](https://github.com/KeyserDSoze/Omnia/actions/runs/44896756867) SUCCESS sul commit `d218100f`. #148 chiudibile solo per il reconciler read-only, non per offsite/monotonic pin.
- [ ] **#144/#141 P0 OPEN**: S3 version-history scan
  non è un pin monotono/offsite indipendente, non
  attesta source DELETE COMMIT o global DR gate;
  Azure version-level WORM staging e privacy review
  ancora mancanti.

## #149 — S3 signed cross-bucket exact VersionId pin (ADR 0048)

- [x] Test TDD-first RED per doppio bucket Object Lock,
  pin canonico HMAC con 3 chiavi distinte, VersionId
  esatti e recupero positivo dopo latest delete marker,
  con fault injection lost pin ACK.
- [x] Pin writer e reader S3 real protocol con GET
  version-specific + GetObjectRetention COMPLIANCE
  ed expiration confronto con la ricevuta.
- [x] **TDD RED/GREEN verificata**: [RED CI 44904227083](https://github.com/KeyserDSoze/Omnia/actions/runs/44904227083) per tipi assenti; [GREEN CI 44904790204](https://github.com/KeyserDSoze/Omnia/actions/runs/44904790204) su `3db924a0`: 14/14 S3 (pin reale con 2 bucket + 4 test nuovi), 4/4 Azure SDK HTTP, 19/19 Domain, 80/80 Integration e PITR/Docker/mobile SUCCESS; [OIDC 44904790199](https://github.com/KeyserDSoze/Omnia/actions/runs/44904790199), [Pages 44904790212](https://github.com/KeyserDSoze/Omnia/actions/runs/44904790212) SUCCESS, medesimo SHA. La chiusura #149 copre solo questa slice, dopo recheck di ogni commit docs finale.
- [ ] **#144/#141 P0 rimangono OPEN**: due bucket same-runner
  non equivalgono a authority indipendente; serve
  durable monotonic version-pin external control plane,
  segregated accounts/keys/IAM, offsite retention,
  Azure staging WORM, source DELETE COMMIT and
  authenticated global recovery gate.

## P0 #162 — Tenant-level JSON intake quota (ADR 0061)

- [x] Scoped child of #37 extends #159/#161 with an atomic
  global/tenant/actor cap during authenticated JSON receiving
  (default 32/16/4) and immediate eviction of idle keys.
- [x] Test-first RED behavioral evidence: CI 44954479076,
  120/121 integration passed; extra same-tenant reader wrongly
  entered handler prior to feature implementation.
- [ ] Exact final main SHA must have successful CI including
  real PostgreSQL/RLS, PITR/witness, S3/Azure, Docker, mobile,
  OIDC browser and Pages; see issue #162 for live evidence.
- [ ] Parent #37 and epic #2 remain OPEN for global/tenant-wide
  fair scheduling, multi-instance/distributed control, WAF,
  media/storage/connector quotas and production review.

## P0 #163 JSON retained-buffer lifetime (ADR 0062)

- [x] Bounded concurrent in-memory JSON request streams during
  downstream handler work with independent instance cap 64.
- [x] TDD test-first and multi-failure cleanup regression tests.
- [ ] Verify full CI, real PostgreSQL, physical PITR, Docker,
  mobile, OIDC browser, Pages all SUCCESS on final SHA; issue #163
  is the evidence authority.
- [ ] Keep #37/#2 OPEN: distributed/tenant-fair processing,
  media/connector/storage quotas, WAF/ingress, load/GDPR review.

## P0 #164 — Tenant ceiling for buffered JSON processing (ADR 0063)

- [x] TDD behavioral RED: test-only c6b0abed,
  CI 44957612052 129/130 Integration passed; tenant A
  request wrongly read when spare GLOBAL buffers existed.
- [x] Atomic global/tenant buffer counts during slow handler
  lifetime, defaults 64/16; explicit config valid 1..global
  and default auto-clamp. No tenant key retention when idle.
- [ ] Final same SHA success required for CI (PostgreSQL,
  provider contracts, PITR/Docker/mobile), OIDC real-browser
  and Pages; exact workflow evidence maintained in #164.
- [ ] Parent #37 and privacy epic #2 still OPEN: cross-replica
  distributed quota, reserved-fair processing, ingress/WAF,
  media/AI/storage/connector budgets, realistic load and GDPR.

Historical #162/#163 checkboxes above refer to earlier candidates;
their child issues contain the final successful CI/OIDC/Pages
evidence and both are already CLOSED. They are NOT open work.

## P0 #165 — Actor cap on JSON buffers held by slow handlers (ADR 0064)

- [x] TDD behavior-first RED: test-only commit
  8cdda406, CI 44960870525:
  132 passed / 1 failed Integration, fifth same-account
  request wrongly reached handler.
- [x] Atomic per-process global/tenant/actor buffered JSON
  slots defaults 64/16/4; optional actor config clamps to
  the current tenant budget, active-only keys and 503
  no-read/no-queue. Independent read limits unchanged.
- [ ] Full final main SHA SUCCESS required for CI (real
  PostgreSQL, S3/Azure, PITR/Docker, mobile), real-browser
  OIDC and GitHub Pages; issue #165 holds exact evidence.
- [ ] Parent #37 and epic #2 OPEN: distributed quotas,
  reserved-fair processing, ingress/WAF, media/provider
  budgets, real load and legal/GDPR release review.

## P0 #166 tenant aggregate API rate (ADR 0065)
- [x] TDD RED test-only commit 4096db9a, CI 44962256629:
  135/136 PostgreSQL Integration passed, third same-tenant
  actor bypassed tenant 2/min while under actor 10/min.
- [x] 60-second process-local tenant rate cap 600 default,
  and bounded 4096 tracked windows, no queue, 429/no-store
  on exhaustion or cardinality saturation. Injectable
  TimeProvider supports deterministic clock and 128-way
  parallel admission regression.
- [ ] Confirm all CI jobs (PostgreSQL, S3/Azure, PITR,
  Docker, mobile), OIDC real-browser, Pages SUCCESS on
  the SAME final main SHA before closing child #166.
- [ ] Parent #37 and epic #2 remain OPEN for
  cross-instance rates, ingress WAF, anonymous sockets,
  media/provider budgets, load/SLO and GDPR release.

## P0 #167 — bounded actor rate partition memory (ADR 0066)

- [x] Behavior-first RED: test-only b564d5a6,
  CI 44966863089, 142 PASS / 1 FAIL real Integration
  (third actor wrongly admitted with MaxTrackedActors=2).
- [x] Preserve 120/min actor and 600/min tenant default
  request budgets. Limit actor partition count to
  8192 (1..32768) with shared bounded 60s fixed-window
  engine, 15s lazy expiry and fail-closed 429 unknown
  accounts, independent tenant partition map 4096.
- [ ] Final main SHA must have SUCCESS CI, real PostgreSQL,
  physical PITR, Docker, mobile, OIDC real-browser and
  GitHub Pages; exact evidence documented in issue #167.
- [ ] Keep parent #37 and epic #2 OPEN: remaining WAF,
  distributed quotas, per-connector/media/provider
  budgets, realism/load/SLO and production GDPR signoff.

Historical issue checkboxes above may predate the finished
CI SHA; refer to the CLOSED child issue comments for
verified exact-SHA evidence rather than assuming pending
work when the corresponding child is already closed.


## #182 — source filter on loaded Web timeline (ADR 0080)

Child of #79 / #41: tested accessible per-source choice
and "Tutte" over the signed-in Web timeline's already
loaded pages, with visible/loaded counts, transparent
pagination, explicit empty states and session-bound
selection. This is **not** server-side search, daily
thread grouping, device parity or production GDPR scope.
Actual RED/GREEN Actions evidence is tracked in issue #182;
#79, #41 and #2 stay OPEN.


## #183 — daily headings for the authenticated Web timeline (ADR 0081)

Client-only local calendar day grouping from real `occurredAt`
with DST-aware `Intl`, merging loaded keyset pages without
duplicated day headings. Rows preserve signed source/time,
optional #182 source filter and session-bound privacy fences.
This closes only the narrow child #183 after same-SHA CI
evidence; parent #79 and #7/#41 remain OPEN for threads,
day search, tag/person views and native parity. AI recap
is a separate, unimplemented product capability.


## #184 — combined loaded tag/source selector (ADR 0082)

After #182 and #183, signed-in Web now filters only
already-loaded notes with exact source AND tag while preserving
the original local-day grouping and page cursor. Tag options
come from the whole loaded list. Empty state/count honestly
describe loaded rows, never server-wide search. Backend tags
are heuristic keywords (not AI). RED→GREEN and same-SHA CI
evidence is in #184. Parents #79/#7/#41/#17/#2 stay OPEN
for editable tags, thread/person views, native parity and
privacy/compliance gates.
## #185 — filtro giorno locale sulle sole note Web già caricate (ADR 0083)

Child di #79 che segue #182 (fonte), #183 (giornate) e #184 (tag). Scelte giornaliere esplicite, nel timezone del device, derivate dalle sole pagine già caricate. I tre filtri AND precedono il grouping; nessuna query globale, nessuna scansione automatica, paging esplicito ed epoch sessioni invariati. Dettagli e test real OIDC in issue #185. Non completano thread/persona, native sync né privacy: #79/#7/#41/#17/#2 OPEN.

## #186 — CI clock-valid Azure WORM scripted retention fixture

A passing synthetic Azure SDK protocol test must retain its one-hour fail-closed real-clock retention boundary. Historical fixed `now` inputs in two positive tests expired and triggered `AZURE_WORM_RETENTION_UNTRUSTED` in CI. #186 shifts only synthetic test reference times to real UTC and preserves negative near-expiry tests, without touching production publisher logic or claiming real Azure WORM custody. Evidence and closure state tracked in GitHub #186; #144/#10 remain OPEN.
