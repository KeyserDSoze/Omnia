#!/usr/bin/env bash
# #109/#140: synthetic physical pg_basebackup + WAL named-point recovery.
# GitHub CI disposable PostgreSQL 17/pgvector containers ONLY.
# This must NEVER operate against a production database or publish archives.
set -euo pipefail
: "${RUNNER_TEMP:?GitHub CI private temp directory required}"
: "${GITHUB_ACTIONS:?GitHub CI only}"
: "${OMNIA_TEST_APP_DB:?test runtime connection required}"
: "${OMNIA_TEST_KEYRING_FILE:?test KEK file required}"
[[ "$GITHUB_ACTIONS" == "true" ]]
[[ "$OMNIA_TEST_APP_DB" == "Host=127.0.0.1;Port=15432;Database=omnia_test;Username=omnia_app;Password="* ]]
[[ -f "$OMNIA_TEST_KEYRING_FILE" ]]
source_container="omnia-ci-pg"
recovery_container="omnia-ci-pitr-recovery"
base_path="/tmp/omnia-ci-pitr-base"
archive_path="/tmp/omnia-ci-wal-archive"
restore_target="omnia_ci_before_destructive_change"
volume="omnia-ci-pitr-${GITHUB_RUN_ID:-dev}-${GITHUB_RUN_ATTEMPT:-1}"
private_dir="$(mktemp -d "$RUNNER_TEMP/omnia-ci-pitr.XXXXXX")"
chmod 700 "$private_dir"
mkdir -m 700 "$private_dir/base" "$private_dir/archive"
volume_created=0

cleanup() {
  docker rm -f "$recovery_container" >/dev/null 2>&1 || true
  if [[ "$volume_created" == 1 ]]; then
    docker volume rm "$volume" >/dev/null 2>&1 || true
  fi
  docker exec "$source_container" rm -rf "$base_path" "$archive_path" >/dev/null 2>&1 || true
  rm -rf -- "$private_dir"
}
trap cleanup EXIT

docker inspect "$source_container" >/dev/null
archive_mode="$(docker exec "$source_container" psql -U omnia -d omnia_test -Atqc "SHOW archive_mode")"
[[ "$archive_mode" == "on" ]] || { echo "CI WAL archiving was not enabled." >&2; exit 1; }

# The previous logical recovery step left one synthetic encrypted memory in
# the disposable primary. Never seed or inspect any non-test user content.
OMNIA_REHEARSAL_DB="$OMNIA_TEST_APP_DB" \
OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- verify

# A second, independently scoped synthetic actor must survive replay intact.
OMNIA_REHEARSAL_DB="$OMNIA_TEST_APP_DB" \
OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- seed-peer

# RED: a second synthetic actor must already exist BEFORE backup, so
# losing its later committed erasure (without witness ACK) resurrects data.
OMNIA_REHEARSAL_DB="$OMNIA_TEST_APP_DB" \
OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- seed-unwitnessed

# True physical basebackup: includes the database cluster, global roles,
# extensions, RLS, and WAL up to the backup end. pg_verifybackup verifies
# manifest/checksums before any restoration attempt.
docker exec -u postgres "$source_container" pg_basebackup \
  -h /var/run/postgresql -U omnia -D "$base_path" \
  --format=plain --wal-method=stream --checkpoint=fast --no-password
docker exec -u postgres "$source_container" pg_verifybackup "$base_path"

# These WAL changes occur strictly AFTER the completed physical backup.
# The named point precedes a destructive mutation to the synthetic memory.
docker exec "$source_container" psql -X -v ON_ERROR_STOP=1 -U omnia -d omnia_test \
  -c "CREATE TABLE public.omnia_ci_pitr_marker (marker text PRIMARY KEY)" >/dev/null
docker exec "$source_container" psql -X -v ON_ERROR_STOP=1 -U omnia -d omnia_test \
  -c "INSERT INTO public.omnia_ci_pitr_marker (marker) VALUES ('before-target')" >/dev/null
