#!/bin/bash
# Update containers to the latest images.
# Usage: ./scripts/update.sh [--pretix TAG] [--pretalx TAG]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
cd "$PROJECT_DIR"

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
COMPOSE_CMD="docker compose"
if [ "${CLOUDFLARE_DNS_CHALLENGE:-false}" = "true" ]; then
    COMPOSE_CMD="docker compose -f docker-compose.yml -f docker-compose.cloudflare.yml"
fi
$COMPOSE_CMD pull --ignore-buildable 2>/dev/null || $COMPOSE_CMD pull 2>/dev/null || true

echo "Restarting services..."
$COMPOSE_CMD up -d --build

echo ""
echo "=== Update complete ==="
docker compose ps --format "table {{.Name}}\t{{.Image}}\t{{.Status}}"
