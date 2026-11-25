#!/usr/bin/env bash
# Builds the API and Keycloak images and pushes them to the registry. Needs `az login` and Docker.
#   infra/scripts/build-push.sh <acr-name>
set -euo pipefail
acr=${1:?registry name}
cd "$(dirname "$0")/../.."
server=$(az acr show -n "$acr" --query loginServer -o tsv)
tag=${GITHUB_SHA:-$(git rev-parse --short HEAD)}
az acr login -n "$acr"

docker build -f docker/Dockerfile.api -t "$server/tao-api:latest" -t "$server/tao-api:${tag:0:12}" .
docker build -f docker/keycloak/Dockerfile -t "$server/tao-keycloak:latest" -t "$server/tao-keycloak:${tag:0:12}" docker/keycloak
for image in tao-api tao-keycloak; do
  docker push "$server/$image:latest"
  docker push "$server/$image:${tag:0:12}"
done
