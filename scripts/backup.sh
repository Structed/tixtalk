#!/bin/bash
# Back up both PostgreSQL databases.
# Usage:
#   ./scripts/backup.sh                 # Run backup now
#   ./scripts/backup.sh --install-cron  # Install daily 3 AM cron job
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "$SCRIPT_DIR/lib/common.sh"

init_project_dir
cd "$PROJECT_DIR"

# Install cron job if requested
if [ "${1:-}" = "--install-cron" ]; then
    CRON_CMD="0 3 * * * cd $PROJECT_DIR && ./scripts/backup.sh >> /var/log/pretalxtix-backup.log 2>&1"
    # Get existing crontab (ignore error if none exists), filter out old backup entries, add new one
    # Note: grep -v returns 1 if no lines match, so we use || true to handle empty crontab
    ( crontab -l 2>/dev/null || true ) | ( grep -v "pretalxtix-backup" || true ) | { cat; echo "$CRON_CMD"; } | crontab -
    log "Installed daily backup cron job (3:00 AM)."
    echo "Logs: /var/log/pretalxtix-backup.log"
    exit 0
fi

# Load .env for DB credentials
load_env

BACKUP_DIR="$PROJECT_DIR/backups"
mkdir -p "$BACKUP_DIR"
TIMESTAMP=$(date +%Y%m%d-%H%M%S)

log "Starting backup..."

# Dump both databases
for DB in pretix pretalx; do
    BACKUP_FILE="$BACKUP_DIR/${DB}_${TIMESTAMP}.sql.gz"
    docker compose exec -T postgres pg_dump -U "$DB_USER" "$DB" | gzip > "$BACKUP_FILE"
    SIZE=$(du -h "$BACKUP_FILE" | cut -f1)
    echo "  $DB → $BACKUP_FILE ($SIZE)"
done

# Clean up backups older than 30 days
find "$BACKUP_DIR" -name "*.sql.gz" -mtime +30 -delete 2>/dev/null || true

log "Backup complete."
