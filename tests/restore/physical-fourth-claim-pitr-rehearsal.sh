#!/usr/bin/env bash
# Synthetic CI-only PHYSICAL PITR of FOURTH claim PGDATA with THIRD intact.
# A lost fourth claim must NOT authorize a replay: the surviving third must
# reject the same UUID. No primary DELETE, API release or real data access.
set -euo pipefail
: "${RUNNER_TEMP:?}"
: "${CI:?}"
: "${GITHUB_ACTIONS:?}"
: "${GITHUB_RUN_ID:?}"
: "${GITHUB_RUN_ATTEMPT:?}"
: "${OMNIA_TEST_FOURTH_CLAIM_WRITER_DB:?}"
: "${OMNIA_TEST_FOURTH_CLAIM_READER_DB:?}"
: "${OMNIA_TEST_EXECUTION_CLAIM_WRITER_DB:?}"
: "${OMNIA_TEST_EXECUTION_CLAIM_READER_DB:?}"
[[ "$CI" == true && "$GITHUB_ACTIONS" == true ]]
fourth=omnia-ci-fourth-claim-fence
third=omnia-ci-witness-anchor
clone=omnia-ci-fourth-claim-recovered
db=omnia_ci_fourth_claim_pitr
archive=/tmp/omnia-ci-fourth-claim-wal-archive
base=/tmp/omnia-ci-fourth-claim-base
target=omnia_ci_fourth_claim_before_attempt
volume="omnia-ci-fourth-claim-pitr-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT"
private="$(mktemp -d "$RUNNER_TEMP/omnia-fourth-claim-pitr.XXXXXX")"
chmod 700 "$private"
mkdir -m 700 "$private/base" "$private/archive"
created=0
cleanup() {
  docker rm -f "$clone" >/dev/null 2>&1 || true
  if [[ "$created" == 1 ]]; then docker volume rm "$volume" >/dev/null 2>&1 || true; fi
  docker exec "$fourth" rm -rf "$base" >/dev/null 2>&1 || true
  rm -rf -- "$private"
}
trap cleanup EXIT

docker inspect "$fourth" >/dev/null
docker inspect "$third" >/dev/null
[[ "$(docker exec "$fourth" psql -U omnia -d omnia_fourth_claims -Atqc 'SHOW archive_mode')" == on ]] || {
  echo 'Fourth PGDATA WAL archiving unavailable.' >&2; exit 1
}
fourth_system="$(docker exec "$fourth" psql -U omnia -d postgres -Atqc 'SELECT system_identifier FROM pg_control_system()')"
third_system="$(docker exec "$third" psql -U omnia -d postgres -Atqc 'SELECT system_identifier FROM pg_control_system()')"
[[ -n "$fourth_system" && -n "$third_system" && "$fourth_system" != "$third_system" ]] || {
  echo 'Fourth and third PGDATAs are not physically independent.' >&2; exit 1
}
# A separate fourth database keeps the xUnit claim ledger untouched.
docker exec "$fourth" psql -X -v ON_ERROR_STOP=1 -U omnia -d postgres -c "CREATE DATABASE $db" >/dev/null
docker exec -i "$fourth" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$db" < tests/restore/preerase-execution-claims.sql >/dev/null
export OMNIA_CLAIM_PITR_FOURTH_WRITER_DB
export OMNIA_CLAIM_PITR_FOURTH_READER_DB
OMNIA_CLAIM_PITR_FOURTH_WRITER_DB="$(sed "s/Database=omnia_fourth_claims;/Database=$db;/" <<< "$OMNIA_TEST_FOURTH_CLAIM_WRITER_DB")"
OMNIA_CLAIM_PITR_FOURTH_READER_DB="$(sed "s/Database=omnia_fourth_claims;/Database=$db;/" <<< "$OMNIA_TEST_FOURTH_CLAIM_READER_DB")"
export OMNIA_CLAIM_PITR_REQUEST_PATH="$private/request.id"
export OMNIA_CLAIM_PITR_KEY_PATH="$private/key.bin"
cat /proc/sys/kernel/random/uuid > "$OMNIA_CLAIM_PITR_REQUEST_PATH"
openssl rand 32 > "$OMNIA_CLAIM_PITR_KEY_PATH"
chmod 600 "$OMNIA_CLAIM_PITR_REQUEST_PATH" "$OMNIA_CLAIM_PITR_KEY_PATH"
export OMNIA_CLAIM_PITR_CI_ONLY=1
phase() {
  dotnet run --no-build -c Release \
    --project tests/Omnia.ClaimPitrRehearsal/Omnia.ClaimPitrRehearsal.csproj \
    -- "$1"
}
if phase --verify-fourth-live >/dev/null 2>&1; then
  echo 'Unclaimed fourth request was accepted.' >&2; exit 1
