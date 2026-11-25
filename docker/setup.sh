#!/usr/bin/env bash
# First-time on-prem setup: docker/.env with random secrets + envelope key ring.
# Usage: docker/setup.sh [public-url]   e.g. docker/setup.sh https://tao.example.com
set -euo pipefail
cd "$(dirname "$0")"

if [[ -f .env ]]; then
  echo "docker/.env already exists: edit it manually (secrets are never regenerated)." >&2
else
  umask 077
  # Each placeholder gets its own random secret (portable awk, GNU and BSD).
  awk '{ while (index($0, "REPLACE_WITH_RANDOM_SECRET") > 0) {
           cmd = "openssl rand -hex 24"; cmd | getline secret; close(cmd)
           sub(/REPLACE_WITH_RANDOM_SECRET/, secret) }
         print }' .env.example > .env
  if [[ -n "${1:-}" ]]; then
    sed -i.bak "s#^TAO_PUBLIC_URL=.*#TAO_PUBLIC_URL=${1%/}#" .env && rm -f .env.bak
  fi
  echo "Created docker/.env"
fi

mkdir -p secrets
chmod 700 secrets
if [[ ! -f secrets/envelope.keyring.json ]]; then
  umask 077
  printf '{"activeKeyId":"k1","keys":{"k1":"%s"}}\n' "$(openssl rand -base64 32)" \
    > secrets/envelope.keyring.json
  # The API container runs as a non-root user: allow read without exposing to others.
  chmod 644 secrets/envelope.keyring.json
  echo "Created docker/secrets/envelope.keyring.json (back it up: without it notes cannot be decrypted)"
fi
