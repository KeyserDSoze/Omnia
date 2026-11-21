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


## Read-only erasure dispatcher and synthetic delivery (#141 / ADR 0024)

The outbox dispatcher role is separate from API/retention/backfill/audit
and has NO direct SELECT/INSERT/UPDATE/DELETE on personal tables.
An explicit migration opt-in grants it ONLY EXECUTE of the bounded
SECURITY DEFINER `omnia_read_erasure_outbox` function (max 100 rows,
no arbitrary actor parameters). The owner-specific SELECT policy
is necessary because FORCE RLS applies to schema owners, too.
Never grant this function to PUBLIC or use privileged credentials
for the dispatcher. `PostgresErasureDispatchReader.VerifyRoleAsync`
must reject API, retention, audit and backfill identities.

Current `PostgresErasureOutboxLocalPublisher` is test-only and
NOT hosted in API or an autonomous service. It rescans from the
start and uses the COMMITTED outbox UUID to deduplicate at the
local HMAC sink. Timestamp/id cursor is NOT a commit-order watermark
or durable ACK. Do not persist a cursor as delivery confirmation.
An event re-delivered with a conflicting UUID/actor MUST fail closed.
Never log personal actor metadata or journal keys, and never claim
external immutable storage, exactly-once, or release authorization.



## Independent PostgreSQL witness proof (#141 / ADR 0025)

The witness SQL in tests/restore/independent-witness.sql is CI-only.
The PostgreSQL cluster MUST have distinct system_identifier/PGDATA,
roles and passwords, without shared volumes with the primary DB.
Writer has EXECUTE-only append; reader has EXECUTE-only bounded read.
Never expose witness login credentials to API clients or web bundles.
The witness append sequence is allocated transactionally under a
singleton row lock, not nextval. A duplicate UUID with conflicting
actor/type must fail closed. The publisher is ONLY called by test
code and must rescan from the beginning because source timestamps
are NOT commit-order watermarks or durable ACKs. A second PGDATA on
one CI host does NOT prove offsite durability, authority, complete
GDPR erasure, remote key custody or safe release after PITR.



## Same-event witness reconciliation on physical PITR (#141 / ADR 0026)

The CI PITR script must invoke real actor-scoped EraseAsync AFTER its
named restore point, committing consent/memory DELETE plus outbox.
Deliver that committed outbox event to independent witness PGDATA
BEFORE the actual PITR. The recovered database MUST have the actor's
memory resurrected, but the outbox erasure event gone. Before any
replay, verify a dedicated witness reader, a bounded scan and exactly
one match for the hard-coded synthetic actor. Invalid witness role
must fail BEFORE deleting. Repeat replay to check idempotent result;
verify unrelated tenant remains intact, restored actor export empty.
Only loopback test-only connections and no HTTP release endpoints.
This does NOT prove capture of erasures that were not delivered
before source loss, nor production DR, crypto trust or GDPR compliance.

## Isolated CI-only retrying witness publisher (#141 / ADR 0027)

`Omnia.ErasurePublisher` is NOT an HTTP API service or a production
deletion guarantee. For this slice it MUST require CI=true plus explicit
`OMNIA_ERASURE_PUBLISHER_CI_ONLY=1`, and accept ONLY the fixed loopback
synthetic databases/ports/roles (primary omnia_test:15432/omnia_dispatcher,
witness omnia_witness:15436/omnia_witness_writer). Do not expose
credentials, actor IDs or SQL exception details in logs. Verify both
EXECUTE-only identities before any dispatch. `--once` is for CI smoke;
normal operation continuously rescans the full bounded source outbox,
retrying errors with backoff while no local cursor or success ACK is
advanced. The witness's transactional UUID/sequence receipt is the only
persisted acknowledgement, surviving retry/worker restart IF its
separate PostgreSQL cluster survives. UUID conflict must fail closed.
Do not persist a timestamp/id cursor as commit-order or delete source
outbox rows on acknowledgement. Bounded scans >1000 must fail and signal
an operator; the loop may retry but does not prove complete delivery.
This does NOT cover the primary-outbox commit-to-witness loss window,
offsite/WORM/vault/anti-rollback, traffic release, external stores,
consent/legal verification or GDPR readiness. #141 remains OPEN.

## Read-only witness prefix checkpoint audit (#141 / ADR 0028)

