#!/usr/bin/env bash
# CI-only: physically roll back THIRD PostgreSQL claim cluster with
# pg_basebackup + named WAL PITR, while FOURTH independent PGDATA survives.
# The fourth one-shot attempt must still deny retry of the erased claim.
# Never use as recovery-ready authorization or production erasure proof.
set -euo pipefail
: "${RUNNER_TEMP:?}"
: "${CI:?}"
: "${GITHUB_ACTIONS:?}"
: "${OMNIA_TEST_EXECUTION_CLAIM_READER_DB:?}"
: "${OMNIA_TEST_EXECUTION_CLAIM_WRITER_DB:?}"
: "${OMNIA_TEST_FOURTH_CLAIM_ADMIN_DB:?}"
: "${OMNIA_TEST_FOURTH_CLAIM_WRITER_DB:?}"
: "${OMNIA_TEST_FOURTH_CLAIM_READER_DB:?}"
[[ "$CI" == true && "$GITHUB_ACTIONS" == true ]]
third=omnia-ci-witness-anchor
fourth=omnia-ci-fourth-claim-fence
clone=omnia-ci-third-claim-recovered
db=omnia_ci_claim_pitr
third_archive=/tmp/omnia-ci-third-claim-wal-archive
third_base=/tmp/omnia-ci-third-claim-base
target=omnia_ci_third_claim_before_attempt
volume="omnia-ci-third-claim-pitr-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT"
private="$(mktemp -d "$RUNNER_TEMP/omnia-third-claim-pitr.XXXXXX")"
chmod 700 "$private"
mkdir -m 700 "$private/base" "$private/archive"
created=0
cleanup() {
  docker rm -f "$clone" >/dev/null 2>&1 || true
  if [[ "$created" == 1 ]]; then
    docker volume rm "$volume" >/dev/null 2>&1 || true
  fi
  docker exec "$third" rm -rf "$third_base" >/dev/null 2>&1 || true
  rm -rf -- "$private"
}
trap cleanup EXIT

docker inspect "$third" >/dev/null
docker inspect "$fourth" >/dev/null
[[ "$(docker exec "$third" psql -U omnia -d postgres -Atqc 'SHOW archive_mode')" == on ]] || {
  echo "Third cluster WAL archiving unavailable." >&2; exit 1
}
third_system="$(docker exec "$third" psql -U omnia -d postgres -Atqc   'SELECT system_identifier FROM pg_control_system()')"
fourth_system="$(docker exec "$fourth" psql -U omnia -d omnia_fourth_claims -Atqc   'SELECT system_identifier FROM pg_control_system()')"
[[ -n "$third_system" && -n "$fourth_system" && "$third_system" != "$fourth_system" ]] || {
  echo "Third and fourth cluster PGDATAs are not independent." >&2; exit 1
}

# Isolated dedicated test database on THIRD physical cluster. Nothing in
# this script can mutate the real source erasure outbox or the public API.
docker exec "$third" psql -X -v ON_ERROR_STOP=1 -U omnia -d postgres   -c "CREATE DATABASE $db" >/dev/null
docker exec -i "$third" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$db"   < tests/restore/preerase-execution-claims.sql >/dev/null

export OMNIA_CLAIM_PITR_THIRD_WRITER_DB
export OMNIA_CLAIM_PITR_THIRD_READER_DB
OMNIA_CLAIM_PITR_THIRD_WRITER_DB="$(sed "s/Database=omnia_execution_claims;/Database=$db;/" <<< "$OMNIA_TEST_EXECUTION_CLAIM_WRITER_DB")"
OMNIA_CLAIM_PITR_THIRD_READER_DB="$(sed "s/Database=omnia_execution_claims;/Database=$db;/" <<< "$OMNIA_TEST_EXECUTION_CLAIM_READER_DB")"
export OMNIA_CLAIM_PITR_REQUEST_PATH="$private/request.id"
export OMNIA_CLAIM_PITR_KEY_PATH="$private/key.bin"
cat /proc/sys/kernel/random/uuid > "$OMNIA_CLAIM_PITR_REQUEST_PATH"
openssl rand 32 > "$OMNIA_CLAIM_PITR_KEY_PATH"
chmod 600 "$OMNIA_CLAIM_PITR_REQUEST_PATH" "$OMNIA_CLAIM_PITR_KEY_PATH"
export OMNIA_CLAIM_PITR_CI_ONLY=1
phase() {
  dotnet run --no-build -c Release     --project tests/Omnia.ClaimPitrRehearsal/Omnia.ClaimPitrRehearsal.csproj     -- "$1"
}

