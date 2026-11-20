#!/usr/bin/env bash
# #141/#109: PHYSICAL pg_basebackup + named WAL PITR of witness PostgreSQL.
# The THIRD cluster's HMAC checkpoint survives; a restored witness that
# lost a later committed erasure MUST be refused. Disposable CI only.
set -euo pipefail
: "$RUNNER_TEMP"
: "$GITHUB_ACTIONS"
: "$CI"
: "$OMNIA_TEST_WITNESS_ADMIN_DB"
: "$OMNIA_TEST_WITNESS_READER_DB"
: "$OMNIA_TEST_WITNESS_WRITER_DB"
: "$OMNIA_TEST_ANCHOR_ADMIN_DB"
: "$OMNIA_TEST_ANCHOR_WRITER_DB"
: "$OMNIA_TEST_ANCHOR_READER_DB"
[[ "$GITHUB_ACTIONS" == "true" && "$CI" == "true" ]]
[[ "$OMNIA_TEST_WITNESS_ADMIN_DB" == "Host=127.0.0.1;Port=15436;Database=omnia_witness;Username=omnia;Password="* ]]
[[ "$OMNIA_TEST_ANCHOR_ADMIN_DB" == "Host=127.0.0.1;Port=15438;Database=omnia_anchor;Username=omnia;Password="* ]]

source_container="omnia-ci-erasure-witness"
anchor_container="omnia-ci-witness-anchor"
recovery_container="omnia-ci-witness-pitr-recovered"
base_path="/tmp/omnia-ci-witness-pitr-base"
archive_path="/tmp/omnia-ci-witness-wal-archive"
restore_point="omnia_ci_before_witness_pitr_erasure"
volume="omnia-ci-witness-pitr-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT"
private_dir="$(mktemp -d "$RUNNER_TEMP/omnia-witness-pitr.XXXXXX")"
chmod 700 "$private_dir"
mkdir -m 700 "$private_dir/base" "$private_dir/archive"
volume_created=0

cleanup() {
  docker rm -f "$recovery_container" >/dev/null 2>&1 || true
  if [[ "$volume_created" == 1 ]]; then
    docker volume rm "$volume" >/dev/null 2>&1 || true
  fi
  docker exec "$source_container" rm -rf "$base_path" >/dev/null 2>&1 || true
  rm -rf -- "$private_dir"
}
trap cleanup EXIT

docker inspect "$source_container" >/dev/null
docker inspect "$anchor_container" >/dev/null
archive_mode="$(docker exec "$source_container" psql -U omnia -d omnia_witness -Atqc 'SHOW archive_mode')"
[[ "$archive_mode" == "on" ]] || {
  echo "Witness WAL archiving is not enabled in synthetic CI." >&2
  exit 1
}

# Use a NEW database in the THIRD PGDATA, avoiding any other xUnit anchor
# checkpoint and keeping custody independent from the witness PGDATA.
docker exec "$anchor_container" psql -X -v ON_ERROR_STOP=1 -U omnia -d postgres \
  -c "CREATE DATABASE omnia_witness_pitr_anchor" >/dev/null
docker exec -i "$anchor_container" psql -X -v ON_ERROR_STOP=1 \
  -U omnia -d omnia_witness_pitr_anchor \
  < tests/restore/independent-anchor.sql >/dev/null

export OMNIA_WITNESS_PITR_ANCHOR_WRITER_DB
export OMNIA_WITNESS_PITR_ANCHOR_READER_DB
OMNIA_WITNESS_PITR_ANCHOR_WRITER_DB="$(sed 's/Database=omnia_anchor;/Database=omnia_witness_pitr_anchor;/' <<< "$OMNIA_TEST_ANCHOR_WRITER_DB")"
OMNIA_WITNESS_PITR_ANCHOR_READER_DB="$(sed 's/Database=omnia_anchor;/Database=omnia_witness_pitr_anchor;/' <<< "$OMNIA_TEST_ANCHOR_READER_DB")"
export OMNIA_WITNESS_PITR_CI_ONLY=1
export OMNIA_WITNESS_PITR_HMAC_KEY_PATH="$private_dir/hmac.key"
openssl rand 32 > "$OMNIA_WITNESS_PITR_HMAC_KEY_PATH"
chmod 600 "$OMNIA_WITNESS_PITR_HMAC_KEY_PATH"

phase() {
  dotnet run --no-build -c Release \
    --project tests/Omnia.WitnessPitrRehearsal/Omnia.WitnessPitrRehearsal.csproj \
    -- "$1"
}
if env -u OMNIA_WITNESS_PITR_CI_ONLY \
  dotnet run --no-build -c Release \
    --project tests/Omnia.WitnessPitrRehearsal/Omnia.WitnessPitrRehearsal.csproj \
    -- --bootstrap >/dev/null 2>&1; then
  echo "Witness PITR harness accepted non-opted-in execution." >&2
  exit 1
fi
if phase --verify-source >/dev/null 2>&1; then
  echo "Missing third-cluster checkpoint was accepted." >&2
  exit 1