docker exec "$source_container" psql -X -v ON_ERROR_STOP=1 -U omnia -d omnia_test \
  -c "SELECT pg_create_restore_point('$restore_target')" >/dev/null
# RED: this must perform a real actor-scoped service erasure, including
# transactional outbox, after the named restore point (not raw SQL DELETE).
OMNIA_REHEARSAL_DB="$OMNIA_TEST_APP_DB" \
OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- erase-source
docker exec "$source_container" psql -X -v ON_ERROR_STOP=1 -U omnia -d omnia_test \
  -c "INSERT INTO public.omnia_ci_pitr_marker (marker) VALUES ('after-target')" >/dev/null

source_remaining="$(docker exec "$source_container" psql -U omnia -d omnia_test -Atqc \
  "SELECT count(*) FROM public.memory_entries WHERE tenant_id = 'omnia-restore-drill-synthetic' AND owner_sub = 'synthetic-owner-only'")"
[[ "$source_remaining" == "0" ]] || { echo "Source destructive test action did not commit." >&2; exit 1; }

# Both deletes and the erasure outbox row were committed on PRIMARY
# strictly AFTER the restore point. The restored primary must lose it.
source_receipts="$(docker exec "$source_container" psql -U omnia -d omnia_test -Atqc \
  "SELECT count(*) FROM public.erasure_outbox WHERE tenant_id = 'omnia-restore-drill-synthetic' AND owner_sub = 'synthetic-owner-only'")"
[[ "$source_receipts" == "1" ]] || {
  echo "Synthetic source did not commit exactly one erasure event." >&2; exit 1;
}


# Outside-PGDATA synthetic deletion receipt. It is created only AFTER the
# source DELETE commits, so restoring the earlier physical backup omits it.
# The key and receipt never enter the PostgreSQL backup or CI artifacts.
receipt_dir="$private_dir/reconciliation"
mkdir -m 700 "$receipt_dir"
receipt_file="$receipt_dir/receipt.json"
receipt_key="$receipt_dir/hmac.key"
receipt_mac="$receipt_dir/receipt.mac"
jq -cn --arg id "$(openssl rand -hex 16)" \
  '{schemaVersion:1,operation:"erase-actor",restorePoint:"omnia_ci_before_destructive_change",receiptId:$id,tenantId:"omnia-restore-drill-synthetic",ownerSubject:"synthetic-owner-only"}' > "$receipt_file"
openssl rand -out "$receipt_key" 32
chmod 600 "$receipt_file" "$receipt_key"
OMNIA_REHEARSAL_DB="$OMNIA_TEST_APP_DB" \
OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
OMNIA_REHEARSAL_RECEIPT_PATH="$receipt_file" \
OMNIA_REHEARSAL_RECEIPT_KEY_PATH="$receipt_key" \
OMNIA_REHEARSAL_RECEIPT_MAC_PATH="$receipt_mac" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- sign-receipt
chmod 600 "$receipt_mac"

# Separate (synthetic!) signed recovery journal + checkpoint, outside
# PGDATA/WAL. This is NOT a trusted durable producer or monotonic store;
# both artifacts still live on the same disposable CI runner.
audit_dir="$private_dir/offline-journal"
mkdir -m 700 "$audit_dir"
# Private keys are generated only on the ephemeral runner; no
# journal key or checkpoint key lives inside the restored PGDATA.
openssl rand -out "$audit_dir/journal.key" 32
openssl rand -out "$audit_dir/checkpoint.key" 32
chmod 600 "$audit_dir/journal.key" "$audit_dir/checkpoint.key"
export OMNIA_RECOVERY_JOURNAL_PATH="$audit_dir/journal.jsonl"
export OMNIA_RECOVERY_CHECKPOINT_PATH="$audit_dir/checkpoint.json"
export OMNIA_RECOVERY_JOURNAL_KEY_PATH="$audit_dir/journal.key"
export OMNIA_RECOVERY_CHECKPOINT_KEY_PATH="$audit_dir/checkpoint.key"
# The actual .NET local append writer creates journal.jsonl and
# checkpoint.json, verified against independent HMAC keys. CI-only phase
# requires the known synthetic source DB and actor; no HTTP publisher.
OMNIA_REHEARSAL_DB="$OMNIA_TEST_APP_DB" \
OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
  dotnet run --no-build -c Release \
    --project tests/Omnia.RestoreRehearsal -- append-journal
