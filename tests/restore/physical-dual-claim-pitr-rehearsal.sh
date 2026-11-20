#!/usr/bin/env bash
# ADR 0042: real physical PITR of BOTH claim authorities from before the
# same attempted UUID. This is a negative CI-only disaster drill.
# It demonstrates the absence of an anti-rollback authority when both
# claim PGDATAs regress. Never touch primary data, grant recovery-ready,
# or attempt an actual erasure or production use.
set -euo pipefail
: "${RUNNER_TEMP:?}"
: "${CI:?}"
: "${GITHUB_ACTIONS:?}"
: "${GITHUB_RUN_ID:?}"
: "${GITHUB_RUN_ATTEMPT:?}"
: "${OMNIA_TEST_EXECUTION_CLAIM_WRITER_DB:?}"
: "${OMNIA_TEST_EXECUTION_CLAIM_READER_DB:?}"
: "${OMNIA_TEST_FOURTH_CLAIM_WRITER_DB:?}"
: "${OMNIA_TEST_FOURTH_CLAIM_READER_DB:?}"
[[ "$CI" == true && "$GITHUB_ACTIONS" == true ]]
third=omnia-ci-witness-anchor
fourth=omnia-ci-fourth-claim-fence
third_clone=omnia-ci-dual-third-recovered
fourth_clone=omnia-ci-dual-fourth-recovered
third_db=omnia_ci_dual_third_claims
fourth_db=omnia_ci_dual_fourth_claims
third_base=/tmp/omnia-ci-dual-third-base
fourth_base=/tmp/omnia-ci-dual-fourth-base
third_archive=/tmp/omnia-ci-third-claim-wal-archive
fourth_archive=/tmp/omnia-ci-fourth-claim-wal-archive
third_target=omnia_ci_dual_third_before_claim
fourth_target=omnia_ci_dual_fourth_before_claim
third_volume="omnia-ci-dual-third-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT"
fourth_volume="omnia-ci-dual-fourth-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT"
private="$(mktemp -d "$RUNNER_TEMP/omnia-dual-claim-pitr.XXXXXX")"
chmod 700 "$private"
mkdir -m 700 "$private/third" "$private/fourth"
created_third=0
created_fourth=0
cleanup() {
  docker rm -f "$third_clone" "$fourth_clone" >/dev/null 2>&1 || true
  [[ "$created_third" == 1 ]] && docker volume rm "$third_volume" >/dev/null 2>&1 || true
  [[ "$created_fourth" == 1 ]] && docker volume rm "$fourth_volume" >/dev/null 2>&1 || true
  docker exec "$third" rm -rf "$third_base" >/dev/null 2>&1 || true
  docker exec "$fourth" rm -rf "$fourth_base" >/dev/null 2>&1 || true
  rm -rf -- "$private"
}
trap cleanup EXIT
for c in "$third" "$fourth"; do docker inspect "$c" >/dev/null; done
[[ "$(docker exec "$third" psql -U omnia -d postgres -Atqc 'SHOW archive_mode')" == on &&
   "$(docker exec "$fourth" psql -U omnia -d postgres -Atqc 'SHOW archive_mode')" == on ]] || {
  echo 'Both authorities require WAL archiving.' >&2; exit 1
}
third_system="$(docker exec "$third" psql -U omnia -d postgres -Atqc 'SELECT system_identifier FROM pg_control_system()')"
fourth_system="$(docker exec "$fourth" psql -U omnia -d postgres -Atqc 'SELECT system_identifier FROM pg_control_system()')"
[[ -n "$third_system" && -n "$fourth_system" && "$third_system" != "$fourth_system" ]] || {
  echo 'Dual recovery must use two physically distinct authorities.' >&2; exit 1
}

