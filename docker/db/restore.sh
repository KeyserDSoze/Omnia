#!/usr/bin/env bash
# Restores the Tao and Keycloak databases from /backups/<name>, replacing the current data.
# Usage (from docker/): docker compose stop web api keycloak
#                       docker compose --profile backup run --rm restore tao-<timestamp> --yes
# Needs the roles from db-bootstrap (docker compose up -d db db-bootstrap) and the same key ring.
set -euo pipefail
: "${PGPASSWORD:?PGPASSWORD is required}"

name=${1:-}
confirm=${2:-}
if [[ ! $name =~ ^tao-[0-9]{8}T[0-9]{6}Z$ ]]; then
  echo "Usage: restore.sh tao-<timestamp> --yes   (invalid backup name)" >&2; exit 2
fi
dir=/backups/$name
[[ -d $dir ]] || { echo "No such backup: $dir" >&2; exit 2; }
if [[ $confirm != --yes ]]; then
  echo "This replaces the current databases. Run again with --yes." >&2; exit 2
fi

(cd "$dir" && sha256sum --check --quiet SHA256SUMS) || { echo "Backup checksum failed: nothing was changed." >&2; exit 1; }

for role in tao keycloak; do
  [[ $(psql -d postgres -Atc "SELECT 1 FROM pg_roles WHERE rolname = '$role'") == 1 ]] \
    || { echo "Role $role is missing: run docker compose up -d db db-bootstrap first." >&2; exit 1; }
done
active=$(psql -d postgres -Atc \
  "SELECT count(*) FROM pg_stat_activity WHERE datname IN ('tao', 'keycloak') AND pid <> pg_backend_pid()")
if [[ $active != 0 ]]; then
  echo "$active open sessions: stop the services first (docker compose stop web api keycloak)." >&2; exit 1
fi

psql -d postgres -v ON_ERROR_STOP=1 -c 'DROP DATABASE IF EXISTS tao' -c 'DROP DATABASE IF EXISTS keycloak' \
  -c 'CREATE DATABASE tao OWNER tao' -c 'CREATE DATABASE keycloak OWNER keycloak'
pg_restore --exit-on-error --no-owner --dbname=tao "$dir/tao.dump"
pg_restore --exit-on-error --no-owner --role=keycloak --dbname=keycloak "$dir/keycloak.dump"
echo "Restored $name. Start the stack: docker compose up -d"