`PostgresErasureWitnessSnapshot` MUST use only the restricted witness
READER identity, one REPEATABLE READ + READ ONLY transaction, strict
sequence/UUID checks and a bounded 10000-entry scan with an extra-row
overflow probe. Never read witness tables directly from the API. The
version-1 `WitnessCheckpoint` authenticates the exact 1..N witness
prefix (UUID, tenant, subject, event type) by ordered SHA-256 links and
a separate HMAC-SHA256 checkpoint key, with strict canonical JSON and
constant-time tag comparison. Output checkpoint contains only count,
digest and MAC; it is NOT proof of complete source-to-witness delivery.
An earlier signed prefix may be valid even when there are later
UNATTESTED events. A saved checkpoint detects witness rollback only
if its storage, monotonic freshness and key custody truly survive that
rollback. NEVER issue a new checkpoint after disaster to replace the
last independently trusted checkpoint; never auto-authorize `ready`.
`Omnia.WitnessAudit` is CI-ONLY (CI=true plus explicit opt-in, fixed
loopback/DB/reader login), offline and no HTTP. Issue only NEW private
checkpoint files; verify requires separately private 32-byte key
file and role guard; fail closed on missing, truncated or tampered files.
No production WORM/offsite/vault, signer authority, high-watermark,
global release gate, source completeness, or GDPR approval. Keep #141 open.

## Third independent synthetic checkpoint anchor (#141 / ADR 0029)

`tests/restore/independent-anchor.sql` MUST be used ONLY on a third
disposable PostgreSQL 17 cluster at 127.0.0.1:15438, database
`omnia_anchor`, with a DIFFERENT `system_identifier`, PGDATA, roles
and randomized credentials from both primary and witness. No production
or remote host selectors. The separate restricted `omnia_anchor_writer`
has EXECUTE only on a fixed `anchor.advance_checkpoint(integer,bytea)`,
and `omnia_anchor_reader` only on `anchor.current_checkpoint()`.
Neither can access raw anchor tables. SECURITY DEFINER functions require
fully qualified names, locked singleton head, strictly increasing counts,
immutable append-only history, idempotent exact-retry and conflict
denial; no default PUBLIC grants. Always validate roles INSIDE every
standalone .NET adapter operation, not only in the issuer. Never trust
schema-owner/admin as normal reader/writer.

`PostgresWitnessAnchorIssuer` MUST verify the existing third-cluster
HMAC checkpoint AGAINST the full bounded repeatable-read witness snapshot
using externally supplied 32-byte signing key BEFORE issuing a newer
signed prefix. If the prior anchor is unavailable, stale, invalid or
higher than witness, fail closed; never auto-reanchor after disaster.
`PostgresWitnessAnchorVerifier` is strictly read-only and does not
promote a recovery `ready` marker. No unverified new signing,
plaintext actor identifiers in anchor, source outbox pruning, timestamp
commit cursor or API release. The rollback-negative integration test
uses a truncated *in-memory* witness snapshot, not a real witness PITR.
The 3 databases are still on ONE temporary CI runner: privileged owner
rollback of the third cluster or its loss, a stolen signer key, source
erasure before first delivery, source-vs-witness completeness and
third-party store reconciliation all remain OPEN. Do not claim actual
offsite monotonic custody, GDPR compliance or full #141 completion.

## Mandatory explicit anchor bootstrap (#141 / ADR 0029 hardening)

Ordinary `PostgresWitnessAnchorIssuer.IssueAsync` MUST refuse a
MISSING third-cluster anchor head; never interpret absence as proof of
a clean/empty history after disaster. Only the separately named
`BootstrapAsync(signingKey, operatorApproved: true, ct)` may issue the
FIRST synthetic CI checkpoint, and MUST fail if a head already exists.
`operatorApproved` in this test harness is NOT authenticated operator
identity or a production release decision. Do not expose it via API,
user input, or scheduled worker. Lost/truncated anchor requires
out-of-band investigation, not re-bootstrap or auto-`ready`.

## Actual physical witness PITR against surviving third-cluster anchor (#141 / ADR 0030)

The synthetic `physical-witness-pitr-rehearsal.sh` MUST run only on
disposable GitHub Actions with 127.0.0.1 fixed PostgreSQL 17 clusters.
The WITNESS (not the primary) must have WAL archiving enabled; take
`pg_basebackup` and verify its manifest before post-backup WAL writes.
Create a named restore point, append a synthetic erasure through the
restricted witness writer, then advance the HMAC checkpoint stored in
a NEW database of the third PGDATA after verifying the old prefix.
Recover a physical witness clone with `recovery.signal` and
`recovery_target_name` set BEFORE that append, assert before/after
WAL markers and that the post-point event is missing, and assert source
and recovered system_identifier MATCH while third-cluster identifier
differs. Rotate the clone reader password before accessing it.
The read-only verifier MUST reject the recovered witness against the
PRE-EXISTING third-cluster anchored prefix, while accepting the live
witness. Verify the third-cluster count and exact bytes are unchanged
after recovery. Never bootstrap/reanchor after the rollback, mutate
the original anchor, use privileged actor reads, or promote `ready`.

The separate test CLI `Omnia.WitnessPitrRehearsal` MUST require
`CI=true`, `GITHUB_ACTIONS=true`, explicit opt-in and exact
loopback/DB/SQL role restrictions; it may create only a known synthetic
witness erasure in its `--append-and-anchor` phase. It MUST NOT be
hosted by the API. Keep signing keys/private backups inside ephemeral
0700 runner temp, no PII/log secrets and no GitHub artifacts.
The third database still shares the same CI host and administrator:
not an offsite immutable high-watermark, signer vault, global traffic
fence or evidence that EVERY primary erase reached the witness before
a crash. #141/#109/#31 remain OPEN.