# Dedicated synthetic databases and identity-restricted SQL functions;
# do not mutate the original xUnit or prior recovery drill databases.
docker exec "$third" psql -X -v ON_ERROR_STOP=1 -U omnia -d postgres -c "CREATE DATABASE $third_db" >/dev/null
docker exec "$fourth" psql -X -v ON_ERROR_STOP=1 -U omnia -d postgres -c "CREATE DATABASE $fourth_db" >/dev/null
docker exec -i "$third" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$third_db" < tests/restore/preerase-execution-claims.sql >/dev/null
docker exec -i "$fourth" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$fourth_db" < tests/restore/preerase-execution-claims.sql >/dev/null
export OMNIA_DUAL_PITR_THIRD_WRITER_DB OMNIA_DUAL_PITR_THIRD_READER_DB
export OMNIA_DUAL_PITR_FOURTH_WRITER_DB OMNIA_DUAL_PITR_FOURTH_READER_DB
OMNIA_DUAL_PITR_THIRD_WRITER_DB="$(sed "s/Database=omnia_execution_claims;/Database=$third_db;/" <<< "$OMNIA_TEST_EXECUTION_CLAIM_WRITER_DB")"
OMNIA_DUAL_PITR_THIRD_READER_DB="$(sed "s/Database=omnia_execution_claims;/Database=$third_db;/" <<< "$OMNIA_TEST_EXECUTION_CLAIM_READER_DB")"
OMNIA_DUAL_PITR_FOURTH_WRITER_DB="$(sed "s/Database=omnia_fourth_claims;/Database=$fourth_db;/" <<< "$OMNIA_TEST_FOURTH_CLAIM_WRITER_DB")"
OMNIA_DUAL_PITR_FOURTH_READER_DB="$(sed "s/Database=omnia_fourth_claims;/Database=$fourth_db;/" <<< "$OMNIA_TEST_FOURTH_CLAIM_READER_DB")"
export OMNIA_CLAIM_PITR_REQUEST_PATH="$private/request.id"
export OMNIA_CLAIM_PITR_KEY_PATH="$private/key.bin"
cat /proc/sys/kernel/random/uuid > "$OMNIA_CLAIM_PITR_REQUEST_PATH"
openssl rand 32 > "$OMNIA_CLAIM_PITR_KEY_PATH"
chmod 600 "$OMNIA_CLAIM_PITR_REQUEST_PATH" "$OMNIA_CLAIM_PITR_KEY_PATH"
export OMNIA_CLAIM_PITR_CI_ONLY=1
phase() {
  dotnet run --no-build -c Release \
    --project tests/Omnia.ClaimPitrRehearsal/Omnia.ClaimPitrRehearsal.csproj -- "$1"
}
# An absent claim is never an approval. The new phase is initially
# missing during TDD RED; successful GREEN requires a deliberate
# nonzero 42 to signal that the dual-rollback protection is absent.
if phase --dual-verify-live >/dev/null 2>&1; then
  echo 'No synthetic claim should exist yet.' >&2; exit 1
fi
if env -u OMNIA_CLAIM_PITR_CI_ONLY \
  dotnet run --no-build -c Release \
    --project tests/Omnia.ClaimPitrRehearsal/Omnia.ClaimPitrRehearsal.csproj \
    -- --dual-claim >/dev/null 2>&1; then
  echo 'Dual PITR CLI bypassed CI-only opt-in.' >&2; exit 1
fi

# Each physical basebackup is verified BEFORE its named restore point.
docker exec -u postgres "$third" pg_basebackup \
  -h /var/run/postgresql -U omnia -D "$third_base" \
  --format=plain --wal-method=stream --checkpoint=fast --no-password
docker exec -u postgres "$third" pg_verifybackup "$third_base"
docker exec -u postgres "$fourth" pg_basebackup \
  -h /var/run/postgresql -U omnia -D "$fourth_base" \
  --format=plain --wal-method=stream --checkpoint=fast --no-password
docker exec -u postgres "$fourth" pg_verifybackup "$fourth_base"
for pair in "$third:$third_db:$third_target" "$fourth:$fourth_db:$fourth_target"; do
  IFS=: read -r c db target <<< "$pair"
  docker exec "$c" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$db" \
    -c 'CREATE TABLE admission.omnia_ci_dual_marker (marker text PRIMARY KEY)' >/dev/null
  docker exec "$c" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$db" \
    -c "INSERT INTO admission.omnia_ci_dual_marker VALUES ('before-target')" >/dev/null
  docker exec "$c" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$db" \
    -c "SELECT pg_create_restore_point('$target')" >/dev/null
done

# Both RESTORE POINTS precede BOTH claim commits.
# We use the same UUID, scope and random key for the two authorities.
phase --dual-claim
phase --dual-verify-live
request="$(cat "$OMNIA_CLAIM_PITR_REQUEST_PATH")"
[[ "$request" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]]
for pair in "$third:$third_db" "$fourth:$fourth_db"; do
  IFS=: read -r c db <<< "$pair"
  docker exec "$c" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$db" \
    -c "INSERT INTO admission.omnia_ci_dual_marker VALUES ('after-target')" >/dev/null
  [[ "$(docker exec "$c" psql -U omnia -d "$db" -Atqc \
    "SELECT count(*) FROM admission.execution_claims WHERE request_id='$request'")" == 1 ]]