# The old request is not yet consumed in either cluster.
if phase --verify-live >/dev/null 2>&1; then
  echo "Fourth fence falsely accepted an absent attempt." >&2; exit 1
fi
if env -u OMNIA_CLAIM_PITR_CI_ONLY     dotnet run --no-build -c Release       --project tests/Omnia.ClaimPitrRehearsal/Omnia.ClaimPitrRehearsal.csproj       -- --claim >/dev/null 2>&1; then
  echo "Claim PITR CLI bypassed CI opt-in." >&2; exit 1
fi

# Physical basebackup BEFORE both the WAL target and the claim attempt.
docker exec -u postgres "$third" pg_basebackup   -h /var/run/postgresql -U omnia -D "$third_base"   --format=plain --wal-method=stream --checkpoint=fast --no-password
docker exec -u postgres "$third" pg_verifybackup "$third_base"
docker exec "$third" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$db"   -c 'CREATE TABLE admission.omnia_ci_claim_pitr_marker (marker text PRIMARY KEY)' >/dev/null
docker exec "$third" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$db"   -c "INSERT INTO admission.omnia_ci_claim_pitr_marker VALUES ('before-target')" >/dev/null
docker exec "$third" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$db"   -c "SELECT pg_create_restore_point('$target')" >/dev/null

# Strict order: FOURTH ACK -> THIRD claim -> (no primary DELETE in this
# isolated drill). Both claims are valid actor-scoped HMAC evidence.
phase --claim
phase --verify-live
docker exec "$third" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$db"   -c "INSERT INTO admission.omnia_ci_claim_pitr_marker VALUES ('after-target')" >/dev/null
request="$(cat "$OMNIA_CLAIM_PITR_REQUEST_PATH")"
[[ "$request" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]]
[[ "$(docker exec "$third" psql -U omnia -d "$db" -Atqc   "SELECT count(*) FROM admission.execution_claims WHERE request_id = '$request'")" == 1 ]]
fourth_hmac="$(docker exec "$fourth" psql -U omnia -d omnia_fourth_claims -Atqc   "SELECT encode(scope_hmac, 'hex') FROM admission.execution_claims WHERE request_id = '$request'")"
[[ "$fourth_hmac" =~ ^[0-9a-f]{64}$ ]] || {
  echo "Fourth PGDATA did not retain HMAC-authenticated attempt." >&2; exit 1
}

# Archive the complete WAL containing the post-target insert.
last_segment="$(docker exec "$third" psql -U omnia -d "$db" -Atqc   'SELECT pg_walfile_name(pg_current_wal_lsn())')"
[[ "$last_segment" =~ ^[0-9A-F]{24}$ ]]
docker exec "$third" psql -U omnia -d "$db" -Atqc   'SELECT pg_switch_wal()' >/dev/null
archived=0
for i in $(seq 1 60); do
  if docker exec "$third" test -s "$third_archive/$last_segment" >/dev/null 2>&1; then
    archived=1; break
  fi
  sleep 1
done
[[ "$archived" == 1 ]] || {
  echo "Third PGDATA did not archive post-target WAL." >&2; exit 1
}

docker cp "$third:$third_base/." "$private/base/"
docker cp "$third:$third_archive/." "$private/archive/"
[[ -s "$private/base/backup_manifest" &&
   -s "$private/base/global/pg_control" &&
   -s "$private/archive/$last_segment" ]]