fi
if env -u OMNIA_CLAIM_PITR_CI_ONLY \
  dotnet run --no-build -c Release \
    --project tests/Omnia.ClaimPitrRehearsal/Omnia.ClaimPitrRehearsal.csproj \
    -- --claim-fourth >/dev/null 2>&1; then
  echo 'Fourth PITR CLI bypassed CI-only opt-in.' >&2; exit 1
fi

# Basebackup precedes WAL recovery point, claim and post-target marker.
docker exec -u postgres "$fourth" pg_basebackup \
  -h /var/run/postgresql -U omnia -D "$base" \
  --format=plain --wal-method=stream --checkpoint=fast --no-password
docker exec -u postgres "$fourth" pg_verifybackup "$base"
docker exec "$fourth" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$db" \
  -c 'CREATE TABLE admission.omnia_ci_fourth_claim_pitr_marker (marker text PRIMARY KEY)' >/dev/null
docker exec "$fourth" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$db" \
  -c "INSERT INTO admission.omnia_ci_fourth_claim_pitr_marker VALUES ('before-target')" >/dev/null
docker exec "$fourth" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$db" \
  -c "SELECT pg_create_restore_point('$target')" >/dev/null
phase --claim-fourth
phase --verify-fourth-live
docker exec "$fourth" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$db" \
  -c "INSERT INTO admission.omnia_ci_fourth_claim_pitr_marker VALUES ('after-target')" >/dev/null
request="$(cat "$OMNIA_CLAIM_PITR_REQUEST_PATH")"
[[ "$request" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]]
[[ "$(docker exec "$fourth" psql -U omnia -d "$db" -Atqc \
  "SELECT count(*) FROM admission.execution_claims WHERE request_id = '$request'")" == 1 ]]
third_hmac="$(docker exec "$third" psql -U omnia -d omnia_execution_claims -Atqc \
  "SELECT encode(scope_hmac, 'hex') FROM admission.execution_claims WHERE request_id = '$request'")"
[[ "$third_hmac" =~ ^[0-9a-f]{64}$ ]] || {
  echo 'Surviving third claim HMAC is missing.' >&2; exit 1
}
last_segment="$(docker exec "$fourth" psql -U omnia -d "$db" -Atqc \
  'SELECT pg_walfile_name(pg_current_wal_lsn())')"
[[ "$last_segment" =~ ^[0-9A-F]{24}$ ]]
docker exec "$fourth" psql -U omnia -d "$db" -Atqc 'SELECT pg_switch_wal()' >/dev/null
archived=0
for i in $(seq 1 60); do
  if docker exec "$fourth" test -s "$archive/$last_segment" >/dev/null 2>&1; then
    archived=1; break
  fi
  sleep 1
done
[[ "$archived" == 1 ]] || { echo 'Fourth WAL archive incomplete.' >&2; exit 1; }
docker cp "$fourth:$base/." "$private/base/"
docker cp "$fourth:$archive/." "$private/archive/"
[[ -s "$private/base/backup_manifest" && -s "$private/base/global/pg_control" &&
   -s "$private/archive/$last_segment" ]]

cat > "$private/pitr-config" <<'CONF'
restore_command = 'cp /var/lib/postgresql/data/omnia_ci_fourth_claim_wal_archive/%f %p'
recovery_target_name = 'omnia_ci_fourth_claim_before_attempt'
recovery_target_action = 'promote'
recovery_target_timeline = 'current'
CONF
chmod 600 "$private/pitr-config"
docker volume create "$volume" >/dev/null
created=1
docker run --rm --entrypoint bash \
  -v "$volume:/var/lib/postgresql/data" -v "$private:/private:ro" \
  postgres:17 -ceu '
  cp -a /private/base/. /var/lib/postgresql/data/
  mkdir -m 700 /var/lib/postgresql/data/omnia_ci_fourth_claim_wal_archive
  cp -a /private/archive/. /var/lib/postgresql/data/omnia_ci_fourth_claim_wal_archive/
  cat /private/pitr-config >> /var/lib/postgresql/data/postgresql.auto.conf
  touch /var/lib/postgresql/data/recovery.signal
  chmod 700 /var/lib/postgresql/data
  chown -R postgres:postgres /var/lib/postgresql/data