audit_journal() {
  dotnet run --no-build -c Release \
    --project src/backend/Omnia.RecoveryAudit/Omnia.RecoveryAudit.csproj
}

# This check verifies the signed count & chain but cannot prove a
# complete real-world deletion event stream. It is not a release token.
audit_journal
cp "$OMNIA_RECOVERY_JOURNAL_PATH" "$audit_dir/journal.original"
chmod 600 "$audit_dir/journal.original"
: > "$OMNIA_RECOVERY_JOURNAL_PATH"
if audit_journal >/dev/null 2>&1; then
  echo "Recovery audit accepted a truncated external journal." >&2
  exit 1
fi
mv "$audit_dir/journal.original" "$OMNIA_RECOVERY_JOURNAL_PATH"
audit_journal

# RED: deliver the EXACT committed primary outbox event to independent
# witness PGDATA before any PITR. This must not rely on receipt fixture
# signer or a raw admin insertion into the witness.
OMNIA_REHEARSAL_DB="$OMNIA_TEST_APP_DB" \
OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
OMNIA_REHEARSAL_DISPATCH_DB="$OMNIA_TEST_DISPATCH_DB" \
OMNIA_REHEARSAL_WITNESS_WRITER_DB="$OMNIA_TEST_WITNESS_WRITER_DB" \
OMNIA_REHEARSAL_WITNESS_READER_DB="$OMNIA_TEST_WITNESS_READER_DB" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- publish-source

# Ensure the segment containing the named restore point and the later
# destructive action is durable in the WAL archive before copying.
last_segment="$(docker exec "$source_container" psql -U omnia -d omnia_test -Atqc \
  'SELECT pg_walfile_name(pg_current_wal_lsn())')"
[[ "$last_segment" =~ ^[0-9A-F]{24}$ ]] || { echo "Unexpected WAL segment name." >&2; exit 1; }
docker exec "$source_container" psql -U omnia -d omnia_test -Atqc \
  'SELECT pg_switch_wal()' >/dev/null
found_archive=0
for i in $(seq 1 60); do
  if docker exec "$source_container" test -s "$archive_path/$last_segment" >/dev/null 2>&1; then
    found_archive=1
    break
  fi
  sleep 1
done
[[ "$found_archive" == 1 ]] || { echo "The required WAL segment was not archived." >&2; exit 1; }

# Copy private physical files only into an ephemeral CI directory; no
# stdout archive, GitHub artifact, remotely mounted store, or persistent key.
docker cp "$source_container:$base_path/." "$private_dir/base/"
docker cp "$source_container:$archive_path/." "$private_dir/archive/"
[[ -s "$private_dir/base/backup_manifest" ]]
[[ -s "$private_dir/base/global/pg_control" ]]
[[ -s "$private_dir/archive/$last_segment" ]]

cat > "$private_dir/pitr-config" <<'CONF'
restore_command = 'cp /var/lib/postgresql/data/omnia_ci_wal_archive/%f %p'
recovery_target_name = 'omnia_ci_before_destructive_change'
recovery_target_action = 'promote'
recovery_target_timeline = 'current'
CONF
chmod 600 "$private_dir/pitr-config"

