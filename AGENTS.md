# Omnia — repository instructions

**Canonical repository:** https://github.com/KeyserDSoze/Omnia  
**Working branch:** `main` when requested by the owner. Never force push.

## Before changing code

1. Read `README.md`, `docs/architecture.md`, `docs/roadmap.md` and applicable `adr/`.
2. Create/track issues for non-trivial features; document architectural decisions in ADR.
3. Write a failing behavioral test before implementing a feature (red → green → refactor).
4. Run real xUnit, typecheck, Vitest, Playwright, and PostgreSQL tests; never describe unexecuted tests as passed.
5. API mutations must be tenant-scoped and authenticated, and third-party actions require opt-in, scoped privileges and an audit trail.

## Boundaries

- `src/backend/Omnia.Domain`: invariants and entities; no cloud SDKs.
- `src/backend/Omnia.Application`: use cases and ports; no Npgsql/Azure.
- `src/backend/Omnia.Infrastructure`: PostgreSQL migrations and embedding adapters.
- `src/backend/Omnia.Api`: REST, JWT/OIDC, CORS, DI.
- `src/mobile`: Expo React Native and shared web UI; must work with Pages static export.
- `tests/`: xUnit unit + PostgreSQL integration, Vitest pure client logic, Playwright browser.
- `docker/`: disposable local equivalents; `infra/azure`: cloud provisioning templates.
- `docs/` and `adr/`: architecture, security and decisions.

## Safety

No hardcoded credentials, OAuth refresh tokens or private personal content in git. GitHub Pages is public static hosting, not a backend. No unconsented recording, passive surveillance, automatic messaging, invasive third-party capture or claims of universal access. A plugin/connector is NOT trusted code: use declared capabilities, least privilege, secrets vault, explicit user consent, network isolation and audit. Protect the canonical identity and immutable provenance of an event; never rewrite its raw content during normalization. Do not show deterministic keyword tagging as AI insight. No production deployment until threat model, consent/retention, genuine end-to-end authentication and security gates pass.

## Commands

```sh
cd src/mobile && npm install && npm run typecheck && npm test && npm run build:web
cd ../..
dotnet test tests/Omnia.Domain.Tests/Omnia.Domain.Tests.csproj
docker compose --env-file docker/.env -f docker/compose.yml up --build
# OMNIA_TEST_DB=... dotnet test tests/Omnia.Integration.Tests/Omnia.Integration.Tests.csproj
```

## Backlog e feature management

Leggere `docs/backlog.md` e `docs/product-requirements.md` prima di nuove funzionalità. Non duplicare issue: cercare per area/titolo. Ogni nuova feature usa `.github/ISSUE_TEMPLATE/feature.yml`, include epic parent, phase label, priorità, RED → GREEN → REFACTOR, evidenza commit SHA e URL di Actions. Le 14 epic e 116 issue child non indicano funzionalità completate. Le milestone sono label logiche; GitHub Projects/Milestones non configurati. Prima del lavoro su dati veri completare #29, #30, #31, #32, #33, #34, #36, #39, #41, #43 e test #17.

## New consent guard (#29 slice)

All new memory writes MUST pass an active source-scoped `memory.capture` grant. Never bypass `IConsentGuard` or the transactional PostgreSQL check inside `PostgresMemoryRepository.UpsertAsync`. Never initiate external embeddings before consent + domain payload validation. Tests must include revoked/expired grants and cross-tenant denial. The consent endpoint only records first-party acknowledgement; it does **not** legalize third-party recordings or replace GDPR review.

## Rate limit invariants (#37 slice)

Keep `app.UseRateLimiter()` **after** authentication so the key derives from a signed identity; apply the per-user policy to all new `/api/v1` routes. Never use user-supplied headers as quota identity in production. Anonymous fallback uses peer IP only. The in-memory fixed window is **not** a distributed SaaS budget; do not claim full anti-abuse protections without WAF, media quotas and multi-replica tests.

## OIDC Web authorization source of truth

`src/mobile/src/services/webAuth.ts` is only for browser OAuth and must use a maintained OIDC library, authorization code + PKCE S256, session state/nonce checks, ephemeral in-memory tokens, correct redirect /Omnia/, and strict public HTTPS/loopback config. Do not add localStorage/AsyncStorage for tokens, don't store password/client_secret or trust client-supplied tenant IDs. Web captures must be API-confirmed, error states explicit, and manual scoped consent obtained first. Native OIDC requires separate Expo AuthSession + SecureStore with device smoke before claiming parity.

## Mandatory PostgreSQL RLS invariants (#30)

