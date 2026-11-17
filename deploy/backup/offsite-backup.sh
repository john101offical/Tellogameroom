#!/bin/sh
# Copies the newest automatic backup off the server. The server already writes one backup per day into
# its data volume (backups/auto-YYYYMMDD-HHMMSS.db) and keeps 14; this script protects you against losing the VPS.
#
# Run it daily from cron on the VPS, for example at 04:30:
#   30 4 * * * /opt/tellogameroom-repo/deploy/backup/offsite-backup.sh user@backup-host:/backups/tello >> /var/log/tello-backup.log 2>&1
#
# The destination is anything scp accepts. Replace the last line with rclone, aws s3 cp, etc. if you prefer.
set -eu

DEST="${1:?Usage: offsite-backup.sh user@host:/path}"
cd "$(dirname "$0")/../docker"

LATEST=$(docker compose exec -T server sh -c 'ls -1t /data/backups/auto-*.db 2>/dev/null | head -n 1')
if [ -z "$LATEST" ]; then
  echo "$(date -Is) no automatic backup found yet"
  exit 1
fi

TMP=$(mktemp)
trap 'rm -f "$TMP"' EXIT
docker compose cp "server:$LATEST" "$TMP"
scp -q "$TMP" "$DEST/$(basename "$LATEST")"
echo "$(date -Is) copied $(basename "$LATEST") to $DEST"
