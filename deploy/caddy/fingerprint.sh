#!/bin/sh
# Prints the SHA-256 fingerprint of the server's certificate (used with Caddyfile.ip).
set -eu
openssl x509 -in "$(dirname "$0")/certs/server.crt" -noout -fingerprint -sha256 | sed 's/^.*=//; s/://g'