## Live primary-outbox → witness receipt preflight (#141 / ADR 0031)

`PostgresErasureLiveDeliveryAudit` runs READ-ONLY and must validate
`omnia_dispatcher` EXECUTE-only SOURCE identity and independent
`omnia_witness_reader` EXECUTE-only WITNESS identity on EVERY audit.
Read the live primary outbox via only the bounded
`public.omnia_read_erasure_outbox` SECURITY DEFINER function inside
ONE `REPEATABLE READ` / `SET TRANSACTION READ ONLY` session, with
created_at+UUID pagination (never persist as commit-order cursor),
UUID uniqueness and a 1000-event bound plus overflow probe. For every
committed event, query its remote witness receipt by UUID and compare
UUID, tenant, subject, event type and positive witness sequence
exactly, rejecting missing/conflicting entries. No direct table
privileges or write-side ACKs; no actor IDs, SQL errors or credentials
in logs. `VerifyKnownEventAsync` checks a known live committed UUID;
`VerifyAllAsync` checks every row in the bounded source snapshot.
After xUnit + witness publisher, CI runs the offline
`Omnia.ErasureDeliveryAudit --all` tool, requiring CI, GitHub Actions,
explicit opt-in and fixed loopback DB/roles. Wrong source/witness
credentials or disabled opt-in must fail. The tool never grants
`ready` or touches HTTP/worker service state.
CRITICAL: a clean LIVE outbox scan covers only retained source rows
at its snapshot. It CANNOT prove that an erased outbox event never
existed on a restored/rolled-back primary, nor does it rule out
future concurrent commits or guarantee producer-before-crash delivery.
Keep #141, #109 and #31 OPEN; require independent durable commit
sequence/trust authority, ingress/worker fencing, offsite/WORM/vault
and GDPR review before claiming production recovery safety.

## Physical primary PITR with a committed but NEVER witnessed erasure (#141 / ADR 0032)

The real `tests/restore/physical-pitr-rehearsal.sh` now exercises a
SECOND distinct synthetic actor. Seed its memory and consent BEFORE
`pg_basebackup`, then after the named WAL restore point and AFTER the
last source→witness publisher cycle, commit `EraseAsync` via real
actor-scoped service. Verify that the live primary has NO memory for
that actor and DOES have its committed outbox UUID, while the witness
EXECUTE-only reader has NO acknowledgement. Save only that synthetic
UUID in a private 0600 file under the ephemeral 0700 runner directory
(outside source PGDATA, not in logs/artifacts). NEVER claim this
unsigned ephemeral file is a trusted external commit witness.

After a REAL physical source PITR, the recovered primary MUST have
the actor memory+consent resurrected and its committed outbox UUID
ABSENT; witness must STILL lack that UUID. A bounded, read-only
`PostgresErasureLiveDeliveryAudit.VerifyAllAsync` may misleadingly
return success on the remaining source outbox. This must NEVER be
treated as proof of historic completeness or recovery release.
`Omnia.UnwitnessedPitrRehearsal` is test-only: fixed
127.0.0.1:15432/15435 dispatcher identities and 15436 witness READER,
`CI=true`, `GITHUB_ACTIONS=true`, explicit opt-in and known private
synthetic UUID are required. Negative test mode `--live-gap` confirms
a committed pending ACK; `--recovered-gap` confirms the false-clean
risk after PITR. It MUST NOT emit a ready marker, write SQL, or
print UUIDs, tenant data, secrets or raw exception details.
Rotate the recovered dispatcher credential, assert old credential
denial and API quarantine 503/startup denial even after the
source-only scan appears clean. Witnessed replay of a DIFFERENT actor
MUST NOT silently erase the lost-ACK actor: it remains unresolved.
No release after DR until independently authenticated source-commit
provenance and completeness exists. #141/#109/#31 remain OPEN.

## Synthetic PRE-ERASE remote intent, never commit proof (ADR 0033/#141)

`witness.erasure_intents` is a SECOND failure-domain PostgreSQL 17 CI
prototype for an **intent/request before DELETE**, NOT an `erasure_events`
post-commit receipt. Intent UUID differs from the later source outbox
UUID and may exist even when primary `EraseAsync` never commits.
Only `omnia_witness_intent_writer` may EXECUTE
`witness.append_erase_intent(uuid,varchar,varchar)`, only
`omnia_witness_intent_reader` may EXECUTE the find/list functions.
Neither role may access raw witness tables or existing completed
erasure receipts; owner, admin, app runtime and original witness
writer/reader MUST be rejected inside each .NET adapter operation.
Use `FOR UPDATE` on `witness.intent_head` for gapless idempotent
request sequencing and reject same UUID with changed tenant/actor.
Never log tenant/actor identifiers, UUIDs, or credentials; limit
list reads to 100. CI should check the independent PGDATA
`system_identifier` and real PostgreSQL role ACLs.

