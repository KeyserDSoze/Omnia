#!/usr/bin/env bash
# Idempotent Keycloak settings applied at every start (the realm import runs only once).
# Social login with Google and Microsoft (TAO_SOCIAL_LOGIN=google,microsoft), and nothing else.
#   The first social sign-in creates the Tao user, identified by the e-mail. Keycloak's built-in
#   first-login flow links a second provider to an existing e-mail only after proof of the first.
#   With a provider enabled the realm browser flow only redirects to the provider: there is no
#   Tao password form. Without any provider the stock flow stays, so a fresh install is not locked out.
set -euo pipefail

realm=tao
server=${KEYCLOAK_SERVER:-http://keycloak:8080/auth}
flow="first broker login"
social_flow=tao-social-only-browser
kc() { /opt/keycloak/bin/kcadm.sh "$@" --config /tmp/kcadm.config; }
enabled() { [[ ",${TAO_SOCIAL_LOGIN:-}," == *",$1,"* ]]; }
json() { local s=${1//\\/\\\\}; printf '%s' "${s//\"/\\\"}"; }

kc config credentials --server "$server" --realm master \
  --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null

# Keycloak's own administration is a login too: lock out guessing, whether or not it is public.
kc update realms/master -s bruteForceProtected=true -s failureFactor=5

if enabled google || enabled microsoft; then
  if ! kc get authentication/flows -r "$realm" --fields alias --format csv --noquotes | grep -qx "$social_flow"; then
    kc create authentication/flows -r "$realm" -s alias="$social_flow" -s providerId=basic-flow \
      -s topLevel=true -s builtIn=false -s description="Redirect to the social provider chosen by the app"
    kc create "authentication/flows/$social_flow/executions/execution" -r "$realm" \
      -s provider=identity-provider-redirector
  fi
  for id in $(kc get "authentication/flows/$social_flow/executions" -r "$realm" --fields id --format csv --noquotes); do
    kc update "authentication/flows/$social_flow/executions" -r "$realm" -b "{\"id\":\"$id\",\"requirement\":\"REQUIRED\"}"
  done
  kc update "realms/$realm" -s browserFlow="$social_flow"
else
  kc update "realms/$realm" -s browserFlow=browser
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

google_extra=",\"validateSignature\":\"true\""
[[ -n "${GOOGLE_HOSTED_DOMAIN:-}" ]] && google_extra+=",\"hostedDomain\":\"$(json "$GOOGLE_HOSTED_DOMAIN")\""
configure_idp google "${GOOGLE_CLIENT_ID:-}" "${GOOGLE_CLIENT_SECRET:-}" "$google_extra"
configure_idp microsoft "${MICROSOFT_CLIENT_ID:-}" "${MICROSOFT_CLIENT_SECRET:-}" \
  ",\"tenantId\":\"$(json "${MICROSOFT_TENANT:-organizations}")\""
echo "Keycloak social login: ${TAO_SOCIAL_LOGIN:-disabled}"
