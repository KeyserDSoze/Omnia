#!/usr/bin/env bash
# Logical backup of the Tao and Keycloak databases into /backups/tao-<UTC timestamp>/.
# Run from docker/: docker compose --profile backup run --rm backup
# Keeps the newest TAO_BACKUP_KEEP backups (default 14). The key ring is NOT included: back it up
# separately, without it the sealed notes in these dumps cannot be decrypted.
set -euo pipefail
: "${PGPASSWORD:?PGPASSWORD is required}"
keep=${TAO_BACKUP_KEEP:-14}
[[ $keep =~ ^[0-9]+$ && $keep -ge 1 ]] || { echo "TAO_BACKUP_KEEP must be a number of at least 1" >&2; exit 1; }

stamp=$(date -u +%Y%m%dT%H%M%SZ)
final=/backups/tao-$stamp
partial=/backups/.partial-$stamp
umask 077
rm -rf "$partial"
mkdir -p "$partial"
trap 'rm -rf "$partial"' EXIT

for db in tao keycloak; do
  pg_dump --format=custom --dbname="$db" --file="$partial/$db.dump"
done
(cd "$partial" && sha256sum tao.dump keycloak.dump > SHA256SUMS)
mv "$partial" "$final"
echo "Backup written to $final"

# Retention: names sort by timestamp, drop all but the newest $keep.
ls -d /backups/tao-*/ 2>/dev/null | sort | head -n "-$keep" | xargs -r rm -rf
