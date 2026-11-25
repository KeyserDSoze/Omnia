#!/usr/bin/env bash
# Runs backup.sh now and then every TAO_BACKUP_INTERVAL_HOURS. A failed backup is reported and retried
# at the next interval; the loop never stops on its own.
set -uo pipefail
hours=${TAO_BACKUP_INTERVAL_HOURS:-24}
[[ $hours =~ ^[0-9]+$ && $hours -ge 1 ]] || { echo "TAO_BACKUP_INTERVAL_HOURS must be at least 1" >&2; exit 1; }
while true; do
  bash /opt/tao/backup.sh || echo "Backup failed at $(date -u +%FT%TZ)" >&2
  sleep "$((hours * 3600))"
done