Physical source PITR test MUST record a synthetic intent with a
separately randomized UUID in the WITNESS PGDATA **before**
`PostgresPersonalDataService.EraseAsync`. Denied remote writer or
missing opt-in MUST abort before the source actor is touched.
It must then commit the synthetic primary erase OUTSIDE the last
publisher cycle (no completed witness ACK), restore the source
physical WAL to a point before the DELETE, and show the
pre-erase INTENT survives in the SECOND PGDATA even when the
source outbox and completed witness receipt are BOTH absent.
It MUST NOT infer from the intent that the source DELETE succeeded,
that the request was authorized by a real user, or that recovery
can delete the actor; all such cases require an independent
authorization and commit proof. No API hook, automatic reconciliation
or signed `ready` release is provided. Intent PGDATA itself can
roll back (not third-cluster-anchored), and the whole test shares
one CI host. Keep #141, #31, #109, #32 OPEN.

## Correlated remote intent UUID and source erasure event (#141 / ADR 0034)

The opt-in `PostgresPersonalDataService.EraseWithWitnessedIntentAsync`
is STRICTLY for synthetic CI experiments (`CI=true` and
`GITHUB_ACTIONS=true`) and MUST NOT be wired into the public API.
It requires a nonempty request UUID and validates the independent
`omnia_witness_intent_reader` EXECUTE-only login and exact
`requestId`, tenant, owner and `erase-actor-intent` BEFORE
opening any primary actor transaction. A missing, foreign-scope
or forged intent MUST NOT delete memory/consent or enqueue an
outbox event. The SAME request UUID must be inserted into
`public.erasure_outbox.id` IN THE SAME actor-scoped transaction
that deletes consent and memory. The existing uncorrelated public
`EraseAsync` MUST keep its old behavior, and the existing
bounded source→witness publisher must preserve the same event UUID
in `witness.erasure_events`.

Do NOT `ON CONFLICT DO NOTHING`, silently re-delete or replay
old intents: duplicate source UUID MUST throw 23505 and rollback
all changes in the second source transaction, including attempted
deletion of any newer data. Do not auto-retry ambiguous commits.
The real CI primary WAL PITR now uses the pre-intent UUID to call
the correlated synthetic delete, compares live source outbox UUID
against the immutable independent intent UUID BEFORE PITR, then
proves the source event can disappear while the pre-intent persists
on the SECOND PGDATA. The pre-intent still NEVER proves source COMMIT
once source WAL rolls back, and a missing post-commit ACK must keep
API in quarantine. Synthetic CI file with UUID is NOT an
independent signed commit authority. Third anchor does not cover
this intent and no OIDC actor identity is verified here. Keep
#141/#109/#31/#32 open; no production release or GDPR assurance.

**PITR caveat:** source UUID uniqueness prevents stale retries ONLY while the original source outbox exists. Once primary PITR loses that row, the unique constraint CANNOT remember the prior commit. An orphaned remote intent MUST NEVER trigger automatic re-deletion on a recovered primary. Keep API quarantined pending independent commit provenance and authorization.

## Read-only three-way erasure visibility inspection (ADR 0035/#141)

`PostgresCorrelatedErasureReconciliation` is CI-ONLY, invoked via
`InspectAsync(requestUuid)` with three unrelated EXECUTE-only
identities: `omnia_dispatcher` for primary source, `omnia_witness_intent_reader`
for the remote PRE-intent, and `omnia_witness_reader` for the remote
POST-commit erasure receipt. Reject app, writer, owner and admin SQL
identities before returning any observation. A known UUID must have
a verified remote intent with exact actor scope; a missing, conflicting,
wrong-type or null intent MUST throw and never masquerade as a completed
erase. Source event inspection uses a single bounded
`REPEATABLE READ`/`READ ONLY` primary transaction via the
SECURITY DEFINER dispatch function, with overflow denial above 1000.
Every source and witness UUID, tenant, subject and event type must match
the intent; mismatches must be denied before any result is returned.
The three PostgreSQL datasets are NOT in one distributed snapshot.

Exactly four visibility outcomes are allowed:
`IntentOnlyCommitUnknown`: remote request present, no source or ACK
-- NEVER claim "not committed" since primary PITR can hide an earlier commit.
`SourceEventPendingWitness`: source outbox visible, no remote ACK.
`SourceAndWitnessVisible`: matching source and remote completed event
observable, but NOT global cross-store erasure or user authorization.
`WitnessVisibleWithoutSource`: remote completed receipt exists but
source outbox not visible, e.g. primary PITR or forged witness history.
ALL outcomes explicitly have `MayReleaseRecovery=false` and
`MayAutoReplay=false`. No state, signatures, `ready` markers,
HTTP endpoints, or actor DELETEs can be produced by the inspector.
Physical primary PITR rehearsal MUST transition from pending witness
to `IntentOnlyCommitUnknown` after rollback and leave API quarantined.
The independent witness intent can itself roll back; this is not
offsite monotonic authority or evidence of which source COMMIT occurred.
Maintain #141/#109/#31/#32 OPEN.