cat > "$private/pitr-config" <<'CONF'
restore_command = 'cp /var/lib/postgresql/data/omnia_ci_third_claim_wal_archive/%f %p'
recovery_target_name = 'omnia_ci_third_claim_before_attempt'
recovery_target_action = 'promote'
recovery_target_timeline = 'current'
CONF
chmod 600 "$private/pitr-config"
docker volume create "$volume" >/dev/null
created=1
docker run --rm --entrypoint bash   -v "$volume:/var/lib/postgresql/data" -v "$private:/private:ro"   postgres:17 -ceu '
  cp -a /private/base/. /var/lib/postgresql/data/
  mkdir -m 700 /var/lib/postgresql/data/omnia_ci_third_claim_wal_archive
  cp -a /private/archive/. /var/lib/postgresql/data/omnia_ci_third_claim_wal_archive/
  cat /private/pitr-config >> /var/lib/postgresql/data/postgresql.auto.conf
  touch /var/lib/postgresql/data/recovery.signal
  chmod 700 /var/lib/postgresql/data
  chown -R postgres:postgres /var/lib/postgresql/data
' >/dev/null
docker run -d --rm --name "$clone"   -v "$volume:/var/lib/postgresql/data"   -p 127.0.0.1:15441:5432 postgres:17 >/dev/null
ready=0
for i in $(seq 1 90); do
  if docker exec "$clone" pg_isready -U omnia -d "$db" >/dev/null 2>&1; then
    ready=1; break
  fi
  sleep 1
done
[[ "$ready" == 1 ]] || {
  echo "Third physical claim PITR was not promoted." >&2; exit 1
}
clone_system="$(docker exec "$clone" psql -U omnia -d "$db" -Atqc   'SELECT system_identifier FROM pg_control_system()')"
before="$(docker exec "$clone" psql -U omnia -d "$db" -Atqc   "SELECT count(*) FROM admission.omnia_ci_claim_pitr_marker WHERE marker = 'before-target'")"
after="$(docker exec "$clone" psql -U omnia -d "$db" -Atqc   "SELECT count(*) FROM admission.omnia_ci_claim_pitr_marker WHERE marker = 'after-target'")"
recovered_count="$(docker exec "$clone" psql -U omnia -d "$db" -Atqc   "SELECT count(*) FROM admission.execution_claims WHERE request_id = '$request'")"
recovered_mode="$(docker exec "$clone" psql -U omnia -d "$db" -Atqc   'SELECT pg_is_in_recovery()')"
[[ "$clone_system" == "$third_system" &&
   "$clone_system" != "$fourth_system" &&
   "$before" == 1 && "$after" == 0 &&
   "$recovered_count" == 0 && "$recovered_mode" == f ]] || {
  echo "Physical rollback did not remove the exact third claim." >&2; exit 1
}

# The cloned THIRD PGDATA inherited the reader's password: revoke it
# before any post-recovery audit, then check the restricted role boundary.
recovered_password="$(openssl rand -hex 24)"
echo "::add-mask::$recovered_password"
docker exec -i "$clone" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$db"   -v reader_password="$recovered_password" >/dev/null <<'SQL'
SELECT format('ALTER ROLE omnia_claim_reader PASSWORD %L', :'reader_password') \gexec
SQL
export OMNIA_CLAIM_PITR_RECOVERED_THIRD_READER_DB="Host=127.0.0.1;Port=15441;Database=$db;Username=omnia_claim_reader;Password=$recovered_password"
old_reader="$(sed 's/Port=15438;/Port=15441;/' <<< "$OMNIA_CLAIM_PITR_THIRD_READER_DB")"
if OMNIA_CLAIM_PITR_RECOVERED_THIRD_READER_DB="$old_reader"   phase --verify-recovered >/dev/null 2>&1; then
  echo "Recovered third PostgreSQL accepted the inherited stale reader." >&2
  exit 1
fi
unset old_reader recovered_password

# Fourth's unchanged, actor-bound claim must BLOCK the retry even when
# THIRD is physically rolled back and missing its entire post-point claim.
phase --verify-recovered
phase --verify-live
after_hmac="$(docker exec "$fourth" psql -U omnia -d omnia_fourth_claims -Atqc   "SELECT encode(scope_hmac, 'hex') FROM admission.execution_claims WHERE request_id = '$request'")"
[[ "$after_hmac" == "$fourth_hmac" ]] || {
  echo "Surviving FOURTH-PGDATA HMAC claim changed during third PITR." >&2
  exit 1
}
echo "PHYSICAL_THIRD_PITR_FOURTH_CLAIM_SURVIVED"
echo "PHYSICAL_THIRD_PITR_REMAINED_QUARANTINED"
