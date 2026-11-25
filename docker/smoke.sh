#!/usr/bin/env bash
# Acceptance check of a RUNNING Tao gateway: routing, OIDC issuer, admin paths, headers and, when
# a compose command is given, that logs never contain OAuth codes or query strings.
#   docker/smoke.sh https://tao.example.com
#   docker/smoke.sh http://localhost:59800 "docker compose --env-file docker/.env -f docker/compose.yml"
set -uo pipefail
base=${1:?usage: smoke.sh <public-url> [compose command]}
base=${base%/}
compose=${2:-}
failed=0

ok() { echo "ok   $1"; }
bad() { echo "FAIL $1" >&2; failed=1; }
status() { curl -s -o /dev/null -w '%{http_code}' --max-time 20 "$@"; }
expect_status() { # <expected> <what> <curl args...>
  local want=$1 what=$2; shift 2
  local got; got=$(status "$@")
  if [[ $got == "$want" ]]; then ok "$what ($got)"; else bad "$what: expected $want, got $got"; fi
}
body() { curl -fsS --max-time 20 "$@" 2>/dev/null; }
headers() { curl -sS -D - -o /dev/null --max-time 20 "$@" 2>/dev/null | tr -d '\r'; }

expect_status 200 "gateway answers" "$base/healthz"
body "$base/api/health" | grep -q '"ok"' && ok "API is alive" || bad "API health"
issuer=$(body "$base/auth/realms/tao/.well-known/openid-configuration" | tr -d ' ' | grep -o '"issuer":"[^"]*"' | head -1)
[[ $issuer == "\"issuer\":\"$base/auth/realms/tao\"" ]] && ok "OIDC issuer is $base/auth/realms/tao" \
  || bad "OIDC issuer is ${issuer:-missing}, expected $base/auth/realms/tao (TAO_PUBLIC_URL)"
expect_status 401 "the API needs a token" "$base/api/v1/memories"
for path in /auth/admin /auth/admin/ /auth/realms/master /auth/realms/master/ /auth/realms/master/.well-known/openid-configuration; do
  expect_status 404 "Keycloak administration hidden at $path" "$base$path"
done
headers "$base/" | grep -qi '^content-security-policy:' && ok "content security policy" || bad "content security policy missing"
headers "$base/api/v1/memories" | grep -qi '^cache-control:.*no-store' && ok "API answers are no-store" || bad "API answers lack no-store"
if [[ $base == https://* ]]; then
  headers "$base/" | grep -qi '^strict-transport-security:' && ok "HSTS" || bad "HSTS missing (is the tunnel sending X-Forwarded-Proto: https?)"
fi

if [[ -n $compose ]]; then
  canary="tao-canary-$RANDOM$RANDOM"
  status "$base/auth/realms/tao/protocol/openid-connect/auth?client_id=tao-web&code=$canary&state=$canary" >/dev/null
  status "$base/api/v1/memories?cursor=$canary" >/dev/null
  status "$base/?code=$canary" >/dev/null
  sleep 3
  # shellcheck disable=SC2086
  if $compose logs --no-color web api keycloak 2>&1 | grep -q "$canary"; then
    bad "a query-string value reached the logs"
  else
    ok "query strings never reach the logs"
  fi
fi

exit "$failed"