## Third-PGDATA HMAC anchor for PRE-ERASE intent prefix (#141 / ADR 0036)

The CI-only pre-intent stream now has its OWN domain-separated
`IntentCheckpoint` (purpose `pre-erase-intent-prefix`,
different genesis, event type, JSON canonical form and signing key
from completed `WitnessCheckpoint`). A checkpoint stores only
count, digest and HMAC; tenant/subject live only in the SECOND PGDATA
witness, not the THIRD PGDATA checkpoint. An intent is NOT an
attestation of the primary DELETE/COMMIT or post-commit witness ACK.

`PostgresPreEraseIntentSnapshot.ReadAsync` MUST require the independent
`omnia_witness_intent_reader` EXECUTE-only identity, scan the
witness under ONE PostgreSQL REPEATABLE READ + READ ONLY transaction,
enforce a continuous sequence, UUID uniqueness, max 10000 intents
plus a one-row probe to reject overflow, and never read raw tables.
`PostgresPreEraseIntentAnchorIssuer` MUST verify an already signed
prefix against the current intent snapshot BEFORE advancing the
external monotonic checkpoint. A missing anchor is NOT proof of empty
history: ordinary IssueAsync must deny; BootstrapAsync needs explicit
synthetic operator approval only at first-time initialisation.
The existing third-cluster `anchor.advance_checkpoint` SQL enforces
non-decreasing count and byte-for-byte replay at equal count, with
EXECUTE-only roles and no direct table grants. Isolate anchors in
separate databases on the THIRD PGDATA: `omnia_anchor` for completed
erasure events and `omnia_intent_anchor` for xUnit intent prefix,
with distinct signing keys/payloads. Never mix the formats, silently
re-bootstrap, overwrite old checkpoints, or auto-issue after PITR.

The real WITNESS physical WAL PITR rehearsal must create yet another
third-cluster database `omnia_witness_pitr_intent_anchor`, seed an
intent BEFORE basebackup, issue an explicit intent checkpoint, append
and anchor a new intent AFTER the named WAL restore point, recover a
physical witness clone missing that intent while leaving THIRD PGDATA
untouched, rotate the recovered intent-reader credential and confirm
the OLD credential fails. A read-only verifier MUST REJECT the
recovered intent prefix yet ACCEPT the live stream, and compare exact
third-cluster checkpoint count and bytes before/after the physical
recovery. It must still refuse recovery `ready`, actor re-deletion
or replay of ambiguous requests. The separate anchor shares the same
CI host/DB admin, can itself be rolled back, and new intents can be
lost before the anchor advance: no offsite/WORM commit authority,
global release gate, real OIDC authorization or GDPR readiness.
Keep #141, #109, #31 and #32 open.

## Anchored PRE-ERASE intent required before synthetic source DELETE (#141 / ADR 0037)

The strict `PostgresPersonalDataService.EraseWithAnchoredIntentAsync` is an
experimental GitHub-CI-only method (checks `CI=true` and
`GITHUB_ACTIONS=true`, which are **NOT** a production authentication
mechanism). It is NOT registered with the REST API or the
`IPersonalDataService` port and cannot act on real user content.
Its input is request UUID, tenant, owner, restricted intent-reader
connection to SECOND PGDATA, restricted checkpoint-reader connection
to THIRD PGDATA, and a private 32-byte HMAC signing key. It MUST
refuse an absent intent, missing third-cluster anchor, invalid HMAC,
incorrect tenant/owner, wrong SQL role, or an intent with sequence
**GREATER THAN the authenticated high-watermark**. The exact actor
must be located within the same REPEATABLE READ witness snapshot
used to verify the entire anchored HMAC prefix; the same verified
request UUID is then inserted into the source erasure outbox in the
transaction that removes synthetic memory and consent. Denied
preflight MUST NOT open a source actor transaction or change an
outbox row, memory or consent. A duplicate UUID on a still-intact
primary outbox MUST cause 23505 and rollback any attempted later
deletion. After source PITR that UUID may disappear, so NEVER replay
automatically. The earlier weaker CI-only
`EraseWithWitnessedIntentAsync` remains for backward-compatible
tests and is NOT an anchored operation. Do not treat it as the new gate.

In real physical PRIMARY WAL PITR drill, provision an isolated
`omnia_pitr_preflight_anchor` database on the THIRD PGDATA; use
separately randomized 32-byte HMAC key held in a 0600 temporary
runner file. Require a denied pre-delete invocation after remote
intent append but BEFORE checkpoint issuance, and prove source
memory and outbox unchanged; only after explicit third-cluster
checkpoint ACK may the correlated synthetic DELETE succeed.
The old reader/writer identity and missing/privileged credentials
must be rejected; witness post-commit ACK is deliberately absent.
After physical PITR the actor data must resurrect and the outbox
disappear while the separately verified intent prefix/third PGDATA
checkpoint remains unchanged. API remains quarantined. This
demonstrates the pre-ack gate only within CI and DOES NOT confer
a source COMMIT certificate, independent offsite/WORM custody,
user-signed OIDC consent or global ingress/worker/replica fencing.
The two remote databases do not share a serializable distributed
transaction; concurrent recovery or a rollback of the THIRD
cluster can invalidate the safety assumption. #141 and GDPR/DR
dependencies remain OPEN.

