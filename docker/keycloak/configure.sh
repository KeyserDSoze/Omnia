#!/usr/bin/env bash
# Idempotent Keycloak settings applied at every start (the realm import runs only once).
# Optional social login with Google and Microsoft (TAO_SOCIAL_LOGIN=google,microsoft),
# restricted to users that already exist: the first time, the user confirms the link with
# their Tao password; afterwards the social login alone is enough. No self-registration.
set -euo pipefail

realm=tao
flow=tao-invite-only-first-login
kc() { /opt/keycloak/bin/kcadm.sh "$@" --config /tmp/kcadm.config; }
enabled() { [[ ",${TAO_SOCIAL_LOGIN:-}," == *",$1,"* ]]; }
json() { local s=${1//\\/\\\\}; printf '%s' "${s//\"/\\\"}"; }

kc config credentials --server http://keycloak:8080/auth --realm master \
  --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null

if enabled google || enabled microsoft; then
  if ! kc get authentication/flows -r "$realm" --fields alias --format csv --noquotes | grep -qx "$flow"; then
    kc create authentication/flows -r "$realm" -s alias="$flow" -s providerId=basic-flow \
      -s topLevel=true -s builtIn=false \
      -s description="Social login only for existing users, confirmed once with their password"
    for provider in idp-detect-existing-broker-user idp-confirm-link idp-username-password-form; do
      kc create "authentication/flows/$flow/executions/execution" -r "$realm" -s provider="$provider"
    done
  fi
  # Unknown e-mail: refused. Known e-mail: confirm the link and re-enter the Tao password once.
  for id in $(kc get "authentication/flows/$flow/executions" -r "$realm" --fields id --format csv --noquotes); do
    kc update "authentication/flows/$flow/executions" -r "$realm" -b "{\"id\":\"$id\",\"requirement\":\"REQUIRED\"}"
  done
fi

configure_idp() {
  local alias=$1 client_id=$2 secret=$3 extra=$4
  if ! enabled "$alias"; then
    if kc get "identity-provider/instances/$alias" -r "$realm" >/dev/null 2>&1; then
      kc update "identity-provider/instances/$alias" -r "$realm" -s enabled=false
    fi
    return 0
  fi
  if [[ -z "$client_id" || -z "$secret" ]]; then
    echo "TAO_SOCIAL_LOGIN includes $alias but its client id or secret is empty" >&2
    return 1
  fi
  local body="{\"alias\":\"$alias\",\"providerId\":\"$alias\",\"enabled\":true,\"trustEmail\":false,\
\"storeToken\":false,\"linkOnly\":false,\"firstBrokerLoginFlowAlias\":\"$flow\",\
\"config\":{\"clientId\":\"$(json "$client_id")\",\"clientSecret\":\"$(json "$secret")\",\"syncMode\":\"IMPORT\"$extra}}"
  if kc get "identity-provider/instances/$alias" -r "$realm" >/dev/null 2>&1; then
    printf '%s' "$body" | kc update "identity-provider/instances/$alias" -r "$realm" -f -
  else
    printf '%s' "$body" | kc create identity-provider/instances -r "$realm" -f -
  fi
}

configure_idp google "${GOOGLE_CLIENT_ID:-}" "${GOOGLE_CLIENT_SECRET:-}" ""
configure_idp microsoft "${MICROSOFT_CLIENT_ID:-}" "${MICROSOFT_CLIENT_SECRET:-}" \
  ",\"tenantId\":\"$(json "${MICROSOFT_TENANT:-organizations}")\""
echo "Keycloak social login: ${TAO_SOCIAL_LOGIN:-disabled}"