fi
phase --bootstrap
phase --verify-source
if phase --bootstrap >/dev/null 2>&1; then
  echo "Repeated checkpoint bootstrap was accepted." >&2
  exit 1
fi

# Complete physical basebackup and verify its manifest. No restore files,
# WAL, passwords or keys leave the disposable runner.
docker exec -u postgres "$source_container" pg_basebackup \
  -h /var/run/postgresql -U omnia -D "$base_path" \
  --format=plain --wal-method=stream --checkpoint=fast --no-password
docker exec -u postgres "$source_container" pg_verifybackup "$base_path"

# These marker writes and the named restore point are POST-backup WAL.
# The anchor advance and committed erasure occur strictly AFTER the point.
docker exec "$source_container" psql -X -v ON_ERROR_STOP=1 -U omnia -d omnia_witness \
  -c 'CREATE TABLE witness.omnia_ci_pitr_marker (marker text PRIMARY KEY)' >/dev/null
docker exec "$source_container" psql -X -v ON_ERROR_STOP=1 -U omnia -d omnia_witness \
  -c "INSERT INTO witness.omnia_ci_pitr_marker VALUES ('before-target')" >/dev/null
docker exec "$source_container" psql -X -v ON_ERROR_STOP=1 -U omnia -d omnia_witness \
  -c "SELECT pg_create_restore_point('$restore_point')" >/dev/null

phase --append-and-anchor
phase --verify-source
docker exec "$source_container" psql -X -v ON_ERROR_STOP=1 -U omnia -d omnia_witness \
  -c "INSERT INTO witness.omnia_ci_pitr_marker VALUES ('after-target')" >/dev/null

source_erasure="$(docker exec "$source_container" psql -U omnia -d omnia_witness -Atqc \
  "SELECT count(*) FROM witness.erasure_events WHERE tenant_id = 'omnia-ci-witness-pitr-synthetic'")"
[[ "$source_erasure" == 1 ]] || {
  echo "Committed post-target witness erasure missing." >&2; exit 1
}
source_sequence="$(docker exec "$source_container" psql -U omnia -d omnia_witness -Atqc \
  'SELECT max(sequence) FROM witness.erasure_events')"
anchor_sequence="$(docker exec "$anchor_container" psql -U omnia -d omnia_witness_pitr_anchor -Atqc \
  'SELECT checkpoint_count FROM anchor.head WHERE singleton = 1')"
[[ "$source_sequence" == "$anchor_sequence" ]] || {
  echo "Third-cluster checkpoint did not attest the post-point event." >&2; exit 1
}
anchor_bytes="$(docker exec "$anchor_container" psql -U omnia -d omnia_witness_pitr_anchor -Atqc \
  'SELECT encode(checkpoint, '\''hex'\'') FROM anchor.checkpoints ORDER BY checkpoint_count DESC LIMIT 1')"
[[ -n "$anchor_bytes" ]] || { echo "External checkpoint missing." >&2; exit 1; }
source_system="$(docker exec "$source_container" psql -U omnia -d omnia_witness -Atqc \
  'SELECT system_identifier FROM pg_control_system()')"
anchor_system="$(docker exec "$anchor_container" psql -U omnia -d omnia_witness_pitr_anchor -Atqc \
  'SELECT system_identifier FROM pg_control_system()')"
[[ -n "$source_system" && -n "$anchor_system" && "$source_system" != "$anchor_system" ]] || {
  echo "Witness and anchor share the same physical cluster identity." >&2; exit 1
}

# Archive the WAL segment containing the post-target erase before recovery.
last_segment="$(docker exec "$source_container" psql -U omnia -d omnia_witness -Atqc \
  'SELECT pg_walfile_name(pg_current_wal_lsn())')"
[[ "$last_segment" =~ ^[0-9A-F]{24}$ ]] || {
  echo "Unexpected witness WAL segment name." >&2; exit 1
}
docker exec "$source_container" psql -U omnia -d omnia_witness -Atqc \
  'SELECT pg_switch_wal()' >/dev/null
archived=0
for i in $(seq 1 60); do
  if docker exec "$source_container" test -s "$archive_path/$last_segment" >/dev/null 2>&1; then
    archived=1
    break
  fi
  sleep 1
done
[[ "$archived" == 1 ]] || { echo "Witness WAL segment not archived." >&2; exit 1; }

docker cp "$source_container:$base_path/." "$private_dir/base/"
docker cp "$source_container:$archive_path/." "$private_dir/archive/"
[[ -s "$private_dir/base/backup_manifest" &&
   -s "$private_dir/base/global/pg_control" &&
   -s "$private_dir/archive/$last_segment" ]]

cat > "$private_dir/pitr-config" <<'CONF'
restore_command = 'cp /var/lib/postgresql/data/omnia_ci_witness_wal_archive/%f %p'
recovery_target_name = 'omnia_ci_before_witness_pitr_erasure'
recovery_target_action = 'promote'
recovery_target_timeline = 'current'
CONF
chmod 600 "$private_dir/pitr-config"

