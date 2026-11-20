#!/usr/bin/env bash
# #109/#140 TDD: independent PostgreSQL 17/pgvector cluster logical recovery.
# SYNTHETIC CI ONLY; never pass production databases, files, credentials or KEKs.
set -euo pipefail
: "${OMNIA_TEST_APP_DB:?source test runtime DB required}"
: "${OMNIA_TEST_KEYRING_FILE:?ephemeral test keyring required}"
: "${RUNNER_TEMP:?CI private temporary directory required}"
[[ -f "$OMNIA_TEST_KEYRING_FILE" ]]
[[ "$OMNIA_TEST_APP_DB" == "Host=127.0.0.1;Port=15432;Database=omnia_test;Username=omnia_app;Password="* ]]
docker inspect omnia-ci-pg >/dev/null

source_container="omnia-ci-pg"
target_container="omnia-ci-pg-recovery"
target_database="omnia_recovery"
source_archive="/tmp/omnia-ci-independent-restore.dump"
target_archive="/tmp/omnia-ci-independent-restore.dump"
workdir="$(mktemp -d "$RUNNER_TEMP/omnia-ci-recovery.XXXXXX")"
chmod 700 "$workdir"
host_archive="$workdir/backup.dump"
retained_ring="$workdir/retained-ring.json"
missing_ring="$workdir/missing-ring.json"

cleanup() {
  # Never delete any non-disposable database. This entire container was
  # created by this test, with no shared PGDATA volume.
  docker rm -f "$target_container" >/dev/null 2>&1 || true
  docker exec "$source_container" rm -f "$source_archive" >/dev/null 2>&1 || true
  rm -rf -- "$workdir"
}
trap cleanup EXIT

# This must be the synthetic sealed note left by the in-cluster rehearsal.
OMNIA_REHEARSAL_DB="$OMNIA_TEST_APP_DB" \
OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- verify

# A real pg_dump is a secret-bearing archive, including non-sealed metadata.
# No stdout dump, GitHub artifact, uploaded backup or leaked passwords.
docker exec "$source_container" pg_dump -U omnia -d omnia_test -Fc -f "$source_archive"
docker exec "$source_container" chmod 600 "$source_archive"
docker cp "$source_container:$source_archive" "$host_archive"
chmod 600 "$host_archive"

# Fresh PostgreSQL cluster with DIFFERENT admin/app credentials and PGDATA.
# Reusing the original cluster's roles is specifically forbidden here.
recovery_admin_password="$(openssl rand -hex 24)"
recovery_app_password="$(openssl rand -hex 24)"
recovery_retention_password="$(openssl rand -hex 24)"
recovery_backfill_password="$(openssl rand -hex 24)"
recovery_audit_password="$(openssl rand -hex 24)"
recovery_dispatch_password="$(openssl rand -hex 24)"
echo "::add-mask::$recovery_admin_password"
echo "::add-mask::$recovery_app_password"
echo "::add-mask::$recovery_retention_password"
echo "::add-mask::$recovery_backfill_password"
echo "::add-mask::$recovery_audit_password"
echo "::add-mask::$recovery_dispatch_password"

docker run -d --rm --name "$target_container" \
  -e POSTGRES_USER=omnia -e POSTGRES_DB="$target_database" \
  -e "POSTGRES_PASSWORD=$recovery_admin_password" \
  -p 127.0.0.1:15433:5432 pgvector/pgvector:pg17 >/dev/null

for i in $(seq 1 60); do
  if docker exec "$target_container" pg_isready -U omnia -d "$target_database" >/dev/null 2>&1; then break; fi
  sleep 2
done
docker exec "$target_container" pg_isready -U omnia -d "$target_database" >/dev/null

source_system="$(docker exec "$source_container" psql -U omnia -d omnia_test -Atqc \
  'SELECT system_identifier FROM pg_control_system()')"
target_system="$(docker exec "$target_container" psql -U omnia -d "$target_database" -Atqc \
  'SELECT system_identifier FROM pg_control_system()')"
[[ -n "$source_system" && -n "$target_system" && "$source_system" != "$target_system" ]] || {
  echo "Independent database cluster identity check failed." >&2; exit 1;
}

docker cp "$host_archive" "$target_container:$target_archive"
docker exec "$target_container" chmod 600 "$target_archive"

# pg_dump does NOT include cluster-global PostgreSQL roles. Prove that
# ACL-bearing restore fails into an otherwise fresh cluster without roles.
no_roles="$(docker exec "$target_container" psql -U omnia -d "$target_database" -Atqc \
  "SELECT count(*) FROM pg_roles WHERE rolname IN ('omnia_app', 'omnia_retention', 'omnia_backfill', 'omnia_backfill_audit', 'omnia_dispatcher')")"