## One-shot independent synthetic erasure attempt (ADR 0038 / #141)

The CI-only `PostgresPersonalDataService.EraseWithOneShotAnchoredIntentAsync`
adds a THIRD-PGDATA, one-time `admission.execution_claims` record
**after** authenticating a matching, already-anchored remote pre-intent,
but **before** opening any primary actor-scoped transaction. The
dedicated `omnia_claim_writer` EXECUTE-only role may call
`admission.claim_once(uuid,bytea)` but MUST NOT read/modify raw tables,
issue HMAC checkpoints or completed-erasure witness receipts. An
independent `omnia_claim_reader` may call only
`admission.has_claim(uuid)`. The schema's unrevocable-per-user
one-shot UUID PK is a fail-closed attempt record; any reuse MUST
raise 23505, including an identical request. A concurrent eight-way
same UUID claim must commit exactly once. The stored scope HMAC
uses a domain-separated canonical payload; no raw actor or tenant
text goes into THIRD PGDATA, but digest/request UUID are STILL
regulated metadata. All SECURITY DEFINER routines must be
created and have PUBLIC EXECUTE revoked in one transaction.

**Critical semantics:** claim present means only "an attempt
was admitted before a possible source DELETE". It is NOT a
source commit receipt, not evidence of GDPR completion, and
not proof of a legally authorized actor. A failed/ambiguous
primary commit permanently consumes the UUID; NEVER retry,
auto-replay, report "erased" or issue recovery ready.
The claim writer alone can deny/consume a request: never
treat CI SQL access as OIDC user authorization, and
restrict/rotate real credentials in any future design.
One-shot safety persists ONLY while this THIRD PGDATA and
the HMAC key remain independently trustworthy; both still
share one disposable CI host and admin with the primary/witness.

The physical primary WAL PITR rehearsal MUST use a separate
`omnia_pitr_execution_claims` DB on the THIRD PGDATA. It
must first deny an unanchored intent, then register a
one-shot claim and locally commit synthetic actor DELETE
and same UUID outbox after independent intent checkpoint ACK
but before completed witness ACK. It must prove the attempt
record survives the physical source rollback and actually
invoke the strict method on the promoted recovered primary:
the source outbox UUID has vanished, yet the third-cluster
claim MUST reject the reused request with 23505 BEFORE any
re-deletion. The resurrected synthetic memory+consent and
empty outbox must remain intact; API stays quarantined.
Keep #141/#109/#31/#32 open; do not integrate this test
method into REST/API or claim offsite WORM/commit certainty.

## Authenticated one-shot attempt scope / physical PITR (ADR 0039, #141)

The CI-only third-PGDATA `admission.execution_claims` is **not**
trustworthy based solely on UUID presence. For read-only verification use
`PostgresPreEraseExecutionClaimReader.VerifyClaimAsync(uuid,tenant,owner,key,ct)`,
which requires `omnia_claim_reader` (not the writer or DB admin) and
reads only `admission.claim_scope(uuid)` through EXECUTE-only grants.
The function must be created with `SECURITY DEFINER`,
`search_path=pg_catalog,pg_temp`, and REVOKE PUBLIC/GRANT reader
in the SAME SQL transaction as CREATE. Never grant raw table SELECT.
`omnia_claim_writer` MUST NOT receive execute on `claim_scope`.
The writer and verifier MUST share one canonical domain-separated
HMAC-SHA256 payload for UUID, tenant, owner; the verifier must compare
32-byte HMAC using `CryptographicOperations.FixedTimeEquals`
and zero temporary digests. Missing UUID, wrong actor, wrong signing
key, tampered HMAC, unauthorized SQL identity: fail closed.
`HasClaimAsync` remains only a boolean diagnostic (not proof of scope).

The real physical PRIMARY PITR rehearsal must use the privately
supplied `OMNIA_PREERASE_PITR_HMAC_KEY_PATH` and restricted
`OMNIA_PREERASE_PITR_CLAIM_READER_DB`. Verify no claim exists before
the one-shot attempt, then verify the exact tenant/actor HMAC and
reject foreign actor/reader-writer role confusion AFTER source COMMIT
and AGAIN after the promoted primary clone has lost its source outbox.
This read-only evidence still never authorizes `ready` or DELETE.
A claimant with DB superuser access can rewrite the HMAC and a
claim can exist without a source commit; none of this is WORM,
offsite custody, user OIDC authentication or GDPR Art.17 compliance.
Keep #141/#109/#31/#32 OPEN.

## Fourth physical PostgreSQL one-shot attempt fence (ADR 0040/#141)