docker volume create "$volume" >/dev/null
volume_created=1
docker run --rm --entrypoint bash \
  -v "$volume:/var/lib/postgresql/data" -v "$private_dir:/private:ro" \
  postgres:17 -ceu '
  cp -a /private/base/. /var/lib/postgresql/data/
  mkdir -m 700 /var/lib/postgresql/data/omnia_ci_witness_wal_archive
  cp -a /private/archive/. /var/lib/postgresql/data/omnia_ci_witness_wal_archive/
  cat /private/pitr-config >> /var/lib/postgresql/data/postgresql.auto.conf
  touch /var/lib/postgresql/data/recovery.signal
  chmod 700 /var/lib/postgresql/data
  chown -R postgres:postgres /var/lib/postgresql/data
' >/dev/null

docker run -d --rm --name "$recovery_container" \
  -v "$volume:/var/lib/postgresql/data" \
  -p 127.0.0.1:15439:5432 postgres:17 >/dev/null
ready=0
for i in $(seq 1 90); do
  if docker exec "$recovery_container" pg_isready -U omnia -d omnia_witness >/dev/null 2>&1; then
    ready=1; break
  fi
  sleep 1
done
[[ "$ready" == 1 ]] || {
  echo "Witness physical PITR did not reach the named WAL target." >&2; exit 1
}
before="$(docker exec "$recovery_container" psql -U omnia -d omnia_witness -Atqc \
  "SELECT count(*) FROM witness.omnia_ci_pitr_marker WHERE marker = 'before-target'")"
after="$(docker exec "$recovery_container" psql -U omnia -d omnia_witness -Atqc \
  "SELECT count(*) FROM witness.omnia_ci_pitr_marker WHERE marker = 'after-target'")"
recovered_erasure="$(docker exec "$recovery_container" psql -U omnia -d omnia_witness -Atqc \
  "SELECT count(*) FROM witness.erasure_events WHERE tenant_id = 'omnia-ci-witness-pitr-synthetic'")"
recovered_sequence="$(docker exec "$recovery_container" psql -U omnia -d omnia_witness -Atqc \
  'SELECT max(sequence) FROM witness.erasure_events')"
recovered_system="$(docker exec "$recovery_container" psql -U omnia -d omnia_witness -Atqc \
  'SELECT system_identifier FROM pg_control_system()')"
recovery_status="$(docker exec "$recovery_container" psql -U omnia -d omnia_witness -Atqc \
  'SELECT pg_is_in_recovery()')"
[[ "$before" == 1 && "$after" == 0 && "$recovered_erasure" == 0 &&
   "$recovered_sequence" -lt "$anchor_sequence" &&
   "$source_system" == "$recovered_system" && "$recovery_status" == f ]] || {
  echo "Physical witness PITR did not roll back the exact post-point event." >&2; exit 1
}

# Physical recovery inherited the reader credential. Rotate it before audit.
new_reader_password="$(openssl rand -hex 24)"
echo "::add-mask::$new_reader_password"
docker exec -i "$recovery_container" psql -X -v ON_ERROR_STOP=1 \
  -U omnia -d omnia_witness -v reader_password="$new_reader_password" >/dev/null <<'SQL'
SELECT format('ALTER ROLE omnia_witness_reader PASSWORD %L', :'reader_password') \gexec
SQL
export OMNIA_WITNESS_PITR_RECOVERED_READER_DB="Host=127.0.0.1;Port=15439;Database=omnia_witness;Username=omnia_witness_reader;Password=$new_reader_password"
old_reader="$(sed 's/Port=15436;/Port=15439;/' <<< "$OMNIA_TEST_WITNESS_READER_DB")"
if OMNIA_WITNESS_PITR_RECOVERED_READER_DB="$old_reader" \
  phase --verify-recovered >/dev/null 2>&1; then
  echo "Recovered witness accepted stale login credentials." >&2; exit 1
fi
unset old_reader new_reader_password

# Expected FAIL-CLOSED verification: the independent third-cluster anchor
# is ahead of the recovered witness; no signing or readiness change.
phase --verify-recovered
# The LIVE witness remains valid. The third PGDATA was never recovered.
phase --verify-source
anchor_after="$(docker exec "$anchor_container" psql -U omnia -d omnia_witness_pitr_anchor -Atqc \
  'SELECT encode(checkpoint, '\''hex'\'') FROM anchor.checkpoints ORDER BY checkpoint_count DESC LIMIT 1')"
anchor_seq_after="$(docker exec "$anchor_container" psql -U omnia -d omnia_witness_pitr_anchor -Atqc \
  'SELECT checkpoint_count FROM anchor.head WHERE singleton = 1')"
[[ "$anchor_bytes" == "$anchor_after" && "$anchor_seq_after" == "$anchor_sequence" ]] || {
  echo "Independent anchor was modified by physical witness recovery." >&2; exit 1
}
echo "PHYSICAL_WITNESS_PITR_ROLLBACK_DETECTED_WITH_SURVIVING_ANCHOR"