[[ "$no_roles" == "0" ]] || { echo "Recovery cluster was not pristine." >&2; exit 1; }
if docker exec "$target_container" pg_restore -U omnia -d "$target_database" \
    --no-owner --exit-on-error "$target_archive" >/dev/null 2>&1; then
  echo "Recovery unexpectedly succeeded without application roles." >&2
  exit 1
fi

# Reset failed partial restore and explicitly provision the five minimum roles.
# NEVER import pg_dumpall --globals-only credentials or reuse source secrets.
docker exec "$target_container" dropdb -U omnia "$target_database"
docker exec "$target_container" createdb -U omnia "$target_database"
docker exec -i "$target_container" psql -X -v ON_ERROR_STOP=1 \
  -U omnia -d "$target_database" \
  -v app_password="$recovery_app_password" \
  -v retention_password="$recovery_retention_password" \
  -v backfill_password="$recovery_backfill_password" \
  -v audit_password="$recovery_audit_password" \
  -v dispatch_password="$recovery_dispatch_password" >/dev/null <<'SQL'
SELECT format(
  'CREATE ROLE omnia_app LOGIN PASSWORD %L NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE',
  :'app_password'
) \gexec
SELECT format(
  'CREATE ROLE omnia_retention LOGIN PASSWORD %L NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT',
  :'retention_password'
) \gexec
SELECT format(
  'CREATE ROLE omnia_backfill LOGIN PASSWORD %L NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT',
  :'backfill_password'
) \gexec
SELECT format(
  'CREATE ROLE omnia_backfill_audit LOGIN PASSWORD %L NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT',
  :'audit_password'
) \gexec
SELECT format(
  'CREATE ROLE omnia_dispatcher LOGIN PASSWORD %L NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT',
  :'dispatch_password'
) \gexec
SQL

# Restore the schema, RLS, policies, ACL, vector extension and data as
# destination schema owner. Fail on any missing owner/grant/dependency.
docker exec "$target_container" pg_restore -U omnia -d "$target_database" \
  --no-owner --exit-on-error "$target_archive" >/dev/null

target_cs="Host=127.0.0.1;Port=15433;Database=omnia_recovery;Username=omnia_app;Password=$recovery_app_password"
source_password="${OMNIA_TEST_APP_DB##*Password=}"
# Assert the recovery server does NOT accept the original application login.
if OMNIA_REHEARSAL_DB="Host=127.0.0.1;Port=15433;Database=omnia_recovery;Username=omnia_app;Password=$source_password" \
   OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
   dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- verify >/dev/null 2>&1; then
  echo "Recovered database reused the source runtime credential." >&2
  exit 1
fi
unset source_password

# The backup cannot decrypt historical ciphertext without the historical KEK.
# A newly active KEK is safe only while the previous one is retained.
old_id="$(jq -er '.activeKeyId' "$OMNIA_TEST_KEYRING_FILE")"
old_kek="$(jq -er --arg id "$old_id" '.keys[$id]' "$OMNIA_TEST_KEYRING_FILE")"
new_id="omnia-independent-recovery-v2"
[[ "$new_id" != "$old_id" ]]
new_kek="$(openssl rand -base64 32)"
jq -nc --arg oldId "$old_id" --arg old "$old_kek" \
  --arg newId "$new_id" --arg fresh "$new_kek" \
  '{activeKeyId:$newId,keys:{($oldId):$old,($newId):$fresh}}' > "$retained_ring"
jq -nc --arg newId "$new_id" --arg fresh "$new_kek" \
  '{activeKeyId:$newId,keys:{($newId):$fresh}}' > "$missing_ring"
chmod 600 "$retained_ring" "$missing_ring"
unset old_id old_kek new_kek

# Run the existing non-owner RLS, TTL, export, AES-AAD and vector assertions
# against the NEW cluster, not the source.
OMNIA_REHEARSAL_DB="$target_cs" \
OMNIA_REHEARSAL_KEYRING_PATH="$retained_ring" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- verify
OMNIA_REHEARSAL_DB="$target_cs" \
OMNIA_REHEARSAL_KEYRING_PATH="$missing_ring" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- missing-old-key

# Verify erasure isolation across physical clusters: clone destroyed,
# source still contains original sealed note with its original KEK.
OMNIA_REHEARSAL_DB="$target_cs" \
OMNIA_REHEARSAL_KEYRING_PATH="$retained_ring" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- erase
OMNIA_REHEARSAL_DB="$OMNIA_TEST_APP_DB" \
OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- verify

echo "Independent PostgreSQL cluster restore, role recovery and key continuity passed."
