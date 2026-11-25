#!/usr/bin/env bash
# Go-live checklist of docker/.env and the host, BEFORE exposing Tao. Nothing is changed.
#   docker/preflight.sh            (checks docker/.env)
#   docker/preflight.sh path/.env
set -uo pipefail
cd "$(dirname "$0")"
env_file=${1:-.env}
failed=0

ok() { echo "ok   $1"; }
warn() { echo "WARN $1"; }
bad() { echo "FAIL $1" >&2; failed=1; }
[[ -f $env_file ]] || { echo "FAIL $env_file not found: run docker/setup.sh <public-url> first" >&2; exit 1; }
val() { grep -E "^$1=" "$env_file" | tail -1 | cut -d= -f2-; }

url=$(val TAO_PUBLIC_URL)
case "$url" in
  https://localhost*|https://127.*) warn "TAO_PUBLIC_URL is $url: a tunnel needs a real hostname" ;;
  https://*) ok "TAO_PUBLIC_URL is $url" ;;
  http://localhost*|http://127.*) warn "TAO_PUBLIC_URL is $url: fine for a test, not for going online" ;;
  *) bad "TAO_PUBLIC_URL must be the https address people will open (got '${url:-empty}')" ;;
esac

for name in POSTGRES_PASSWORD APP_DB_PASSWORD RETENTION_DB_PASSWORD KEYCLOAK_DB_PASSWORD KEYCLOAK_ADMIN_PASSWORD; do
  secret=$(val "$name")
  if [[ -z $secret || $secret == *REPLACE_WITH* || ${#secret} -lt 24 ]]; then bad "$name is empty, a placeholder or too short"; fi
done
[[ $failed == 0 ]] && ok "database and admin secrets are set"

mode=$(stat -c %a "$env_file" 2>/dev/null || stat -f %Lp "$env_file" 2>/dev/null || echo "")
if [[ -n $mode && $((8#$mode & 8#077)) -ne 0 ]]; then warn "$env_file is readable by other users (mode $mode): chmod 600"; else ok "$env_file is private"; fi

providers=$(val TAO_SOCIAL_LOGIN)
if [[ -z $providers ]]; then
  warn "TAO_SOCIAL_LOGIN is empty: nobody can sign in with Google or Microsoft and the stock Keycloak login stays"
else
  for provider in ${providers//,/ }; do
    upper=$(echo "$provider" | tr '[:lower:]' '[:upper:]')
    if [[ -z $(val "${upper}_CLIENT_ID") || -z $(val "${upper}_CLIENT_SECRET") ]]; then
      bad "TAO_SOCIAL_LOGIN lists $provider but ${upper}_CLIENT_ID or ${upper}_CLIENT_SECRET is empty"
    else
      ok "$provider sign-in is configured (redirect URI: $url/auth/realms/tao/broker/$provider/endpoint)"
    fi
  done
fi

case "$(val TAO_HTTP_BIND)" in
  ""|127.0.0.1) ok "the gateway listens on the host only" ;;
  *) warn "TAO_HTTP_BIND is $(val TAO_HTTP_BIND): restrict the port with the server firewall" ;;
esac

ring=$(val TAO_KEYRING_FILE); ring=${ring:-./secrets/envelope.keyring.json}
if [[ -f $ring ]] && grep -q '"activeKeyId"' "$ring"; then
  ok "key ring found ($ring): keep a second copy OFF this server, notes cannot be decrypted without it"
else
  bad "key ring $ring is missing or invalid: run docker/setup.sh"
fi

backups=$(val TAO_BACKUP_DIR)
if [[ -z $backups || $backups == ./backups ]]; then
  warn "TAO_BACKUP_DIR is the default inside the repository: use an encrypted disk, ideally another machine"
else
  [[ -d $backups ]] && ok "backups go to $backups" || bad "TAO_BACKUP_DIR $backups does not exist"
fi

free_kb=$(df -Pk . | awk 'NR==2 {print $4}')
if [[ -n $free_kb && $free_kb -lt 5242880 ]]; then warn "less than 5 GB free on this disk"; else ok "disk space"; fi

if command -v docker >/dev/null 2>&1; then
  if docker compose --env-file "$env_file" -f compose.yml config -q 2>/dev/null; then ok "compose configuration is valid"
  else bad "docker compose config fails: check $env_file"; fi
else
  warn "docker is not installed on this machine"
fi
[[ -n $(val TUNNEL_TOKEN) ]] && ok "TUNNEL_TOKEN is set (docker compose --profile tunnel up -d)" \
  || warn "TUNNEL_TOKEN is empty: run your own cloudflared or set it to use the tunnel profile"

exit "$failed"
