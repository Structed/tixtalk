#!/bin/bash
# First-time deployment of Pretix + Pretalx.
# Generates secrets, validates config, sets up DNS, and starts all services.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "$SCRIPT_DIR/lib/common.sh"

init_project_dir
cd "$PROJECT_DIR"

echo "=== Deploying Pretix + Pretalx ==="

# Create .env from template if it doesn't exist
if [ ! -f .env ]; then
    echo "Creating .env from .env.example..."
    cp .env.example .env
    echo "Please edit .env with your domain and SMTP settings, then re-run this script."
    exit 1
fi

# Load .env to check values
load_env

# Validate required config
if [ -z "${DOMAIN:-}" ] || [ "$DOMAIN" = "yourdomain.com" ] || [ "$DOMAIN" = "localhost" ]; then
    echo "ERROR: Set DOMAIN in .env to your actual domain (not 'localhost' or 'yourdomain.com')."
    exit 1
fi

# Compute TICKETS_HOST / TALKS_HOST if not set (backward compat)
if [ -z "${TICKETS_HOST:-}" ]; then
    TICKETS_HOST="${SUBDOMAIN_PREFIX:-}tickets.${DOMAIN}"
    # Update existing blank entry or append
    if grep -q '^TICKETS_HOST=' .env 2>/dev/null; then
        sed -i "s|^TICKETS_HOST=.*|TICKETS_HOST=${TICKETS_HOST}|" .env
    else
        echo "TICKETS_HOST=${TICKETS_HOST}" >> .env
    fi
    echo "Computed TICKETS_HOST=${TICKETS_HOST}"
fi
if [ -z "${TALKS_HOST:-}" ]; then
    TALKS_HOST="${SUBDOMAIN_PREFIX:-}talks.${DOMAIN}"
    # Update existing blank entry or append
    if grep -q '^TALKS_HOST=' .env 2>/dev/null; then
        sed -i "s|^TALKS_HOST=.*|TALKS_HOST=${TALKS_HOST}|" .env
    else
        echo "TALKS_HOST=${TALKS_HOST}" >> .env
    fi
    echo "Computed TALKS_HOST=${TALKS_HOST}"
fi

# Generate secrets if empty
CHANGED=false
if [ -z "${DB_PASSWORD:-}" ]; then
    sed -i "s/^DB_PASSWORD=$/DB_PASSWORD=$(generate_secret 32)/" .env
    CHANGED=true
    echo "Generated DB_PASSWORD"
fi
if [ -z "${PRETIX_SECRET_KEY:-}" ]; then
    sed -i "s/^PRETIX_SECRET_KEY=$/PRETIX_SECRET_KEY=$(generate_secret 50)/" .env
    CHANGED=true
    echo "Generated PRETIX_SECRET_KEY"
fi
if [ -z "${PRETALX_SECRET_KEY:-}" ]; then
    sed -i "s/^PRETALX_SECRET_KEY=$/PRETALX_SECRET_KEY=$(generate_secret 50)/" .env
    CHANGED=true
    echo "Generated PRETALX_SECRET_KEY"
fi

if [ "$CHANGED" = true ]; then
    echo "Secrets written to .env — keep this file safe!"
    echo ""
fi

# Set up Cloudflare DNS records if API token is configured
if [ -n "${CLOUDFLARE_API_TOKEN:-}" ]; then
    echo "Setting up Cloudflare DNS..."
    bash "$SCRIPT_DIR/cloudflare-dns.sh"
    echo ""
else
    echo "No CLOUDFLARE_API_TOKEN set — make sure DNS records exist:"
    echo "  ${TICKETS_HOST} → $(curl -s -4 ifconfig.me 2>/dev/null || echo '<server-ip>')"
    echo "  ${TALKS_HOST}   → $(curl -s -4 ifconfig.me 2>/dev/null || echo '<server-ip>')"
    echo ""
fi

# Get compose command based on TLS mode
COMPOSE_CMD=$(compose_cmd)
if [ "${CLOUDFLARE_DNS_CHALLENGE:-false}" = "true" ]; then
    echo "Using Cloudflare proxy mode (internal TLS)..."
fi

# Pull images and start
echo "Pulling container images..."
$COMPOSE_CMD pull --quiet 2>/dev/null || true

echo "Starting services..."
$COMPOSE_CMD up -d

echo ""
echo "=== Deployment complete ==="
echo ""
echo "  Pretix:  https://${TICKETS_HOST}"
echo "  Pretalx: https://${TALKS_HOST}"
echo ""
echo "First-time setup:"
echo "  1. Wait ~30s for services to initialize"
echo "  2. Run migrations:  docker compose exec pretix pretix migrate"
echo "  3. Rebuild assets:  docker compose exec pretix pretix rebuild"
echo "  4. Run migrations:  docker compose exec pretalx pretalx migrate"
echo "  5. Rebuild assets:  docker compose exec pretalx pretalx rebuild"
echo ""
echo "Set up periodic tasks (required — sends emails, processes payments, etc.):"
echo "  ./scripts/cron.sh --install"
echo ""
echo "Set up daily backups:"
echo "  ./scripts/backup.sh --install-cron"
