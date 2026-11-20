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