For every query accessing `memory_entries` or `consent_grants`, use `PostgresActorScope.OpenAsync` and execute all SQL inside that same transaction. Source tenant/subject ONLY from a validated OIDC JWT; no client JSON/header/GUC override. Use PostgreSQL runtime role that is NOT schema owner, SUPERUSER, BYPASSRLS or owner-member, and keep distinct privileged migration credentials. The app must fail startup if RLS isn't FORCE enabled. Add real PostgreSQL negative tests for unscoped/cross-owner SQL and pooled connection reuse. Never weaken `PostgresRuntimeGuard` or bypass RLS for tests; migration/admin datasource is *only* for migrations and fixture setup. Keep application SQL parameterized (custom GUCs alone do not prevent arbitrary SQL injection).

## Personal data export / erasure boundaries (#31 slice)

All new storage for personal content or derived embeddings MUST be registered in a complete export+erasure inventory, with contract tests, before the feature may be claimed end-to-end. Only the two PostgreSQL primary tables are handled by `IPersonalDataService` today; never assert backup, device, provider, Keycloak or external cache deletion. `PostgresActorLock` MUST be acquired before any capture/grant/revoke or export/erasure, inside `PostgresActorScope`, to serialize requests and protect against post-erasure writes. Only JWT-validated tenant+subject are allowed; no selector in user input. Keep export no-store, streaming, complete (no silent caps) and private. Deletion requires explicit confirmation and should return an accurate scoped receipt. Preserve RLS and real-Postgres/Keycloak E2E coverage.

## Mandatory retention invariants (#136/#32)

All newly persisted memory **must** receive a server-generated expiry based on initial `received_at`; no client control, no automatic extension from idempotent retry. Do not expose expired notes through normal memory browsing. Extend retention only through a new user-authorized capture (never an implicit retry). All new personal-data stores and derivatives require their own documented purge and RLS/security tests. The only global purge function is `public.omnia_purge_expired_memories(integer)` with a bounded, hardcoded body, SECURITY DEFINER, default EXECUTE revoked and granted only to `omnia_retention`; do not put it in the HTTP API role. Use separate random secrets/identities for migration, API and worker. Keep the parent #32 open until source-specific/legal retention, backup restore/tombstones, audit and provider propagation exist. Never claim that 30 days is required by GDPR.

## Crypto component — mandatory constraints (#137/#33)

`IEnvelopeEncryption` is a **codec contract**, NOT encryption-at-rest for the current PostgreSQL tables. Do not claim memory, consent ledger, embeddings, media, OAuth tokens or backup are encrypted until actual schema migration/backfill and real tests. Use fresh random DEKs, AES-256-GCM with 16-byte auth tags and 12-byte nonces, bind AAD to authenticated tenant + owner + purpose + canonical record ID, never use attacker-controlled identity claims, and fail closed on corrupted/unknown envelopes. KEKs are externally provisioned; no hardcoded, deterministic, fallback, default, browser-bundled or committed keys. Keep previous KEKs until all persisted objects are rewrapped and verified; document vault, rotation/restore and encrypted-search tradeoffs. Any new encrypted table must be included in #31 export/erasure and #32 retention before use. #33 remains open until durable storage, vault and recovery gates are met.


## Sealed memory write-path (#139 / #138)

Every new memory capture MUST use `SealedMemoryPayload.Encrypt` with a **stable canonical row UUID** and validated tenant/subject as AEAD associated data. The SQL columns `raw_content`, `normalized_json`, `summary`, `tags` are redacted in new rows; `sealed_payload` is mandatory for new INSERT. Never introduce a plaintext fallback for new writes, including key-ring errors or disabled configuration. `omnia_enforce_memory_sealed_writes` and `ck_memory_sealed_content_redacted` must remain effective after schema changes. Timeline and export MAY decode historical legacy rows only until an audited backfill/cutover replaces them; no bypass of JWT RLS or leak in log. New payloads remain within #31 export/erasure and #32 TTL. Crypto key ring is mandatory for API boot and sourced externally, not browser/Expo/GitHub sources. The actual encryption-at-rest gate remains OPEN while legacy rows, vector embeddings, metadata, backup, Key Vault/OpenBao are unresolved.


## Offline actor-scoped legacy memory backfill (#140 first slice)

The `Omnia.Backfill` executable must NOT be registered as an HTTP API or accept
untrusted browser-supplied actor selectors. Its operator uses the separately
provisioned, non-owner, no-BYPASSRLS `omnia_backfill` identity plus external KEK.
Only SELECT/UPDATE of memory_entries are granted. Acquire
`PostgresActorLock` **before** `FOR UPDATE SKIP LOCKED` and commit the new
sealed_payload and four redacted legacy fields atomically. Never change
canonical UUID, received_at/TTL, occurred_at, provenance, embedding or consent.
Never log plaintext, account identifiers or keys. Keep legacy reads intact
until a verified GLOBAL zero-legacy gate, restore rehearsal and schema cutover;
per-actor count zero is not global completion. #140/#138/#33 remain open.