The CI-only `EraseWithFourthFenceAsync` method must first validate
an HMAC-anchored remote pre-intent on witness/third PGDATA; **then**
commit a domain-separated one-shot UUID/scope-HMAC claim on a distinct
FOURTH PGDATA using `omnia_claim_writer` EXECUTE-only, **then**
commit the corresponding THIRD-PGDATA claim, **then** open the primary
actor-scoped DELETE + same-UUID outbox transaction. The FOURTH attempt
is **not a source COMMIT certificate**, and a failure anywhere after
it is consumed leaves the request UNCERTAIN, never automatically
replayed or marked erased. The fourth SQL writer must not be the third
SQL connection. Both claim databases use the restricted
`admission.claim_once` and `admission.claim_scope` functions,
without raw table grants. The fourth store is a separately initialized
PostgreSQL 17 instance with a distinct `system_identifier` and port
15440; all PGDATAs still share one ephemeral CI host and common
container administration, NOT offsite/WORM.

The TDD integration test MUST (a) reject a fourth reader masquerading
as writer before any mutation, (b) prove a successful single attempt
on fourth+third and source UUID outbox, (c) simulate loss of the
third-PGDATA claim via a TEST superuser, (d) show that the fourth
claim remains HMAC-verifiable and rejects an identical request with
23505 before deleting new source data, and (e) show that a fourth
claim may be stranded if third write fails, with no automatic retry.
A separate TRUE PHYSICAL WAL PITR rehearsal MUST take a
`pg_basebackup` of THIRD PGDATA before a named restore point and
commit a dedicated synthetic attempt fourth-then-third afterward;
the promoted third clone must lack its original claim but the live
fourth must still contain its authenticated scope. The CLI must
use EXECUTE-only readers, rotate recovered third-reader credentials,
reject stale passwords and repeated fourth writes, keep actor data
untouched, and **never** emit recovery-ready. Original third
should continue to verify the claim. Keep #141/#109/#31/#32
OPEN. A simultaneous PITR of fourth and third, loss of the fourth
acknowledgment, credential compromise or shared host/admin recovery
may still defeat this synthetic anti-rollback; fix requires independently
administered immutable offsite custody, authenticated real requester,
primary COMMIT provenance, global traffic gates and legal review.


## Complementary fourth-PGDATA physical PITR negative gate (#141 / ADR 0041)

`tests/restore/physical-fourth-claim-pitr-rehearsal.sh` is CI-only, after
ADR 0040's THIRD-PGDATA drill. It physically rolls back FOURTH using a
verified basebackup, named WAL target and promotion, while THIRD retains the
actor-bound claim. With reissued restricted credentials on the restored
FOURTH clone, replay may re-consume FOURTH but MUST fail at THIRD with
PostgreSQL 23505 **before any primary DELETE**. Check original passwords
are denied, marker order and authenticated claim scope. No real data,
source deletion, API promotion or auto-retry. If both THIRD and FOURTH
are rolled back, or a source COMMIT is lost/uncertain, these claims do
NOT certify deletion. No offsite/WORM custody or production permission.
#141/#109/#31/#32 remain open.

## Simultaneous THIRD+FOURTH physical PITR counterexample (#141 / ADR 0042)

The CI-only `tests/restore/physical-dual-claim-pitr-rehearsal.sh`
performs TWO verified basebackups and named-WAL PITRs to a point before
the SAME HMAC-scoped request UUID was claimed on both PGDATAs. The
promoted clones use ports 15443 and 15444 and rotated restricted
credentials. Both original clusters must still authenticate the
request. Both recovered claim writers can RE-CLAIM the same UUID:
this is intentionally an **unsafe replay counterexample**, NOT a
defense or a production success. The CLI must return NONZERO 42 with
`DUAL_PITR_BOTH_CLAIMS_CAN_BE_REUSED_NO_RELEASE` and the shell must
interpret only that exact result as a successful NEGATIVE regression;
API, DELETE, worker and release state remain untouched/quarantined.
No offsite immutable authority, postcommit source attestation or
simultaneous rollback protection exists. #141 and dependencies OPEN.

## Immutable version-pinned attempt evidence contract (#143 / ADR 0043)

`ImmutableAttemptEvidence.VerifyAsync` is a read-only boundary,
NOT permission to claim/delete/replay/recover. A valid observation
MUST pin a known independently provisioned store identity AND exact
object version, reject "latest"/missing version/incorrect key, require
S3 COMPLIANCE or an actually locked Azure WORM policy with unexpired
retention, validate canonical bounded schema and two distinct 32-byte
scope/evidence HMAC keys. The contract's provider metadata is UNTRUSTED
unless a separately tested implementation obtains real version-level
lock status. In-memory/fake readers and `CreateSynthetic` are test-only,
NEVER offsite/WORM evidence. No real providers, version pin authority,
OIDC authorization, source COMMIT proof or global release exist yet.
Keep #141/#109/#31/#32 OPEN. Do NOT wire this observer into REST
or treat a valid receipt as GDPR erasure evidence.