# New isolated physical runtime from the basebackup. The physical database
# system ID must MATCH the source (unlike a logical restore) by design.
docker volume create "$volume" >/dev/null
volume_created=1
docker run --rm --entrypoint bash \
  -v "$volume:/var/lib/postgresql/data" \
  -v "$private_dir:/private:ro" \
  pgvector/pgvector:pg17 -ceu '
  cp -a /private/base/. /var/lib/postgresql/data/
  mkdir -m 700 /var/lib/postgresql/data/omnia_ci_wal_archive
  cp -a /private/archive/. /var/lib/postgresql/data/omnia_ci_wal_archive/
  cat /private/pitr-config >> /var/lib/postgresql/data/postgresql.auto.conf
  touch /var/lib/postgresql/data/recovery.signal
  chmod 700 /var/lib/postgresql/data
  chown -R postgres:postgres /var/lib/postgresql/data
' >/dev/null

# Because this is a physical copy, it includes source credentials. Rotate
# the recovered runtime password before permitting the application check.
docker run -d --rm --name "$recovery_container" \
  -v "$volume:/var/lib/postgresql/data" \
  -p 127.0.0.1:15435:5432 \
  pgvector/pgvector:pg17 >/dev/null
ready=0
for i in $(seq 1 90); do
  if docker exec "$recovery_container" pg_isready -U omnia -d omnia_test >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done
[[ "$ready" == 1 ]] || { echo "PITR recovery did not reach the named restore point." >&2; exit 1; }

# Before/after WAL marker is the exact stop-point assertion, not just
# "PostgreSQL boots". Both marker table and 'before-target' record are
# POST-backup WAL writes. The subsequent destructive action must be absent.
marker_before="$(docker exec "$recovery_container" psql -U omnia -d omnia_test -Atqc \
  "SELECT count(*) FROM public.omnia_ci_pitr_marker WHERE marker = 'before-target'")"
marker_after="$(docker exec "$recovery_container" psql -U omnia -d omnia_test -Atqc \
  "SELECT count(*) FROM public.omnia_ci_pitr_marker WHERE marker = 'after-target'")"
[[ "$marker_before" == 1 && "$marker_after" == 0 ]] || {
  echo "PITR did not stop at the exact named WAL record." >&2; exit 1;
}
in_recovery="$(docker exec "$recovery_container" psql -U omnia -d omnia_test -Atqc \
  'SELECT pg_is_in_recovery()')"
[[ "$in_recovery" == f ]] || { echo "Recovered primary not promoted." >&2; exit 1; }
source_system="$(docker exec "$source_container" psql -U omnia -d omnia_test -Atqc \
  'SELECT system_identifier FROM pg_control_system()')"
target_system="$(docker exec "$recovery_container" psql -U omnia -d omnia_test -Atqc \
  'SELECT system_identifier FROM pg_control_system()')"
[[ -n "$source_system" && "$source_system" == "$target_system" ]] || {
  echo "Physical basebackup system identifier mismatch." >&2; exit 1;
}

# The target must be BEFORE the service erasure and BEFORE its outbox
# insert, while the SECOND independent witness still knows the event.
recovered_source_receipts="$(docker exec "$recovery_container" psql -U omnia -d omnia_test -Atqc \
  "SELECT count(*) FROM public.erasure_outbox WHERE tenant_id = 'omnia-restore-drill-synthetic' AND owner_sub = 'synthetic-owner-only'")"
[[ "$recovered_source_receipts" == "0" ]] || {
  echo "PITR did not roll back the committed erasure outbox." >&2; exit 1;
}


new_app_password="$(openssl rand -hex 24)"
echo "::add-mask::$new_app_password"
docker exec -i "$recovery_container" psql -X -v ON_ERROR_STOP=1 \
  -U omnia -d omnia_test -v app_password="$new_app_password" >/dev/null <<'SQL'
SELECT format('ALTER ROLE omnia_app PASSWORD %L', :'app_password') \gexec
SQL

