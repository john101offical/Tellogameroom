#!/bin/sh
# Prints the SHA-256 fingerprint of Caddy's root certificate (used with Caddyfile.ip).
# Run on the VPS from the repository root, after "docker compose up -d".
set -eu
cd "$(dirname "$0")/../docker"
docker compose exec -T caddy cat /data/caddy/pki/authorities/local/root.crt \
  | openssl x509 -noout -fingerprint -sha256 \
  | sed 's/^.*=//; s/://g'