' >/dev/null
docker run -d --rm --name "$clone" \
  -v "$volume:/var/lib/postgresql/data" \
  -p 127.0.0.1:15442:5432 postgres:17 >/dev/null
ready=0
for i in $(seq 1 90); do
  if docker exec "$clone" pg_isready -U omnia -d "$db" >/dev/null 2>&1; then
    ready=1; break
  fi
  sleep 1
done
[[ "$ready" == 1 ]] || { echo 'Fourth PITR clone failed to start.' >&2; exit 1; }
clone_system="$(docker exec "$clone" psql -U omnia -d "$db" -Atqc 'SELECT system_identifier FROM pg_control_system()')"
before="$(docker exec "$clone" psql -U omnia -d "$db" -Atqc \
  "SELECT count(*) FROM admission.omnia_ci_fourth_claim_pitr_marker WHERE marker = 'before-target'")"
after="$(docker exec "$clone" psql -U omnia -d "$db" -Atqc \
  "SELECT count(*) FROM admission.omnia_ci_fourth_claim_pitr_marker WHERE marker = 'after-target'")"
recovered="$(docker exec "$clone" psql -U omnia -d "$db" -Atqc \
  "SELECT count(*) FROM admission.execution_claims WHERE request_id = '$request'")"
in_recovery="$(docker exec "$clone" psql -U omnia -d "$db" -Atqc 'SELECT pg_is_in_recovery()')"
[[ "$clone_system" == "$fourth_system" && "$clone_system" != "$third_system" &&
   "$before" == 1 && "$after" == 0 && "$recovered" == 0 && "$in_recovery" == f ]] || {
  echo 'Physical fourth PITR did not restore the intended pre-attempt point.' >&2; exit 1
}
# Restore inherited global role passwords. Rotate BOTH writer and reader,
# verify old logins fail, and use only newly provisioned restricted logins.
new_writer="$(openssl rand -hex 24)"
new_reader="$(openssl rand -hex 24)"
echo "::add-mask::$new_writer"
echo "::add-mask::$new_reader"
docker exec -i "$clone" psql -X -v ON_ERROR_STOP=1 -U omnia -d "$db" \
  -v writer_password="$new_writer" -v reader_password="$new_reader" >/dev/null <<'SQL'
SELECT format('ALTER ROLE omnia_claim_writer PASSWORD %L', :'writer_password') \gexec
SELECT format('ALTER ROLE omnia_claim_reader PASSWORD %L', :'reader_password') \gexec
SQL
export OMNIA_CLAIM_PITR_RECOVERED_FOURTH_READER_DB="Host=127.0.0.1;Port=15442;Database=$db;Username=omnia_claim_reader;Password=$new_reader"
export OMNIA_CLAIM_PITR_RECOVERED_FOURTH_WRITER_DB="Host=127.0.0.1;Port=15442;Database=$db;Username=omnia_claim_writer;Password=$new_writer"
old_reader="$(sed 's/Port=15440;/Port=15442;/' <<< "$OMNIA_CLAIM_PITR_FOURTH_READER_DB")"
old_writer="$(sed 's/Port=15440;/Port=15442;/' <<< "$OMNIA_CLAIM_PITR_FOURTH_WRITER_DB")"
if OMNIA_CLAIM_PITR_RECOVERED_FOURTH_READER_DB="$old_reader" \
   phase --verify-fourth-recovered >/dev/null 2>&1; then
  echo 'Fourth recovery accepted old reader password.' >&2; exit 1
fi
if OMNIA_CLAIM_PITR_RECOVERED_FOURTH_WRITER_DB="$old_writer" \
   phase --verify-fourth-recovered >/dev/null 2>&1; then
  echo 'Fourth recovery accepted old writer password.' >&2; exit 1
fi
unset old_reader old_writer new_writer new_reader

# Recovered fourth can re-admit the UUID, but THIRD still rejects 23505.
# No real source DELETE or release attempt is performed.
phase --verify-fourth-recovered
phase --verify-fourth-live
after_hmac="$(docker exec "$third" psql -U omnia -d omnia_execution_claims -Atqc \
  "SELECT encode(scope_hmac, 'hex') FROM admission.execution_claims WHERE request_id = '$request'")"
[[ "$after_hmac" == "$third_hmac" ]] || {
  echo 'Third claim was modified during fourth PITR.' >&2; exit 1
}
echo 'PHYSICAL_FOURTH_PITR_THIRD_CLAIM_SURVIVED'
echo 'PHYSICAL_FOURTH_PITR_REMAINED_QUARANTINED'