done

# Include the complete post-target WAL in the private runner archive.
for pair in "$third:$third_db:$third_archive" "$fourth:$fourth_db:$fourth_archive"; do
  IFS=: read -r c db archive <<< "$pair"
  segment="$(docker exec "$c" psql -U omnia -d "$db" -Atqc 'SELECT pg_walfile_name(pg_current_wal_lsn())')"
  [[ "$segment" =~ ^[0-9A-F]{24}$ ]]
  docker exec "$c" psql -U omnia -d "$db" -Atqc 'SELECT pg_switch_wal()' >/dev/null
  present=0
  for i in $(seq 1 60); do
    if docker exec "$c" test -s "$archive/$segment" >/dev/null 2>&1; then
      present=1; break
    fi
    sleep 1
  done
  [[ "$present" == 1 ]] || { echo 'Dual PITR WAL archive incomplete.' >&2; exit 1; }
done

# Distinct physical clones, fixed loopback ports, no production PGDATA.
restore() {
  local c="$1" base="$2" archive="$3" target="$4" clone="$5" volume="$6" port="$7" dst="$8"
  mkdir -m 700 "$dst/base" "$dst/archive"
  docker cp "$c:$base/." "$dst/base/"
  docker cp "$c:$archive/." "$dst/archive/"
  [[ -s "$dst/base/backup_manifest" && -s "$dst/base/global/pg_control" ]]
  cat > "$dst/pitr-config" <<CONF
restore_command = 'cp /var/lib/postgresql/data/omnia_ci_dual_wal_archive/%f %p'
recovery_target_name = '$target'
recovery_target_action = 'promote'
recovery_target_timeline = 'current'
CONF
  chmod 600 "$dst/pitr-config"
  docker volume create "$volume" >/dev/null
  if [[ "$port" == 15443 ]]; then created_third=1; else created_fourth=1; fi
  docker run --rm --entrypoint bash -v "$volume:/var/lib/postgresql/data" \
    -v "$dst:/private:ro" postgres:17 -ceu '
      cp -a /private/base/. /var/lib/postgresql/data/
      mkdir -m 700 /var/lib/postgresql/data/omnia_ci_dual_wal_archive
      cp -a /private/archive/. /var/lib/postgresql/data/omnia_ci_dual_wal_archive/
      cat /private/pitr-config >> /var/lib/postgresql/data/postgresql.auto.conf
      touch /var/lib/postgresql/data/recovery.signal
      chmod 700 /var/lib/postgresql/data
      chown -R postgres:postgres /var/lib/postgresql/data
    ' >/dev/null
  docker run -d --rm --name "$clone" -v "$volume:/var/lib/postgresql/data" \
    -p "127.0.0.1:$port:5432" postgres:17 >/dev/null
  ready=0
  for i in $(seq 1 90); do
    if docker exec "$clone" pg_isready -U omnia -d postgres >/dev/null 2>&1; then
      ready=1; break
    fi
    sleep 1
  done
  [[ "$ready" == 1 ]] || { echo 'Dual PITR clone did not start.' >&2; exit 1; }
}
restore "$third" "$third_base" "$third_archive" "$third_target" "$third_clone" "$third_volume" 15443 "$private/third"
restore "$fourth" "$fourth_base" "$fourth_archive" "$fourth_target" "$fourth_clone" "$fourth_volume" 15444 "$private/fourth"

# Exact physical identity, named WAL marker ordering, promoted clones,
# lost UUIDs. The living sources MUST still retain the authenticated claims.
for pair in "$third_clone:$third_db:$third_system" "$fourth_clone:$fourth_db:$fourth_system"; do
  IFS=: read -r c db sid <<< "$pair"
  [[ "$(docker exec "$c" psql -U omnia -d "$db" -Atqc \
    'SELECT system_identifier FROM pg_control_system()')" == "$sid" &&
     "$(docker exec "$c" psql -U omnia -d "$db" -Atqc 'SELECT pg_is_in_recovery()')" == f &&
     "$(docker exec "$c" psql -U omnia -d "$db" -Atqc \
       "SELECT count(*) FROM admission.omnia_ci_dual_marker WHERE marker='before-target'")" == 1 &&
     "$(docker exec "$c" psql -U omnia -d "$db" -Atqc \
       "SELECT count(*) FROM admission.omnia_ci_dual_marker WHERE marker='after-target'")" == 0 &&
     "$(docker exec "$c" psql -U omnia -d "$db" -Atqc \
       "SELECT count(*) FROM admission.execution_claims WHERE request_id='$request'")" == 0 ]] || {
    echo 'Dual PITR must lose both post-target claims.' >&2; exit 1
  }