original_password="${OMNIA_TEST_APP_DB##*Password=}"
if OMNIA_REHEARSAL_DB="Host=127.0.0.1;Port=15435;Database=omnia_test;Username=omnia_app;Password=$original_password" \
   OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
   dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- verify >/dev/null 2>&1; then
  echo "Physical recovery retained the source runtime login password." >&2
  exit 1
fi
unset original_password

target_cs="Host=127.0.0.1;Port=15435;Database=omnia_test;Username=omnia_app;Password=$new_app_password"
OMNIA_REHEARSAL_DB="$target_cs" \
OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- verify

# A PITR backup does not contain KEKs; losing the historical KEK must
# produce a cryptographic failure, not an alternative plaintext response.
lost_ring="$private_dir/only-new-key.json"
fresh_key="$(openssl rand -base64 32)"
jq -nc --arg kek "$fresh_key" \
  '{activeKeyId:"pitr-v2",keys:{"pitr-v2":$kek}}' > "$lost_ring"
chmod 600 "$lost_ring"
unset fresh_key
OMNIA_REHEARSAL_DB="$target_cs" \
OMNIA_REHEARSAL_KEYRING_PATH="$lost_ring" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- missing-old-key
# RLS-scoped replay MUST be fail-closed for missing/tampered receipt,
# then succeed with the out-of-backup authenticated deletion receipt.
# Deliberately do not expose this recovered DB via the public API.
reconcile() {
  OMNIA_REHEARSAL_DB="$target_cs" \
  OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
  OMNIA_REHEARSAL_RECEIPT_PATH="$receipt_file" \
  OMNIA_REHEARSAL_RECEIPT_KEY_PATH="$receipt_key" \
  OMNIA_REHEARSAL_RECEIPT_MAC_PATH="$receipt_mac" \
    dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- reconcile
}
mv "$receipt_mac" "$receipt_mac.held"
if reconcile >/dev/null 2>&1; then
  echo "Restore reconciliation accepted missing authentication." >&2; exit 1
fi
mv "$receipt_mac.held" "$receipt_mac"
cp "$receipt_mac" "$receipt_mac.authentic"
printf 'tampered' > "$receipt_mac"
if reconcile >/dev/null 2>&1; then
  echo "Restore reconciliation accepted a tampered receipt MAC." >&2; exit 1
fi
mv "$receipt_mac.authentic" "$receipt_mac"

# Negative: a witness writer login MUST NOT authorize reconciliation.
# The restored source still contains the resurrected actor after denial.
if OMNIA_REHEARSAL_DB="$target_cs" \
   OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
   OMNIA_REHEARSAL_WITNESS_READER_DB="$OMNIA_TEST_WITNESS_WRITER_DB" \
   dotnet run --no-build -c Release \
      --project tests/Omnia.RestoreRehearsal -- witness-reconcile >/dev/null 2>&1; then
  echo "PITR reconciliation accepted a witness writer as reader." >&2
  exit 1
fi

# Check the unauthorized attempt did not delete data or affect another actor.
OMNIA_REHEARSAL_DB="$target_cs" \
OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- verify

# The positive reconciliation is now sourced from the SECOND PGDATA
# and the actual transactionally committed erase event, NOT from a
# runner-created fixture receipt. Source outbox row is absent on PITR.
witness_reconcile() {
  OMNIA_REHEARSAL_DB="$target_cs" \
  OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
  OMNIA_REHEARSAL_WITNESS_READER_DB="$OMNIA_TEST_WITNESS_READER_DB" \
    dotnet run --no-build -c Release \
      --project tests/Omnia.RestoreRehearsal -- witness-reconcile
}
witness_reconcile
witness_reconcile
# The older HMAC receipt also remains valid for idempotent replay.
reconcile
# Idempotent replay: an already-applied receipt is harmless to replay.
reconcile
OMNIA_REHEARSAL_DB="$target_cs" \
OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- verify-erased
OMNIA_REHEARSAL_DB="$target_cs" \
OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- verify-peer

echo "Physical PITR plus authenticated out-of-backup erasure replay verified."
