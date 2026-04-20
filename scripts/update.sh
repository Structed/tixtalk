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

# Determine the project owner (for installing cron jobs under the right user)
PROJECT_OWNER="$(stat -c '%U' "$PROJECT_DIR")"

# Migrate cron jobs from root to project owner (fixes cloud-init installing as root)
if sudo crontab -l 2>/dev/null | grep -qE "# tixtalk-(cron|backup)|tixtalk-(cron|backup)\.log"; then
    echo ""
    echo "Migrating cron jobs from root to $PROJECT_OWNER..."
    ( sudo crontab -l 2>/dev/null | grep -vE "# tixtalk-(cron|backup)|tixtalk-(cron|backup)\.log" || true ) | sudo crontab -
    echo "Cron jobs removed from root's crontab."
fi

# Fix backup directory ownership if it was created by root
if [ -d "$PROJECT_DIR/backups" ] && [ "$(stat -c '%U' "$PROJECT_DIR/backups")" = "root" ]; then
    echo "Fixing backup directory permissions..."
    sudo chown -R "$PROJECT_OWNER:$(stat -c '%G' "$PROJECT_DIR")" "$PROJECT_DIR/backups"
    echo "Backup directory ownership fixed recursively."
fi

# Install cron jobs as the project owner (not root)
install_cron_as_owner() {
    if [ "$(id -un)" = "$PROJECT_OWNER" ]; then
        "$@"
    else
        sudo -u "$PROJECT_OWNER" "$@"
    fi
}

# Ensure cron jobs are installed and up-to-date (--install de-duplicates existing entries)
echo ""
echo "Ensuring cron jobs are up-to-date..."
install_cron_as_owner bash "$SCRIPT_DIR/cron.sh" --install
install_cron_as_owner bash "$SCRIPT_DIR/backup.sh" --install-cron

echo ""
echo "=== Update complete ==="
$COMPOSE_CMD ps --format "table {{.Name}}\t{{.Image}}\t{{.Status}}"
