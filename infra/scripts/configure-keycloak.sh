#!/usr/bin/env bash
# Applies the sign-in providers and hardening to the deployed Keycloak with the production script
# (docker/keycloak/configure.sh), run from a Keycloak container against the public address.
#   infra/scripts/configure-keycloak.sh <key-vault-name> <keycloak-url> <social-providers>
set -euo pipefail
vault=${1:?key vault name}
url=${2:?keycloak url}
providers=${3:-}
cd "$(dirname "$0")/../.."

secret() { az keyvault secret show --vault-name "$vault" -n "$1" --query value -o tsv; }
admin_password=$(secret keycloak-admin-password)

docker run --rm --entrypoint bash \
  -e KEYCLOAK_SERVER="${url%/}/auth" -e KEYCLOAK_ADMIN=tao-admin -e KEYCLOAK_ADMIN_PASSWORD="$admin_password" \
  -e TAO_SOCIAL_LOGIN="$providers" -e TAO_APP_URL="${TAO_APP_URL:-}" \
  -e MICROSOFT_CLIENT_ID="${MICROSOFT_CLIENT_ID:-}" -e MICROSOFT_CLIENT_SECRET="$(secret microsoft-client-secret)" \
  -e MICROSOFT_TENANT="${MICROSOFT_TENANT:-common}" \
  -e GOOGLE_CLIENT_ID="${GOOGLE_CLIENT_ID:-}" -e GOOGLE_CLIENT_SECRET="$(secret google-client-secret)" \
  -e GOOGLE_HOSTED_DOMAIN="${GOOGLE_HOSTED_DOMAIN:-}" \
  -v "$PWD/docker/keycloak/configure.sh:/opt/tao/configure.sh:ro" \
  quay.io/keycloak/keycloak:26.4 /opt/tao/configure.sh
