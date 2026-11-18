#!/bin/sh
# Creates the HTTPS certificate for a server reached by IP address (used with Caddyfile.ip) and prints the
# fingerprint to enter in the Dashboard and Machine apps.
#
# The certificate is self-signed and valid for 10 years. Keeping one fixed certificate means the fingerprint never
# changes, so the apps only need it once. An existing certificate is kept; delete deploy/caddy/certs to make a new
# one (every app then needs the new fingerprint).
#
# Run on the VPS from anywhere:  sh /opt/tellogameroom/deploy/caddy/make-cert.sh
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
CERTS="$HERE/certs"
ENV_FILE="$HERE/../docker/.env"

ADDRESS="${1:-}"
if [ -z "$ADDRESS" ] && [ -f "$ENV_FILE" ]; then
  ADDRESS=$(sed -n 's/^TELLO_ADDRESS=//p' "$ENV_FILE" | tr -d '\r' | head -n 1)
fi
if [ -z "$ADDRESS" ]; then
  echo "Usage: make-cert.sh <server IP address>   (or set TELLO_ADDRESS in deploy/docker/.env)" >&2
  exit 1
fi

mkdir -p "$CERTS"
if [ ! -f "$CERTS/server.crt" ] || [ ! -f "$CERTS/server.key" ]; then
  case "$ADDRESS" in
    *[!0-9.]*) SAN="DNS:$ADDRESS" ;;
    *) SAN="IP:$ADDRESS" ;;
  esac
  openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 3650 \
    -keyout "$CERTS/server.key" -out "$CERTS/server.crt" \
    -subj "/CN=TelloGameRoom $ADDRESS" -addext "subjectAltName=$SAN" \
    -addext "basicConstraints=critical,CA:FALSE" -addext "extendedKeyUsage=serverAuth" 2>/dev/null
  # Caddy runs as root in its container; the key stays readable only by root on the host.
  chmod 600 "$CERTS/server.key"
  echo "Created a new certificate for $ADDRESS" >&2
fi

openssl x509 -in "$CERTS/server.crt" -noout -fingerprint -sha256 | sed 's/^.*=//; s/://g'