done
[[ "$(docker exec "$third" psql -U omnia -d "$third_db" -Atqc \
  "SELECT count(*) FROM admission.execution_claims WHERE request_id='$request'")" == 1 &&
   "$(docker exec "$fourth" psql -U omnia -d "$fourth_db" -Atqc \
  "SELECT count(*) FROM admission.execution_claims WHERE request_id='$request'")" == 1 ]]

# No old clone credentials may be reused after recovery. No raw or
# privileged SQL connection is passed to the C# admission adapter.
for pair in "$third_clone:$third_db:THIRD:15443" "$fourth_clone:$fourth_db:FOURTH:15444"; do
  IFS=: read -r c db side port <<< "$pair"
  writer="$(openssl rand -hex 24)"
  reader="$(openssl rand -hex 24)"
  echo "::add-mask::$writer"
  echo "::add-mask::$reader"
  docker exec -i "$c" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$db" \
    -v writer_password="$writer" -v reader_password="$reader" >/dev/null <<'SQL'
SELECT format('ALTER ROLE omnia_claim_writer PASSWORD %L', :'writer_password') \gexec
SELECT format('ALTER ROLE omnia_claim_reader PASSWORD %L', :'reader_password') \gexec
SQL
  printf -v "OMNIA_DUAL_PITR_RECOVERED_${side}_WRITER_DB" '%s' \
    "Host=127.0.0.1;Port=$port;Database=$db;Username=omnia_claim_writer;Password=$writer"
  printf -v "OMNIA_DUAL_PITR_RECOVERED_${side}_READER_DB" '%s' \
    "Host=127.0.0.1;Port=$port;Database=$db;Username=omnia_claim_reader;Password=$reader"
  export "OMNIA_DUAL_PITR_RECOVERED_${side}_WRITER_DB"
  export "OMNIA_DUAL_PITR_RECOVERED_${side}_READER_DB"
done

# Verify stale credentials are refused without issuing any claim.
old_third_reader="$(sed 's/Port=15438;/Port=15443;/' <<< "$OMNIA_DUAL_PITR_THIRD_READER_DB")"
old_fourth_reader="$(sed 's/Port=15440;/Port=15444;/' <<< "$OMNIA_DUAL_PITR_FOURTH_READER_DB")"
if OMNIA_DUAL_PITR_RECOVERED_THIRD_READER_DB="$old_third_reader" \
  phase --dual-probe-recovered >/dev/null 2>&1; then
  echo 'Recovered third retained old reader credential.' >&2; exit 1
fi
if OMNIA_DUAL_PITR_RECOVERED_FOURTH_READER_DB="$old_fourth_reader" \
  phase --dual-probe-recovered >/dev/null 2>&1; then
  echo 'Recovered fourth retained old reader credential.' >&2; exit 1
fi

# The test MUST observe that the two restored authorities BOTH
# re-accept the UUID. That is a dangerous vulnerability, NOT success
# of the anti-rollback design. CLI exit 42 denotes fail-closed recovery.
set +e
phase --dual-probe-recovered > "$private/probe.log" 2>&1
status=$?
set -e
[[ "$status" == 42 ]] && grep -Fq 'DUAL_PITR_BOTH_CLAIMS_CAN_BE_REUSED_NO_RELEASE' "$private/probe.log" || {
  echo 'Dual claim rollback did not expose the expected unsafe state.' >&2
  exit 1
}
phase --dual-verify-live
# Both recovered UUIDs have now been consumed again; no source DELETE
# was attempted. An HMAC or claim alone is never a commit receipt.
for pair in "$third_clone:$third_db" "$fourth_clone:$fourth_db"; do
  IFS=: read -r c db <<< "$pair"
  [[ "$(docker exec "$c" psql -U omnia -d "$db" -Atqc \
    "SELECT count(*) FROM admission.execution_claims WHERE request_id='$request'")" == 1 ]]
done
echo 'PHYSICAL_DUAL_CLAIM_PITR_UNSAFE_REPLAY_OBSERVED'
echo 'PHYSICAL_DUAL_CLAIM_PITR_REMAINED_QUARANTINED'
