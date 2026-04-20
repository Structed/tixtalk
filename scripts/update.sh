#!/bin/bash
# Update containers to the latest images.
# Usage: ./scripts/update.sh [--pretix TAG] [--pretalx TAG]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "$SCRIPT_DIR/lib/common.sh"

init_project_dir
cd "$PROJECT_DIR"

# Load .env for configuration
load_env || true

# Parse optional tag overrides
while [[ $# -gt 0 ]]; do
    case $1 in
        --pretix)
            sed -i "s/^PRETIX_IMAGE_TAG=.*/PRETIX_IMAGE_TAG=$2/" .env
            echo "Set PRETIX_IMAGE_TAG=$2"
            shift 2
            ;;
        --pretalx)
            sed -i "s/^PRETALX_IMAGE_TAG=.*/PRETALX_IMAGE_TAG=$2/" .env
            echo "Set PRETALX_IMAGE_TAG=$2"
            shift 2
            ;;
        *)
            echo "Usage: $0 [--pretix TAG] [--pretalx TAG]"
            exit 1
            ;;
    esac
done

echo "Pulling latest images..."
COMPOSE_CMD=$(compose_cmd)
$COMPOSE_CMD pull --ignore-buildable 2>/dev/null || $COMPOSE_CMD pull 2>/dev/null || true

echo "Restarting services..."
$COMPOSE_CMD up -d --build

# Migrate cron jobs from root to current user (fixes cloud-init installing as root)
if [ "$(id -u)" -ne 0 ] && sudo crontab -l 2>/dev/null | grep -q "tixtalk"; then
    echo ""
    echo "Migrating cron jobs from root to $(id -un)..."
    # Remove tixtalk entries from root's crontab
    ( sudo crontab -l 2>/dev/null | grep -v "tixtalk" || true ) | sudo crontab -
    echo "Cron jobs removed from root's crontab."
fi

# Fix backup directory ownership if it was created by root
if [ -d "$PROJECT_DIR/backups" ] && [ ! -w "$PROJECT_DIR/backups" ]; then
    echo "Fixing backup directory permissions..."
    sudo chown "$(id -u):$(id -g)" "$PROJECT_DIR/backups"
    echo "Backup directory ownership fixed."
fi

# Auto-install periodic task cron if not already present
if ! crontab -l 2>/dev/null | grep -q "scripts/cron.sh"; then
    echo ""
    echo "Installing periodic task cron job (runs every 5 minutes)..."
    bash "$SCRIPT_DIR/cron.sh" --install
fi

# Auto-install backup cron if not already present
if ! crontab -l 2>/dev/null | grep -q "scripts/backup.sh"; then
    echo ""
    echo "Installing backup cron job (daily 3 AM)..."
    bash "$SCRIPT_DIR/backup.sh" --install-cron
fi

echo ""
echo "=== Update complete ==="
docker compose ps --format "table {{.Name}}\t{{.Image}}\t{{.Status}}"
