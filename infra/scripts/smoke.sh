#!/usr/bin/env bash
# Acceptance check of the Azure deployment, from the outside: both apps answer, the issuer is the
# one the API trusts, the API needs a token and accepts the Pages origin.
#   infra/scripts/smoke.sh <api-url> <keycloak-url> <pages-origin>
set -uo pipefail
api=${1:?api url}; api=${api%/}
kc=${2:?keycloak url}; kc=${kc%/}
origin=${3:?pages origin}
failed=0
ok() { echo "ok   $1"; }
bad() { echo "FAIL $1" >&2; failed=1; }
status() { curl -s -o /dev/null -w '%{http_code}' --max-time 30 "$@"; }

curl -fsS --max-time 30 "$api/health" | grep -q '"ok"' && ok "API is alive" || bad "API health"
issuer=$(curl -fsS --max-time 30 "$kc/auth/realms/tao/.well-known/openid-configuration" | tr -d ' ' | grep -o '"issuer":"[^"]*"' | head -1)
[[ $issuer == "\"issuer\":\"$kc/auth/realms/tao\"" ]] && ok "OIDC issuer is $kc/auth/realms/tao" || bad "OIDC issuer is ${issuer:-missing}"
[[ $(status "$api/api/v1/memories") == 401 ]] && ok "the API needs a token" || bad "the API answers without a token"
[[ $(status "$api/api/login-options") == 200 ]] && ok "login options are public" || bad "login options"
cors=$(curl -s -D - -o /dev/null --max-time 30 -X OPTIONS -H "Origin: $origin" -H "Access-Control-Request-Method: GET" \
  -H "Access-Control-Request-Headers: authorization" "$api/api/v1/memories" | tr -d '\r' | grep -i '^access-control-allow-origin:' || true)
[[ $cors == *"$origin"* ]] && ok "CORS allows $origin" || bad "CORS does not allow $origin"
headers=$(curl -s -D - -o /dev/null --max-time 30 "$api/api/v1/memories" || true)
grep -qi '^cache-control:.*no-store' <<<"$headers" && ok "API answers are no-store" || bad "no-store missing"
exit "$failed"
