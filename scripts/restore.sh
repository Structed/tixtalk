#!/bin/bash
# Restore a database from a backup file.
# Usage: ./scripts/restore.sh <backup-file> <database>
# Example: ./scripts/restore.sh backups/pretix_20260324-030000.sql.gz pretix
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "$SCRIPT_DIR/lib/common.sh"

init_project_dir
cd "$PROJECT_DIR"

if [ $# -lt 2 ]; then
    echo "Usage: $0 <backup-file.sql.gz> <database>"
    echo "  database: pretix or pretalx"
    echo ""
    echo "Available backups:"
    ls -lh backups/*.sql.gz 2>/dev/null || echo "  (none)"
    exit 1
fi

BACKUP_FILE="$1"
DATABASE="$2"

if [ ! -f "$BACKUP_FILE" ]; then
    echo "ERROR: Backup file not found: $BACKUP_FILE"
    exit 1
fi

if [ "$DATABASE" != "pretix" ] && [ "$DATABASE" != "pretalx" ]; then
    echo "ERROR: Database must be 'pretix' or 'pretalx'"
    exit 1
fi

# Load .env for DB credentials
load_env

echo "WARNING: This will overwrite the '$DATABASE' database with the backup."
read -p "Are you sure? (y/N) " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Cancelled."
    exit 0
fi

echo "Stopping app containers..."
docker compose stop pretix pretalx

echo "Restoring $DATABASE from $BACKUP_FILE..."
gunzip -c "$BACKUP_FILE" | docker compose exec -T postgres psql -U "$DB_USER" -d "$DATABASE" --quiet

echo "Restarting app containers..."
docker compose start pretix pretalx

echo "=== Restore complete ==="
