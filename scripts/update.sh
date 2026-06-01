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

# Backfill TICKETS_HOST / TALKS_HOST if missing or empty (added in v2.x for subdomain prefix support)
if [ -f .env ]; then
    if ! grep -q '^TICKETS_HOST=.\+' .env 2>/dev/null; then
        if [ -z "${DOMAIN:-}" ] || [ "$DOMAIN" = "localhost" ]; then
            echo "WARNING: DOMAIN is not set or is 'localhost' — skipping TICKETS_HOST backfill"
        else
            TICKETS_HOST="${SUBDOMAIN_PREFIX:-}tickets.${DOMAIN}"
            if grep -q '^TICKETS_HOST=' .env 2>/dev/null; then
                sed -i "s|^TICKETS_HOST=.*|TICKETS_HOST=${TICKETS_HOST}|" .env
            else
                echo "" >> .env
                echo "TICKETS_HOST=${TICKETS_HOST}" >> .env
            fi
            echo "Backfilled TICKETS_HOST=${TICKETS_HOST} into .env"
        fi
    fi
    if ! grep -q '^TALKS_HOST=.\+' .env 2>/dev/null; then
        if [ -z "${DOMAIN:-}" ] || [ "$DOMAIN" = "localhost" ]; then
            echo "WARNING: DOMAIN is not set or is 'localhost' — skipping TALKS_HOST backfill"
        else
            TALKS_HOST="${SUBDOMAIN_PREFIX:-}talks.${DOMAIN}"
            if grep -q '^TALKS_HOST=' .env 2>/dev/null; then
                sed -i "s|^TALKS_HOST=.*|TALKS_HOST=${TALKS_HOST}|" .env
            else
                echo "" >> .env
                echo "TALKS_HOST=${TALKS_HOST}" >> .env
            fi
            echo "Backfilled TALKS_HOST=${TALKS_HOST} into .env"
        fi
    fi
    # Backfill ENVIRONMENT if missing or empty (introduced in v2.x; all pre-existing stacks are prod)
    if ! grep -q '^ENVIRONMENT=.\+' .env 2>/dev/null; then
        ENVIRONMENT="prod"
        if grep -q '^ENVIRONMENT=' .env 2>/dev/null; then
            sed -i "s|^ENVIRONMENT=.*|ENVIRONMENT=${ENVIRONMENT}|" .env
        else
            echo "" >> .env
            echo "ENVIRONMENT=${ENVIRONMENT}" >> .env
        fi
        echo "Backfilled ENVIRONMENT=${ENVIRONMENT} into .env"
    fi
fi

# Validate ENVIRONMENT (catch typos early — refuse to continue with bad values)
if [ -n "${ENVIRONMENT:-}" ] && [ "$ENVIRONMENT" != "prod" ] && [ "$ENVIRONMENT" != "dev" ]; then
    echo "ERROR: ENVIRONMENT='${ENVIRONMENT}' is not a recognized value (expected 'prod' or 'dev')."
    echo "  Fix the value in .env before running update."
    exit 1
fi

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
$COMPOSE_CMD pull 2>/dev/null || true

echo "Restarting services..."
$COMPOSE_CMD up -d

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

# Only install backup cron for production (dev environments skip daily backups)
if [ "${ENVIRONMENT:-prod}" = "prod" ]; then
    install_cron_as_owner bash "$SCRIPT_DIR/backup.sh" --install-cron
else
    echo "Skipping backup cron (${ENVIRONMENT} environment)"
    # Remove any existing backup cron from the project owner's crontab
    if install_cron_as_owner crontab -l 2>/dev/null | grep -qE "# tixtalk-backup|tixtalk-backup\.log"; then
        echo "Removing stale backup cron entry..."
        if [ "$(id -un)" = "$PROJECT_OWNER" ]; then
            ( crontab -l 2>/dev/null | grep -v "# tixtalk-backup" | grep -v "tixtalk-backup\.log" || true ) | crontab -
        else
            sudo -u "$PROJECT_OWNER" bash -c '( crontab -l 2>/dev/null | grep -v "# tixtalk-backup" | grep -v "tixtalk-backup\.log" || true ) | crontab -'
        fi
    fi
fi

echo ""
echo "=== Update complete ==="
$COMPOSE_CMD ps --format "table {{.Name}}\t{{.Image}}\t{{.Status}}"
