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

echo ""
echo "=== Update complete ==="
docker compose ps --format "table {{.Name}}\t{{.Image}}\t{{.Status}}"
