#!/usr/bin/env bash
# Synthetic PostgreSQL/pgvector backup → independent DB restore. CI-only.
set -euo pipefail
: "${OMNIA_TEST_APP_DB:?restricted test runtime DB required}"
: "${OMNIA_TEST_KEYRING_FILE:?ephemeral test KEK ring required}"
test -f "$OMNIA_TEST_KEYRING_FILE"
docker inspect omnia-ci-pg >/dev/null

restore_db="omnia_restore_drill"
backup_file="/tmp/omnia-restore-rehearsal.dump"
rotated_ring="$(mktemp "${RUNNER_TEMP:-/tmp}/omnia-rotated.XXXXXX")"
missing_old_ring="$(mktemp "${RUNNER_TEMP:-/tmp}/omnia-missing-old.XXXXXX")"
chmod 600 "$rotated_ring" "$missing_old_ring"
cleanup() {
  # All destructive calls restricted to disposable CI Docker database.
  docker exec omnia-ci-pg dropdb -U omnia --if-exists "$restore_db" >/dev/null 2>&1 || true
  docker exec omnia-ci-pg rm -f "$backup_file" >/dev/null 2>&1 || true
  rm -f "$rotated_ring" "$missing_old_ring"
}
trap cleanup EXIT

restored_cs="${OMNIA_TEST_APP_DB/Database=omnia_test/Database=$restore_db}"
if [[ "$restored_cs" == "$OMNIA_TEST_APP_DB" ]]; then
  echo "Invalid restore database selector." >&2
  exit 1
fi

OMNIA_REHEARSAL_DB="$OMNIA_TEST_APP_DB" \
OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- seed

docker exec omnia-ci-pg pg_dump -U omnia -d omnia_test -Fc -f "$backup_file"
docker exec omnia-ci-pg chmod 600 "$backup_file"
docker exec omnia-ci-pg createdb -U omnia "$restore_db"
docker exec omnia-ci-pg pg_restore -U omnia -d "$restore_db" --exit-on-error --no-owner "$backup_file"

old_id="$(jq -er '.activeKeyId' "$OMNIA_TEST_KEYRING_FILE")"
old_kek="$(jq -er --arg id "$old_id" '.keys[$id]' "$OMNIA_TEST_KEYRING_FILE")"
new_id="omnia-rehearsal-v2"
[[ "$old_id" != "$new_id" ]]
new_kek="$(openssl rand -base64 32)"
jq -nc --arg oldId "$old_id" --arg old "$old_kek" --arg newId "$new_id" --arg newer "$new_kek" \
  '{activeKeyId:$newId, keys:{($oldId):$old, ($newId):$newer}}' > "$rotated_ring"
jq -nc --arg newId "$new_id" --arg newer "$new_kek" \
  '{activeKeyId:$newId, keys:{($newId):$newer}}' > "$missing_old_ring"
unset old_kek new_kek

# The restored ciphertext is decryptable with retained old KEK, despite
# active ID changing. Missing old KEK MUST fail closed.
OMNIA_REHEARSAL_DB="$restored_cs" \
OMNIA_REHEARSAL_KEYRING_PATH="$rotated_ring" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- verify
OMNIA_REHEARSAL_DB="$restored_cs" \
OMNIA_REHEARSAL_KEYRING_PATH="$missing_old_ring" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- missing-old-key
OMNIA_REHEARSAL_DB="$restored_cs" \
OMNIA_REHEARSAL_KEYRING_PATH="$rotated_ring" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- erase
OMNIA_REHEARSAL_DB="$OMNIA_TEST_APP_DB" \
OMNIA_REHEARSAL_KEYRING_PATH="$OMNIA_TEST_KEYRING_FILE" \
  dotnet run --no-build -c Release --project tests/Omnia.RestoreRehearsal -- verify

echo "Synthetic PostgreSQL restore rehearsal passed; key continuity required."
