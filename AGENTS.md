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
cd src/mobile && npm ci && npm run typecheck && npm test && npm run build:web
cd ../..
dotnet test tests/Omnia.Domain.Tests/Omnia.Domain.Tests.csproj
docker compose --env-file docker/.env -f docker/compose.yml up --build
# OMNIA_TEST_DB=... dotnet test tests/Omnia.Integration.Tests/Omnia.Integration.Tests.csproj
```

## Frozen npm dependency graph (#188 / ADR 0085)

Every Expo Web/CI/OIDC/native workflow MUST validate
`src/mobile/package-lock.json` using
`node src/mobile/scripts/verify-mobile-lockfile.mjs` and use `npm ci`,
never unconstrained `npm install` in a release gate. The npm v3 lock is
checked into main with exact root manifest, SHA-512 integrity and registry
provenance. The current SDK 57 compatible Reanimated/Worklets/Gesture
Handler peer versions are declared explicitly; do not bypass resolution
with --force or --legacy-peer-deps. Dependency upgrades require a reviewed
lock diff, fresh npm ci, typecheck, Vitest, web export, OIDC real Chromium
and native smoke where relevant. npm ci does NOT prove tarball availability,
native parity or complete supply-chain security. #15 remains open until
NuGet locked restore, toolchain pinning and other DoD gates are real.

## Frozen .NET 10 NuGet dependency graphs (#189 / ADR 0086)

All 20 projects under `src/backend` and `tests` require independent
`packages.lock.json` files, from the official NuGet restore graph.
`Directory.Build.props` enables `RestorePackagesWithLockFile` for every
project and sets `RestoreLockedMode=true` when `CI=true`, including
implicit `dotnet run/build/test` restores. The CI preflight
`python3 tests/quality/verify_nuget_locks.py` rejects missing graph
files, unlocked direct references, mismatched target frameworks and
missing NuGet SHA-512 content hashes; ONLY `dotnet restore --locked-mode`
proves actual graph consistency. Every relevant CI/E2E/Docker API build
must run locked NuGet restore; no `--force-evaluate` in release gates.
Intentional package changes require generating lockfiles locally in a
reviewed change, followed by all tests and real runner verification.
A lock does not freeze .NET SDK/container patch versions, guarantee
NuGet feed uptime or provide software-composition analysis. Keep #15,
#10 and production security/privacy release gates open.

## Exact .NET SDK and Docker runtime patch (#190 / ADR 0087)

The repository root `global.json` MUST select SDK `10.0.404` with
`rollForward=disable`, `allowPrerelease=false`. Both CI and OIDC
Actions must use `actions/setup-dotnet` with `global-json-file`,
execute `dotnet --version` and reject anything other than 10.0.404.
The Docker API builder must use the official Ubuntu Noble patch image
`mcr.microsoft.com/dotnet/sdk:10.0.404-noble`, COPY global.json,
assert the active SDK, and do locked NuGet restore; runtime must use
`mcr.microsoft.com/dotnet/aspnet:10.0.15-noble` with real runtime
framework checks. The fail-closed structural policy under
`tests/quality/verify_dotnet_toolchain.py` is mandatory but is NOT a
substitute for actual tool/image execution. Update all pinned versions
together after official security patch review, not via floating tags
or silent roll-forward. **Version tags can be rebuilt** and are NOT
OCI digest pins. #15 and #10 stay open for digest attestation, scans
and all other release gates; no production readiness claim.

## Backlog e feature management

Leggere `docs/backlog.md` e `docs/product-requirements.md` prima di nuove funzionalità. Non duplicare issue: cercare per area/titolo. Ogni nuova feature usa `.github/ISSUE_TEMPLATE/feature.yml`, include epic parent, phase label, priorità, RED → GREEN → REFACTOR, evidenza commit SHA e URL di Actions. Le 14 epic e 116 issue child non indicano funzionalità completate. Le milestone sono label logiche; GitHub Projects/Milestones non configurati. Prima del lavoro su dati veri completare #29, #30, #31, #32, #33, #34, #36, #39, #41, #43 e test #17.

## New consent guard (#29 slice)

All new memory writes MUST pass an active source-scoped `memory.capture` grant. Never bypass `IConsentGuard` or the transactional PostgreSQL check inside `PostgresMemoryRepository.UpsertAsync`. Never initiate external embeddings before consent + domain payload validation. Tests must include revoked/expired grants and cross-tenant denial. The consent endpoint only records first-party acknowledgement; it does **not** legalize third-party recordings or replace GDPR review.

## Private REST response cache invariant (#173 / ADR 0072)

Every `/api/v1` response MUST set server-origin `Cache-Control: no-store`, `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`, including success, streamed export, auth 401/403, 429 and recovery 503. Use the single `ApiPrivateResponseCachePolicy` BEFORE recovery quarantine and auth; `OnStarting` owns the final headers to prevent endpoint regression. Do not accidentally apply it to `/health`, `/ready` or a lookalike path segment. Browser fetch cache flags do not replace origin response policy. No-store does NOT clear JavaScript Cache API, local files or noncompliant CDN caches; retain full GDPR/consent/retention controls.

## Rate limit invariants (#37 / ADR 0068)

The live `/api/v1` request-rate middleware MUST run AFTER authentication and authorization and use `ApiAtomicRequestRateBudget` to admit actor+tenant windows together. The former `UseRateLimiter` / named policy and standalone `ApiActorRequestRateBudget` plus `ApiTenantRequestRateBudget` are NOT the live middleware; they are retained only for isolated legacy tests. Derive both signed partition keys from validated OIDC claims, never client headers. A 429 must neither spend the other minute budget nor allocate a fresh partition; no body/DB work on denial. Keep strict global/tenant/actor bounded cardinality and fail-fast config. The process-local fixed windows are NOT distributed SaaS quota, WAF, media limit or GDPR readiness.

## Authenticated personal timeline pages (#174 / ADR 0073)

The legacy `GET /api/v1/memories` array contract MUST remain available.
The explicit `GET /api/v1/memories/page` contract returns limited, no-store
`{items,nextCursor}`; max 50, default 10. Cursor is an exact, canonical
UTC ticks+UUID keyset position, NEVER a bearer or tenant selector. Each SQL
query stays in `PostgresActorScope` using claims from signed OIDC/JWT and
FORCE RLS, `expires_at>now()`, parameterized tuple comparison and sealed
payload AAD validation. No OFFSET or skip-based paging. Web older-page loads
must be invalidated on logout, refresh, consent revoke/grant or erasure;
deduplicate by canonical UUID, preserve visible notes/cursor on offline
failure, and never persist personal pages/cursors/tokens in browser storage.
Do not assert immutable multi-page snapshots or native/offline parity.

## OIDC Web authorization source of truth

`src/mobile/src/services/webAuth.ts` is only for browser OAuth and must use a maintained OIDC library, authorization code + PKCE S256, session state/nonce checks, ephemeral in-memory tokens, correct redirect /Omnia/, and strict public HTTPS/loopback config. Do not add localStorage/AsyncStorage for tokens, don't store password/client_secret or trust client-supplied tenant IDs. Web captures must be API-confirmed, error states explicit, and manual scoped consent obtained first. Native OIDC requires separate Expo AuthSession + SecureStore with device smoke before claiming parity.

### Web cloud session safety (#170/#171/#172)
All remote timeline/consent read responses are fenced with `CloudViewEpoch` after signout, new refresh, revoke, erase or unmount. Every API 401 from a current browser session MUST synchronously clear private UI, invalidate outstanding reads and discard local OIDC tokens; 401s from old session generations MUST NOT log out a newer account. Token getter failure must fail closed without dispatching a stale token. Treat 403/429/offline as distinct errors, not proof of expired authentication. **User logout must preempt `busy` mutations**: never deny signout while a POST/export is awaiting a response. Mutation/response JSON and private Blob export must be session-epoch fenced before UI changes, follow-up GET or download. If a real server POST was already accepted, tell the user it may have been processed: logout is not an erasure or rollback. Server-side Keycloak revocation, native device parity and GDPR erasure are separate.

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

## S3 cross-bucket immutable exact-version pin (#149 / ADR 0048)

`S3ObjectLockVersionPinService` signs a minimal canonical
pin envelope containing request UUID, evidence store identity,
exact evidence VersionId, actor-scope HMAC and a dedicated
pin HMAC. Require three distinct 32-byte keys
(scope/evidence/pin), no tenant/subject plaintext in the S3
body. Verify original exact S3 COMPLIANCE evidence
before any pin PUT. Pin's one-shot PutObject uses
conditional IfNoneMatch, COMPLIANCE, MD5 transport
and an AWS client with MaxErrorRetry=0, then verifies
the exact pin VersionId via `S3ObjectLockPinReader` and
the original evidence VersionId; pin expiry must not
precede the evidenced retention. Never read mutable
latest. Any unknown ACK, missing/foreign version,
tampered pin, bad HMAC, shorter retention, governance
or provider failure must produce Uncertain, NEVER
no-attempt, new DELETE, retry, replay or ready.
Two *buckets* on the SAME CI SeaweedFS host do
NOT mean independently administered or physically
offsite custody; caller obtains both version IDs and
still needs independently durable monotonic pins
outside restorable PostgreSQL. No REST DI wiring.
#144/#141/#109/#31/#32 remain OPEN.

## S3 pin catalog ambiguity forensic audit (#150 / ADR 0049)

`S3ObjectLockPinHistoryAudit` reuses the bounded ListVersions port and
`S3ObjectLockVersionPinService.VerifyPinnedAsync` to report ONLY whether
one externally pinned signed COMPLIANCE pin was visible in a clean provider
listing. Any delete marker, second version, foreign key, malformed/partial
pagination, 403 or readback mismatch MUST be `Uncertain`. Neither a clean
list nor a positive HMAC read is independent proof of monotonic once-ever,
absence of earlier attempts, source DELETE COMMIT or offsite custody.
The audit has NO writer, retry, replay, API DI or recovery release action.
Test with two synthetic same-admin SeaweedFS buckets and a deliberate
second COMPLIANCE object after a delete marker. #144/#141/#109/#31 OPEN.

## Azure Blob one-shot upload + exact version lock (#151 / ADR 0050)

Azure custom blob version-level immutability on upload MAY start
**Unlocked**. The writer MUST validate the scoped evidence HMAC and
UTC future retention before any upload. Use SDK options with
Retry.MaxRetries=0 (the writer owns the SDK client), single conditional
PUT If-None-Match:*; require the returned exact UTC VersionId,
then set Locked policy on **WithVersion(id)** and verify via the
read-only Azure pinned HEAD+GET adapter. A timeout or failure between
PUT and lock is **UNCERTAIN**, never safe to retry or delete. Even a
positive readback does not prove source DELETE COMMIT, global
anti-rollback, independent custody or permission to release traffic.
All real Azure RBAC, staging WORM and key custody are future gates;
scripted SDK HTTP contracts are synthetic. Never connect this
provider to REST DELETE, replay or ready; #144/#141/#109/#31 OPEN.

## Azure WORM visible history audit (#152 / ADR 0051)

Do NOT treat Azure Blob versioned WORM evidence as once-ever or
anti-rollback. A READ-ONLY provider history observation may enumerate
Version|Deleted|DeletedWithVersions|Snapshots for an **exact** evidence key,
with bounded pages and cursor checks. Any deleted/snapshot/duplicate,
second version, untrusted VersionId, missing/truncated page or 403
is `Uncertain`. Only one *visible* matching version may be checked
using exact-version Azure Locked + HMAC readback. Even success
NEVER grants auto retry, DELETE, source commit, replay, recovery-ready,
complete history or independent custody. Scripted SDK tests do not
replace true Azure offsite WORM/RBAC, independent monotonic journal
or GDPR review. #144/#141/#109/#31 OPEN.

## Azure Blob lost-ACK read-only reconciliation (#153 / ADR 0052)

A lost Azure Blob PUT or lock ACK is NEVER proof that the operation did
not commit. A forensic reconciler may scan the **visible** provider
version history for a known UUID using 2-item pages, max 8 pages,
exact key and nonrepeating cursor. Reject deleted, snapshot, noncurrent,
foreign, duplicate, bad/versionless entries and provider 403 or truncated
catalogues. Only ONE visible candidate may be verified using exact
VersionId Azure Locked + actor HMAC. Even a positive observation leaves
MayAutoRetry/MayReleaseRecovery/ProvesSourceDeleteCommit/
ProvesCompleteHistory/ProvesMonotonicHistory **false**. Never use listing
as a trusted independent pin, “nothing written”, permission to retry,
replay, DELETE or release quarantine. No REST/API or worker wiring;
synthetic xUnit tests DO NOT prove production Azure WORM. #144/#141/#109/#31 OPEN.

## Azure signed cross-container immutable pin (#154 / ADR 0053)

`AzureBlobVersionPinService` signs an actor-scoped canonical JSON PIN
(UUID, original exact UTC VersionId, store identity, separate HMAC) using
THREE distinct 32-byte keys (scope/evidence/pin) without tenant or
subject plaintext. Publish ONLY after exact evidence `AzureLocked` and
actor HMAC verification, then perform ONE `If-None-Match: *` pin PUT
using SDK Retry.MaxRetries=0, lock only the ACK VersionId and verify
version-specific HEAD/GET `AzureLocked` and pin HMAC (max 1024 bytes).
`VerifyPinnedAsync` always requires BOTH exact externally supplied
VersionIds. Reject lost ACK, incomplete lock, foreign store/actor,
wrong MAC/mode/version/expiry; NO retries, DELETE, source COMMIT,
monotonic history or release, even when `PinObserved=true`. Store
identity labels and two containers in the same Azure account are NOT
independent offsite custody. Scripted SDK HTTP tests are NOT live
Azure WORM, RBAC, external pin authority or GDPR signoff.
Keep #144/#141/#109/#31 OPEN.

## Azure signed PIN visible-history audit (#155 / ADR 0054)

`AzureBlobWormPinHistoryAudit` scans the **provider-visible** catalogue
for a canonical `erasure-pins/v1/{uuid:N}.json` key using the strict
Azure version-list SDK port (also used for evidence keys). Allow only
`Version|Deleted|DeletedWithVersions|Snapshots` and bounded exact-key
pages, max 2 entries and 8 pages, with nonrepeating continuation.
Competing PIN version, snapshot, deleted/tombstone, duplicate, foreign
key, bad or missing version/cursor, 403, partial page or failure =>
`Uncertain` BEFORE pin readback. A single visible PIN version may
be authenticated with `AzureBlobVersionPinService.VerifyPinnedAsync`
using BOTH externally pinned VersionIds, actor-bound signed PIN HMAC,
AzureLocked policies and expiry; it NEVER proves complete history,
monotonicity, source DELETE COMMIT or a right to retry or release.
No provider write/DELETE, API registration, worker, replay or ready.
Scripted Azure SDK HTTP != real Azure WORM/offsite/IAM. Keep
#144/#141/#109/#31 OPEN.

## JSON API body budget / no implicit decompression (#156 / ADR 0055)

All authenticated POST/PUT/PATCH `/api/v1` requests MUST pass
`ApiJsonBodyBudget` AFTER OIDC authentication, per-principal
rate-limiting and authorization but BEFORE model binding/DB/AI.
The default limit is 65536 bytes (`RequestBody:MaxJsonBytes`, valid
1024..65536); Kestrel's separate 70KiB limit is insufficient for
TestServer or arbitrary ingress. Return 413 for declared oversized
Content-Length AND for chunked/no-length streams by reading at most
budget+1 bytes. Reject Content-Encoding except absent or identity
with 415; do not decompress request bodies or log their contents.
Preserve safe small JSON exactly, keep health and GET untouched and
rate-limit/consent/RLS unchanged. Test with real PostgreSQL
WebApplicationFactory and fake auth header only inside tests.
This is NOT a distributed/connector/media quota, DDoS gateway, slow
body timeout, real OIDC attestation or GDPR release. #37 remains OPEN.

## Total JSON body read deadline / slow-client guard (#157, ADR 0056)

`ApiJsonBodyBudget` MUST enforce BOTH 1..64KiB body budget (ADR 0055)
and a **single absolute total read deadline** for authenticated JSON
POST/PUT/PATCH `/api/v1` requests. Configure
`RequestBody:ReadTimeoutMilliseconds` default 10000, valid
100..30000; invalid startup values must fail closed. Use a linked
cancellation token (`RequestAborted`) with CancelAfter ONCE before
the first body read, and do not reset it per chunk/trickle. An
app-level read expiration (not client disconnect) returns HTTP 408
`request_body_timeout`, `Cache-Control: no-store`, zero binder/DB
side effects and zero body echo. Client abort must remain distinct.
Dispose body timer BEFORE passing completed bounded JSON to the
normal handler. 413/415, authentication→rate-limit→authorization,
real OIDC/JWKS, tenant scope and RLS remain unchanged.
This does NOT implement slow-header defenses, distributed quotas,
connection cap, WAF, binary uploads, total endpoint timeout or
production anti-DDoS. Kestrel request-header and minimum body data
rates plus ingress controls are still necessary; parent #37 OPEN.


## API JSON Content-Type fail-fast boundary (#158 / ADR 0057)

For authenticated POST/PUT/PATCH `/api/v1`, keep media-type checks in
`ApiJsonBodyBudget` AFTER authentication/rate limiting/authorization
and BEFORE reading the body. Accept only ASP.NET Core JSON media types
(`application/json` or `application/*+json`) with no parameters or a
single `charset=utf-8` (case-insensitive, optionally quoted).
Missing, malformed, non-JSON, non-UTF-8, duplicate or unknown media
parameters MUST return 415 `unsupported_media_type` and
`Cache-Control: no-store` without reading bytes or invoking handlers.
Existing content-encoding 415 and declared-size 413 pre-checks precede
media-type validation; total 408 deadline still guards accepted JSON.
Use synthetic fail-on-read stream regression tests. Do not broaden to
media upload, accept unauthenticated data or claim DoS/GDPR readiness.
Keep #37 OPEN.

## Concurrent JSON body reader admission (#159 / ADR 0058)

Authenticated POST/PUT/PATCH /api/v1 request bodies must use ONE shared,
process-local ApiJsonBodyReadConcurrencyLimiter in the API middleware,
AFTER recovery/authentication/rate limiting/authorization and the
existing Content-Encoding, declared size and Content-Type 415/413
prechecks. RequestBody:MaxConcurrentReads defaults to 32 (valid
1..256, fail startup outside range). Admission is a nonblocking,
non-queuing Wait(0) BEFORE allocating the body buffer or ReadAsync.
When saturated: 503 request_body_busy, Retry-After: 1 and no-store;
no body read, model binder, DB/AI work or request content log. Always
release the permit on 408, 413, disconnection, stream exceptions
and successful buffering; release BEFORE handing the buffered body
to the normal endpoint. Scope ONLY concurrent JSON body *reads*
per API instance, not concurrent completed handlers, Kestrel sockets,
slow request headers, connector/media uploads, quotas across replicas,
fairness per tenant or a production DDoS/GDPR release. Parent #37 OPEN.

## Pre-auth Kestrel HTTP transport limits (#160 / ADR 0059)

Kestrel parses request headers BEFORE authentication, rate limiting,
recovery middleware or ApiJsonBodyBudget. Keep an explicit server-level
RequestHeadersTimeout via Transport:RequestHeadersTimeoutMilliseconds
(default 10000; valid 1000..30000), MaxRequestHeadersTotalSize via
Transport:MaxRequestHeadersTotalBytes (default 16384; valid
8192..32768) and MaxRequestHeaderCount (default 64; valid 16..100).
Invalid configuration MUST abort startup, not silently disable limits.
Keep a non-null Kestrel MinRequestBodyDataRate 240 B/s with 5s grace
and the existing 70KiB Kestrel max body size. Contract tests MUST
verify all settings and an ACTUAL local TCP Kestrel incomplete header
times out without running any handler. Normal requests must still work.
Never claim this covers slow TLS handshakes, upstream proxies/WAF,
HTTP/3, per-tenant distributed quotas, binary media or GDPR readiness.
#37 stays OPEN.

## Active-only actor JSON intake fairness (#161 / ADR 0060)

In addition to the globally shared per-instance JSON-read slots
(ADR 0058/#159), no one authenticated OIDC tenant+subject may monopolize
all slots. RequestBody:MaxConcurrentReadsPerActor is optional (default
min(4, MaxConcurrentReads), valid 1..MaxConcurrentReads). Reject invalid
operator values at startup. Acquire global AND actor read capacity
atomically using one short lock and NO queue; only AFTER 413/415 cheap
checks and OIDC/rate-limit/authorization. Use the existing signed-
claims-derived ApiRateLimitPolicy.PartitionKey, never user-provided
X-Forwarded-For/tenant headers. On exhaustion return the SAME generic
503 request_body_busy/no-store/Retry-After:1 without reading any bytes.
Release once on all existing paths (408, 413, disconnect, stream
fault, normal read); release BEFORE endpoint processing. Remove actor
key from the in-memory dictionary immediately when its active read
count becomes zero. Number of retained keys must never exceed
active global read permits; never log/store key values or user bytes.
The authenticated context is validated by OIDC upstream, NOT by this
limiter. Anonymous fallback uses direct network peer only. This is
per ACTOR/per PROCESS fairness, NOT per-tenant, per-cluster, HTTP
connection, media or external service quota. Keep #37/#2 OPEN.

## Tenant-scoped JSON receive budget (#162 / ADR 0061)

The shared instance JSON reader admission control must atomically enforce
three budgets BEFORE body reads: global default 32, verified OIDC actor
default min(4, global), and verified OIDC tenant default min(16, global).
RequestBody:MaxConcurrentReadsPerTenant is optional, valid 1..global;
invalid explicit values fail startup. Derive tenant identity using
ApiRateLimitPolicy.TenantPartitionKey from the same validated JWT claims
as PartitionKey; tenant_id must be unique/nonempty/bounded and is NEVER
taken from request headers, untrusted X-Forwarded-For or body. Invalid/
unauthenticated actors share the existing anonymous network-peer
fallback, although API mutations require authorization upstream.

Global, tenant and actor counts must change together under ONE short
lock and never queue/wait. Saturated requests return existing generic
503 request_body_busy/no-store/Retry-After:1, without request-body
read/handler. Release all counters exactly once after successful receive
or timeout/disconnect/413/stream error, before binding/DB work. Evict
both actor and tenant entries immediately at zero; live partitions
never exceed globally active reads and keys must never be logged.
413/415, OIDC, 429, RLS and consent are unchanged. This is an upper
bound per tenant on ONE process's read-phase concurrency, NOT a fair
reservation under global exhaustion, a cluster quota or GDPR approval.
Parent #37 and epic #2 OPEN.

## Retained JSON request-buffer concurrency (#163 / ADR 0062)

Preserve existing 32/16/4 READ concurrency (process/tenant/actor) and
release read permits BEFORE endpoint execution. Independently cap
concurrently retained JSON MemoryStream buffers across handler work
with one per-process ApiJsonBufferedRequestLimiter, configured by
RequestBody:MaxConcurrentBufferedRequests (default 64, valid 1..256;
fail startup on invalid values). Acquire nonblocking after OIDC, rate
limiting, authorization and cheap 413/415 prechecks, BEFORE buffer
allocation or ReadAsync. On exhaustion reject HTTP 503 request_buffer_busy,
Retry-After:1 and no-store, without reading bytes, logging identity or
executing handler. Release buffered permit only AFTER handler and
MemoryStream disposal, on success, 408, 413 streamed, disconnect, or
any stream/handler exception. Idempotent permit and zero queue/wait.
This is not tenant processing fairness, a DB/AI cap, full memory
ceiling, a cross-replica quota, ingress protection or GDPR signoff.
Parent #37 and epic #2 stay OPEN.

## Active-only tenant fairness for buffered JSON handler lifetimes (#164 / ADR 0063)

The middleware-owned buffered JSON request limiter (#163) must atomically
enforce BOTH a per-instance global cap (default 64) and per-validated-
OIDC-tenant cap (default min(16,global)). Operator override:
RequestBody:MaxConcurrentBufferedRequestsPerTenant optional, 1..global;
bad explicit values reject startup. Leave appsettings default unset
so a smaller global budget clamps the tenant default automatically.
Use ONLY ApiRateLimitPolicy.TenantPartitionKey(context) after OIDC,
rate limiting and authorization; never X-Tenant-Id, forwarded headers
or JSON body. Acquire before buffer allocation/body read, no queue:
saturation 503 request_buffer_busy/no-store/Retry-After:1 without
reading or reaching the handler. One lock protects global+tenant
admission/release, count entries only active tenants, drop at zero;
key count <= global active slots, do not log/store keys. Hold buffer
permit THROUGH handler execution; still release READ permits
32/16/4 BEFORE handler. Release all leases on 408/413, abort,
stream/handler faults and success, idempotently. NOT a guaranteed
fair-share reserve when global pool full, per-actor processing,
cross-replica distributed quota, WAF/media/connector protection or
GDPR release. Keep parent #37 and epic #2 OPEN.

## Active-only actor fairness for retained JSON buffers (#165 / ADR 0064)

In addition to per-instance global buffered-request slots (default 64)
and verified OIDC tenant slots (default min(16,global)), apply a third
per VERIFIED tenant+subject actor slot cap (default min(4,tenant)).
RequestBody:MaxConcurrentBufferedRequestsPerActor is OPTIONAL and valid
1..effective tenant cap; invalid explicit values fail startup.
Leave the actor setting absent from appsettings defaults so reduced
tenant/global limits auto-clamp safely. Acquire global, tenant and
actor slots ATOMICALLY under one lock and without a queue, after
OIDC/rate limit/authorization and cheap 413/415 checks, before
any buffer allocation or ReadAsync. Derive actor and tenant ONLY from
ApiRateLimitPolicy.PartitionKey and TenantPartitionKey (JWT OIDC,
anonymous fallback direct peer), never user-supplied headers.
Return SAME generic 503 request_buffer_busy, Retry-After:1,
no-store when ANY cap is full; no read, handler, payload or identity
echo. Hold this buffered permit throughout downstream handler
and MemoryStream lifetime, releasing on success, 408, 413, abort,
stream or handler failure. Release all 3 counters exactly once
and evict actor and tenant keys immediately at zero, bounded by
global active slots. No key logging/storage. Keep separate JSON
RECEIVE permits 32/16/4 released BEFORE handlers. This is an
upper bound on one API process, NOT a fair reservation when
global capacity is gone, cluster quota, all DB/AI work limiter,
WAF/ingress protection or GDPR signoff. #37 and #2 OPEN.

## Per-tenant request/minute budget (#166 / ADR 0065)
Preserve the ASP.NET Core per-actor RateLimit:RequestsPerMinute
policy (default 120) and apply an independent 60-second fixed
window for authenticated /api/v1 requests across ALL signed
OIDC actors in one tenant. Default
RateLimit:TenantRequestsPerMinute=600, valid 1..10000;
RateLimit:MaxTrackedTenants=4096, valid 1..16384. Explicit
invalid values fail startup. Execute AFTER authentication,
existing UseRateLimiter and authorization and BEFORE JSON
body buffering/handlers; do not limit health, readiness,
anonymous challenges or CORS preflight with this tenant layer.
Partition ONLY with ApiRateLimitPolicy.TenantPartitionKey
from validated OIDC claims, never forwarded/client headers.
On exhaustion return generic 429 rate_limited, no-store and
Retry-After 1..60, no queue, body read or handler work.
One lock guards per-process windows; at most MaxTrackedTenants
keys. Expiry cleanup is lazy on request traffic at most once
per 15 seconds, not at every rejection or in background.
At full partition cardinality, fail CLOSED for unknown tenants
without evicting active tenants. Bounded in-memory key strings
can remain until later traffic/restart; NEVER log or persist.
Fixed-window edge bursts and denial to new tenants under
cardinality pressure are explicit residual risks; not
distributed, WAF, media/connector, GDPR or production-proof.
Keep #37 and #2 OPEN.

## Bounded per-account minute rate limiter (#167 / ADR 0066)

The older ASP.NET Core per-actor RateLimiter partition factory
(no configured actor-cardinality ceiling) is REMOVED. Replace
it with ApiActorRequestWindowLimiter, a process-local per-signed
tenant+subject fixed 60-second window, with default actor rate
RateLimit:RequestsPerMinute=120 (valid 1..1000) and
RateLimit:MaxTrackedActors=8192 (valid 1..32768); invalid
explicit settings fail startup. The per-tenant quota from #166
is preserved: default 600/min and at most 4096 tenant keys.
Both use ONE reusable ApiFixedWindowRequestLimiter engine
with different max key sizes (actor <=512, tenant <=256),
an atomic short lock and lazy expiration on request traffic
every 15 seconds. Do not use AddRateLimiter/RequireRateLimiting
for per-actor partitions without a bounded eviction policy.

Pipeline MUST be UseAuthentication → UseAuthorization →
ApiActorRequestRateBudget → ApiTenantRequestRateBudget →
ApiJsonBodyBudget → endpoint for authenticated /api/v1 routes.
Health, readiness, CORS preflight and 401/403 challenges remain
outside these request windows. Only signed OIDC tenant+sub
partition via ApiRateLimitPolicy.PartitionKey and
TenantPartitionKey, NEVER X-Forwarded-For/X-Tenant-Id/JSON.
Fail-fast 429 rate_limited, no-store, Retry-After 1..60 before
request body reads/handler/DB. At active partition cardinality
capacity, deny UNKNOWN actors/tenants; keep known actors able
to use their unused request allowance, no eviction of active
keys. Do not scan every active key on every 429; never log,
persist or leak actor/tenant keys. Expired identities may
remain in strictly bounded RAM until more request traffic.
Fixed-window boundary bursts and possible temporary 429
for legitimate new accounts are explicit limitations.
The per-process cap does not solve cross-replica/WAF/media/
connector/AI quotas, production load or GDPR. Parent #37/#2
remain OPEN.


## Immutable live memory capture retries (#175 / ADR 0074)

For every authenticated actor, source and externalId, an unexpired
\`memory_entries\` row is WRITE-ONCE. Keep \`PostgresActorLock\`, active
scoped consent, restricted runtime FORCE RLS and \`FOR UPDATE\` in the
same transaction. Compare original sealed content using AEAD tenant,
owner, canonical UUID plus stored occurredAt at PostgreSQL microsecond
precision. Same source event must return the existing UUID without
another Encrypt, SQL UPDATE, changed DEK, expiry extension or
provenance mutation. Reusing a live key for a DIFFERENT event MUST
return generic HTTP 409 \`memory_idempotency_conflict\`, without
personal data, key, actor or tenant echo; Web should show conflict,
not logout or silent success. Only a truly EXPIRED row may be
explicitly reacquired, with fresh consent, subject to existing TTL
policy. Different actor/tenant scopes with the same externalId remain
isolated. A source event without stable occurredAt cannot be replayed
as exact content after the server assigns a new time; use canonical
stable source keys/time on the client. This does not imply offline
queue, immutable multi-source versions or zero embedding-provider
work. Keep #125/#41/#8/#2 OPEN.


## Stale consent grant ID must withdraw current source/purpose (#176 / ADR 0075)

PostgresConsentRepository.GrantAsync supersedes G1 with G2 for one
tenant+subject+source+purpose, leaving G1 in the audit ledger.
A second device may still issue DELETE /api/v1/consents/G1.
RevokeAsync MUST take the shared actor advisory lock in the same
PostgresActorScope restricted FORCE RLS transaction, resolve
source/purpose ONLY from the selected ID belonging to the JWT actor,
and revoke ALL currently unrevoked grants of that same exact scope,
including G2. Never rely on user-controlled source, purpose, tenant
or owner input. Preserve timestamps of already revoked history,
scope isolation, and response semantics 204 own/idempotent,
404 unknown/foreign. Consent withdrawal MUST take effect at the
API capture gate, not merely clear browser UI. Regrant accepted AFTER
withdrawal is a distinct explicit decision; no permanent source ban.
Past notes, offline devices, provider grants, IdP sessions, external
copies and GDPR legal review are still OPEN (#29/#41/#31/#2).

## Web manual capture source keys (#177 / ADR 0076)

`normalizeMemo(..., requireSecureId=true)` is mandatory for authenticated
Web cloud capture. Each NEW click must obtain a fresh
`globalThis.crypto.randomUUID()`; never use the clock, memo text or
`Math.random()` as the sole cloud source key. If secure randomness is
absent/throws, fail closed BEFORE API fetch. A retry after uncertain HTTP
response must retain the exact previously normalized object, key, content
and occurrence time via `retryRef`. Only local non-synchronizing
native/demo mode may use a session-serial `demo-` fallback. This ID is
not an auth token, tenant selector, cross-source dedupe or offline sync.
#41/#125/#8/#2 still OPEN despite #177's narrow resolution.

## Bounded consent ledger must expose active manual scope (#178 / ADR 0077)

`PostgresConsentRepository.ListAsync` limits the actor-scoped result
to 200 rows. NEVER use simple newest-first history ordering, which can
hide the current `manual/memory.capture` grant behind older-scope
renewal/revocation history, even while the backend correctly permits
ingestion. Prioritize still-active manual first, then other active
grants, then newest history, with deterministic UUID tie-break,
within the same restricted `PostgresActorScope` and FORCE RLS.
Authorization/revocation MUST continue to rely on the database
actor identity and transactional active grant checks, not a Web DTO.
Test with >200 newer expired/revoked synthetic records and negative
cross-owner/tenant reads. No full paginated historical ledger or
legal consent coverage; #29/#41/#2 stay OPEN.


## Consent ledger chronological keyset API (#180 / ADR 0078)

Keep the legacy `GET /api/v1/consents` bounded array with #178's
manual-active priority. The additive `GET /api/v1/consents/page`
MUST validate default 25/max 50 and strict `c1.` + canonical
70-character base64url ticks+UUID token. Return generic 400 on
invalid limits/tokens, never echo cursor or grant metadata to logs.
Always read via signed JWT tenant+subject and restricted PostgreSQL
`PostgresActorScope` FORCE RLS; use only parameterized queries with
`(granted_at,id) < (beforeAt,beforeId)` and DESC UUID tie-break,
`LIMIT limit+1`, to avoid missing identical timestamps.
A cursor is a position, not authentication/snapshot/consent.
`IConsentGuard`, regrant/withdrawal locks and memory write gate
must not be affected. Web first-party history navigation and source withdrawal are implemented
in #181 / ADR 0079, with real Keycloak E2E as acceptance gate.
Parent #29/#41/#2 remain OPEN for other consent, native/offline,
third-party and GDPR requirements.


## Explicit signed Web consent ledger lifecycle (#181 / ADR 0079)

The Web consent history browser is opt-in, fetches only bounded
signed `consentPage()` data, shows exact continuation status and
source/purpose/active/expired/revoked statuses, and revokes only
an active actor-owned grant UUID via existing DELETE. Do not
pretend a source label authorizes external connectors or third
party processing. Keep all records and `nextCursor` in transient
state; `ConsentLedgerEpoch` invalidates old results/errors on
logout/401, erase, grant/revoke and refreshCloud. A 204 is
required before telling the user a source was withdrawn;
backend authority/locks/FORCE RLS remain unchanged. Browser
real Keycloak E2E should use synthetic source grants, enforce
429 budgets and test pagination past 25, foreign-actor isolation
at the API, 403 on revoked source ingestion and private
session wipe. No native offline or GDPR legal approval claimed.


## Authenticated loaded-timeline source filter (#182 / ADR 0080)

The Web source selector derives choices ONLY from signed,
RLS-scoped \`memos\` already loaded via memory keyset pages.
Use canonical exact source equality, render visible/loaded
counts and explicit "loaded rows only" notice. An empty
filtered view is not evidence of no server rows; preserve
the \`loadOlder\` action and cursor regardless of filter.
Do not turn the client-only filter into OAuth provider search
or guess ungranted data. Never persist selection to URL or
browser storage, and clear it alongside private view state
on signout/401/erasure. Keep existing \`CloudViewEpoch\`
session fences, note idempotency and API/RLS consent gates.
React Native Pressable uses accessible button roles and
selected state. #79/#41 and GDPR parent #2 remain OPEN.


## Local-calendar day group for loaded timeline (#183 / ADR 0081)

Preserve the signed API `occurredAt` instant in the Web
display model, and derive date headings using the DEVICE's
calendar timezone via `Intl.DateTimeFormat.formatToParts`.
Never group by UTC date slicing or by ingestion/retention
timestamps. Perform exact source filtering before grouping.
Group only already-loaded rows and merge a day when a keyset
page continues it; never auto-fetch unbounded history or
persist derived headings. Keep original note order/UUID,
show canonical source and local clock, and use accessible
header semantics. All headings are derivatives of private
`memos`: existing epoch+JWT fences clear them on logout,
401 or erase. Tests must cover DST, same-day page boundary,
source filter, real signed Keycloak→PostgreSQL and no stale
row on sign-out. #79/#41/#2/#17 remain OPEN.


## Signed loaded-note tag/source intersection (#184 / ADR 0082)

The Web tag filter is a purely local, view-only option derived
from the `tags` fields of ALL already-loaded RLS/JWT note
rows. Choices stay independent of active source/tag selection,
so older pages can add new tags. Use exact array membership
(`source AND tag`), then group remaining rows by the local
day. Keep 10-item keyset pagination opt-in and independent,
the visible/loaded count honest, and preserve the empty state
without residual headings. Tag labels are deterministic
keyword heuristics, NEVER AI insights or provider authorization.
No localStorage/AsyncStorage/URL persistence, no extra API
fetch, no mutation to tags, provenance or consent. Always
wipe source, tag and notes together on logout/401/erase.
The broader #79/#41/#2/#17 remain OPEN.
## Signed loaded-day selection (#185 / ADR 0083)

Expose device-local day options derived from **ALL already-loaded signed actor notes** through the same `groupTimelineByLocalDay` implementation as ADR 0081. No UTC slicing, guessed global day availability, extra fetching, source timezone claims, or derived thread assertions. Exact day selection ANDs with source and keyword tag before rendering grouped headings; keep manual keyset paging independent even when zero records are visible. Use accessible selected-state controls and honest visible/loaded counts. All private selection must be wiped with signed session notes on logout, 401, or erasure; never persist in URL or browser storage. Verify RED→GREEN Vitest DST and genuine Keycloak/JWT/PostgreSQL paging E2E; keep broader #79/#7/#41/#2 OPEN.