## Count-only global legacy audit (#140 / ADR 0013)

The new offline `Omnia.BackfillAudit` executable, unlike the actor-scoped
backfill worker, obtains an exact GLOBAL legacy row count via the fixed SQL
function `public.omnia_count_legacy_memories()` only. NEVER register it in
HTTP DI or grant EXECUTE to the runtime, retention or backfill role. Its
separate `omnia_backfill_audit` identity has no direct SELECT/UPDATE/DELETE
on any personal table and does not inherit schema ownership or BYPASSRLS.
The migration owner is explicitly allowed RLS SELECT only to support the
fixed SECURITY DEFINER function under FORCE RLS in Azure non-superuser mode.
A global zero count is NOT proof that the system can remove the legacy
fallback; restore, backups/WAL, concurrency and signed release gates remain.

## Approved legacy backfill inventory (#140 / ADR 0014)

The offline backfill may accept an operator-provided private JSON inventory
via `OMNIA_BACKFILL_INVENTORY_PATH`, instead of a single tenant/subject.
This is NOT identity discovery or user consent. Require exact schema v1,
explicit operatorApproved=true, strict nonduplicate tenant+owner pairs,
max 1000 actors / 256KiB, absolute private file, no symlinks, no extra
fields or group/world Unix permissions. Never log actor IDs, file paths,
payloads, keys or DB exceptions. Reject API/audit/owner DB roles in the
CLI via `PostgresBackfillRoleGuard`. Every actor remains scoped by RLS
and advisory actor locks with bounded batches and retry-safe progress.
No plaintext fallback or automatic NOT NULL schema cutover. A manifest
result of zero applies ONLY to enumerated actors, not globally.



## Synthetic PostgreSQL restore rehearsal (#140 / ADR 0015)

The only supported restore-test harness runs in the disposable CI
PostgreSQL 17 container. It MUST refuse non-test DB names, remote hosts,
schema-owner credentials and production data. Real custom-format
pg_dump/pg_restore tests are required in addition to codec tests.
Retain historical KEKs to read restored ciphertext, prove missing KEKs
fail closed, validate RLS, UUID/AAD, expiry, export and erasure isolation.
Do not log/archive backup or keys. This does NOT prove cross-cluster
recovery, encrypted backups, WAL/PITR, Azure vault, GDPR erasure or cutover.


## Independent PostgreSQL recovery-cluster CI gate (#109/#140; ADR 0016)

`tests/restore/cross-cluster-rehearsal.sh` MUST run only in GitHub CI
after the synthetic same-cluster restore gate. It creates a NEW temporary
pgvector container with different PostgreSQL system identifier,
passwords and roles; no shared PGDATA. Check that restoring a DB without
the required global roles fails, then provision only the four Omnia
restricted roles before the successful pg_restore. Never reuse source
credentials or import real pg_dumpall password hashes. The harness
accepts only 127.0.0.1 test ports 15432/15433, fixed databases and
runtime identity; no HTTP exposure. RLS must deny unscoped queries
against memories and consent grants after restore. Keep archives,
keys and recovery passwords out of git/artifacts, and cleanup always.
Production Azure/PITR/WAL/vault/backup/tombstones are NOT validated.


## Disposable physical PostgreSQL PITR gate (#109/#140; ADR 0017)

Keep `tests/restore/physical-pitr-rehearsal.sh` CI-ONLY, after the
existing two logical restore gates. Run `pg_basebackup -X stream`,
`pg_verifybackup`, post-backup `pg_create_restore_point`, and WAL
archiving on the disposable test cluster only. The restored physical
copy MUST include a marker written after the basebackup but before
the restore point; it MUST EXCLUDE a second marker and the source-side
deletion after the point. Rotate restored app credentials and prove
original credentials fail. Verify sealed ciphertext, actor-scoped RLS,
TTL, key-loss failure and export using the existing test-only harness.
Never connect this script to production, cloud storage, or real data.
Never publish the physical PGDATA, archive/WAL, keys or passwords in
git, logs or Actions artifacts. PITR on a CI runner is NOT proof of
managed backup, vault recovery, geo-DR or GDPR deletion.


## Synthetic post-PITR erasure reconciliation (#31/#109/#125, ADR 0018)