## REAL S3 Object Lock provider read-only slice (#145 / ADR 0044)

`Omnia.ObjectStore.S3.S3ObjectLockAttemptReader` is an AWS SDK v4
adapter for the ADR0043 observer. The adapter MUST pin the SAME
exact non-null/non-latest VersionId on BOTH GetObjectRetention and
GetObject, require per-version retention mode COMPLIANCE and UTC date,
reject mismatched GetObject response version, enforce strict payload
size on both ContentLength and streaming (512+1 probe), and reject
missing/unauthorized/governance/expired/invalid foreign keys.
Do not use a bucket default retention as substitute for per-version
retention. The reader has NO PUT/DELETE or ready/retry path and
is NOT registered in the API. The SeaweedFS protocol test is
CI-only same-host synthetic S3; even a real S3 COMPLIANCE status
does NOT prove offsite independent custody or source DELETE COMMIT.
#144/#141/#109/#31/#32 remain OPEN. No production parity with
MinIO/Azure; do not turn this into a release gate prematurely.

## Azure Blob WORM read-only SDK adapter (#146 / ADR 0045)

`AzureBlobWormAttemptReader` MUST use an explicit UTC timestamp
version ID and ONE version-scoped `BlobClient.WithVersion` for both
GetPropertiesAsync and DownloadStreamingAsync. Require HEAD VersionId
equals exact pin, version-level ImmutabilityPolicy.PolicyMode Locked,
non-null UTC ExpiresOn, size <=512, and GET VersionId/ContentLength/ETag
agree with HEAD. The 513-byte buffer denies lying or oversized bodies;
null/latest/unlocked/absent/foreign versions, missing policy, 403/404,
bad HMAC and old retention fail closed. Azure SDK HTTP scripted tests
are NOT actual Azure WORM service tests and cannot attest WORM,
IAM, account ownership, version catalog or offsite custody. Do not
register the adapter in REST/DELETE or authorize recovery ready:
#144/#141/#109/#31/#32 remain OPEN.

## S3 Object Lock conditional write & uncertain ACK (#147 / ADR 0046)

`S3ObjectLockAttemptPublisher` is a *testable* read/write
boundary, not an actor DELETE or recovery authorization.
It MUST verify canonical UUID/tenant/owner and 2-key HMAC
payload bytes before any S3 request, reject short/invalid
UTC retention, perform exactly one conditional
`PutObject(IfNoneMatch="*", ObjectLockMode=COMPLIANCE,
ObjectLockRetainUntilDate UTC, Content-MD5)` with an SDK
client configured `MaxErrorRetry=0`, and only return
`Observed` after verifying the returned exact VersionId
with the read-only S3 COMPLIANCE observer. Any lost ack,
timeout, conflict 409/412, null/versionless ACK,
readback untrusted or absent must be `Uncertain`,
NEVER “safe to retry”, “not written”, “erased”, “ready”
or trigger a source DELETE. If-None-Match is current-key
conditional, not cross-PITR anti-rollback; delete markers,
administrative loss, identity compromise and separate
version pin authority remain outstanding. Synthetic
SeaweedFS CI is the SAME runner, not offsite. No
API/DI integration. #144/#141/#109/#31/#32 OPEN.

## S3 writer mode/retention readback hardening (ADR0046/#147)

A write/readback can return `Observed` ONLY when the verified
effective mode is specifically S3Compliance (never AzureLocked)
and effective retention is not materially shorter than the
requested PUT retention (allow less than 1 second for provider
timestamp encoding). The ADR0043 observation must surface
verified mode + retention, not only versionId. TDD MUST prove
RED with provider substitution and shortened retention,
then GREEN on real SeaweedFS and fake error cases. Reject
AWS clients with SDK hidden retries enabled before mutation.
No APIs or production offsite claims; #144/#141 OPEN.

## S3 lost-ACK version-history reconciliation (#148 / ADR 0047)

`S3ObjectLockAttemptHistoryReconciler` is a READ-ONLY
positive-evidence lookup, never an authorization boundary.
It MUST enumerate `ListVersions` for the exact canonical
key (not mutable latest), enforce bounded two-item pages,
at most eight pages, all-page traversal, distinct exact
VersionIds, no wrong-key entries, no cyclic/missing
pagination cursor, delete markers never mistaken for data;
only ONE real S3 COMPLIANCE version may be validated with
`ImmutableAttemptEvidence.VerifyAsync` and two separate
MAC keys. An empty/denied/partial/multiple/foreign/malformed
history MUST return Uncertain, NEVER safe-to-retry or
evidence of absence, primary DELETE commit, or ready.
Real AWS SDK + SeaweedFS test may lose ACK after actual
synthetic remote PUT and recover *prior observation* by
history enumeration, including under a latest delete
marker. This runs on SAME CI runner; does not pin versions
monotonically in independently administered offsite
custody or release the API. #144/#141/#109/#31 OPEN.
