#!/usr/bin/env bash
# Restarts the two web apps so they pull the images just pushed, and waits until they answer.
#   infra/scripts/restart-and-wait.sh <resource-group> <api-app> <keycloak-app>
set -euo pipefail
group=${1:?resource group}
api=${2:?api app}
keycloak=${3:?keycloak app}

az webapp restart -g "$group" -n "$keycloak" -o none
az webapp restart -g "$group" -n "$api" -o none

wait_for() { # <label> <url> <pattern>
  for _ in $(seq 1 90); do
    if curl -fsS --max-time 20 "$2" 2>/dev/null | grep -q "$3"; then echo "ok   $1 answers"; return 0; fi
    sleep 10
  done
  echo "FAIL $1 did not answer at $2 within 15 minutes" >&2
  return 1
}
failed=0
wait_for "Keycloak" "https://$(az webapp show -g "$group" -n "$keycloak" --query defaultHostName -o tsv)/auth/realms/tao" '"realm":"tao"' || failed=1
wait_for "API" "https://$(az webapp show -g "$group" -n "$api" --query defaultHostName -o tsv)/health" '"ok"' || failed=1
exit "$failed"