The restore test MUST prove a record deleted after the named WAL
recovery point is initially recovered and then deleted again ONLY
following an authenticated out-of-PGDATA synthetic erasure receipt.
Refuse missing/tampered receipts before any mutation, keep HMAC key
off Git/logs/backups, reject extra/duplicate JSON fields, bind receipt
to fixed synthetic actor and recovery point. The fixture has a second
tenant whose data MUST remain intact; replay must be idempotent and
return an empty actor export. The test harness accepts only fixed
loopback test ports/databases and must never be HTTP-accessible.
DO NOT claim this test provides an authoritative external deletion
journal, anti-rollback high watermark, legal consent, production
traffic freeze, or cross-store erasure. Those remain P0 open gates.


## Recovery quarantine gate (#141 / ADR 0019)

Omnia.Api MUST call RecoveryQuarantineGate.Verify before DB connection,
migration or worker startup. Non-Development requires an absolute
operator-owned Recovery__AccessStateFile outside PGDATA/backup.
Only exact `ready\n` with private Unix permissions is accepted.
Any other content, missing path, unreadable file or symlink MUST block
startup. Recheck on every HTTP request; return 503/no-store on
missing/quarantined state. An explicitly configured Development file
is subject to identical checks; Development may omit the option
only for local/demo tests. Never create an HTTP release endpoint,
trust browser headers, or put this file in the DB/backup/container image.
IMPORTANT: a `ready\n` marker is NOT an authenticated complete
erasure journal. No automatic promotion, no end-to-end worker/ingress
fencing, no signed high-watermark or full #141 closure.


## Recovery-gated in-process retention worker (#141 / ADR 0020)

`RetentionSweepService` MUST invoke `RecoveryGuardedRetentionCycle`
instead of calling `PostgresRetentionRepository.PurgeBatchAsync`
directly. Recheck the SAME external `RecoveryQuarantineGate.IsReady`
BEFORE EVERY batch (max 10 x 500). Missing/quarantined/insecure
marker blocks new retention SQL work; a changed marker between two
batches MUST stop the second. Continue to use the restricted
`omnia_retention` login. No direct table privileges or PII logs.
Do not claim atomic cancellation of an already-running SQL query,
DB-level fencing, control of offline CLI/workers, replicas or
providers. The release marker remains unauthenticated; #141 OPEN.


## Recovery journal verification slice (#141 / ADR 0021)

The offline `RecoveryJournalVerifier` checks strict bounded JSONL:
contiguous sequences starting at 1, unique event UUIDs, exact v1
schema, authenticated SHA256 chain and a separately HMAC-authenticated
checkpoint (distinct key and trust domain). The read-only CLI
`src/backend/Omnia.RecoveryAudit` MUST fail closed on missing,
truncated, invalid or insecure private files, and it MUST NOT log
actor identifiers, keys or personal data. Do not use it as an
automatic API release authorization. A valid checkpoint only detects
rollback if it is independently durable and newer than the restored
backup; rolling back BOTH artifacts remains undetectable.
No signed production issuer, external append-only ledger, watermark
authority, traffic gate coordination, erasure replay or GDPR sign-off.


## Recovery journal local writer prototype (#141, ADR 0022)

`RecoveryJournalLocalAppender` is NOT a production erasure-event
publisher: it is an append-only synthetic on-disk harness. It MUST
verify old journal + separately keyed checkpoint BEFORE appending,
reject partial/missing/tampered state and stale exclusive lock,
and flush the new journal record before publishing the checkpoint.
Crash between both operations leaves a mismatch and MUST fail closed,
not auto-repair, truncate or silently authorize `ready\n`. Never
put its keys or actor identities in logs, Git, Pages or artifacts.
CI `append-journal` accepts only known fixture actor/test source DB.
Local private files do NOT establish independent durable custody,
distributed locking, true fsync of directories, request authorization,
transactional DB outbox, cross-host monotonic watermark or GDPR erasure.
Do not wire an unauthenticated writer to the real API / EraseAsync.


## Transactional erasure outbox (#141 / ADR 0023)

ALL authenticated primary erasures in PostgresPersonalDataService
MUST insert one `erase-actor` row into `public.erasure_outbox`
IN THE SAME actor-scoped, PostgresActorLock-protected transaction
as the DELETE from consent_grants and memory_entries. NEVER publish
to a filesystem/network before commit, and NEVER treat an event
created without confirmed commit as a deletion receipt.
The runtime must have column-restricted INSERT only, NO SELECT
or UPDATE/DELETE/TRUNCATE and NO timestamp override. FORCE RLS
must deny wrong-actor insertion; PostgresRuntimeGuard.VerifyApiAsync
enforces the API's narrow grant separately from backfill/audit.
Any outbox error MUST roll back both original DELETE operations.
Zero-row erasure may still create a distinct event (no idempotency
key yet). The outbox is INSIDE PGDATA: no durable offsite journal,
no anti-rollback guarantee. Actor IDs are personal metadata and
require retention/data-rights review; DO NOT claim GDPR completion.
