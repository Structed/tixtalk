#!/bin/bash
# Create or update Cloudflare DNS A records for tickets.DOMAIN and talks.DOMAIN.
# Requires CLOUDFLARE_API_TOKEN and CLOUDFLARE_ZONE_ID in .env.
# Idempotent: creates records if they don't exist, updates if they do.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "$SCRIPT_DIR/lib/common.sh"

init_project_dir
cd "$PROJECT_DIR"

# Load .env
load_env

if [ -z "${CLOUDFLARE_API_TOKEN:-}" ]; then
    echo "CLOUDFLARE_API_TOKEN not set in .env — skipping DNS setup."
    echo "Set up DNS records manually instead."
    exit 0
fi

if [ -z "${CLOUDFLARE_ZONE_ID:-}" ]; then
    echo "ERROR: CLOUDFLARE_ZONE_ID is required when CLOUDFLARE_API_TOKEN is set."
    exit 1
fi

# Detect server's public IP
SERVER_IP=$(curl -s -4 ifconfig.me)
if [ -z "$SERVER_IP" ]; then
    echo "ERROR: Could not detect server's public IP."
    exit 1
fi

CF_API="https://api.cloudflare.com/client/v4"
AUTH_HEADER="Authorization: Bearer ${CLOUDFLARE_API_TOKEN}"

# Determine proxy mode based on DNS challenge setting
if [ "${CLOUDFLARE_DNS_CHALLENGE:-false}" = "true" ]; then
    PROXIED=true
else
    PROXIED=false
fi

# Create or update a DNS A record
upsert_record() {
    local name="$1"
    local ip="$2"
    local proxied="$3"

    echo -n "  ${name} → ${ip} (proxied: ${proxied})... "

    # Check if record exists
    local response
    response=$(curl -s -X GET \
        "${CF_API}/zones/${CLOUDFLARE_ZONE_ID}/dns_records?type=A&name=${name}" \
        -H "$AUTH_HEADER" \
        -H "Content-Type: application/json")

    local count
    count=$(echo "$response" | grep -o '"count":[0-9]*' | head -1 | cut -d: -f2)

    if [ "${count:-0}" -gt 0 ]; then
        # Update existing record
        local record_id
        record_id=$(echo "$response" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
        curl -s -X PUT \
            "${CF_API}/zones/${CLOUDFLARE_ZONE_ID}/dns_records/${record_id}" \
            -H "$AUTH_HEADER" \
            -H "Content-Type: application/json" \
            --data "{\"type\":\"A\",\"name\":\"${name}\",\"content\":\"${ip}\",\"ttl\":1,\"proxied\":${proxied},\"comment\":\"Managed by tixtalk\"}" \
            > /dev/null
        echo "updated"
    else
        # Create new record
        curl -s -X POST \
            "${CF_API}/zones/${CLOUDFLARE_ZONE_ID}/dns_records" \
            -H "$AUTH_HEADER" \
            -H "Content-Type: application/json" \
            --data "{\"type\":\"A\",\"name\":\"${name}\",\"content\":\"${ip}\",\"ttl\":1,\"proxied\":${proxied},\"comment\":\"Managed by tixtalk\"}" \
            > /dev/null
        echo "created"
    fi
}

# Resolve hostnames (support subdomain prefix)
TICKETS_HOST="${TICKETS_HOST:-${SUBDOMAIN_PREFIX:-}tickets.${DOMAIN}}"
TALKS_HOST="${TALKS_HOST:-${SUBDOMAIN_PREFIX:-}talks.${DOMAIN}}"

echo "Setting up Cloudflare DNS records (server IP: ${SERVER_IP})..."
upsert_record "${TICKETS_HOST}" "$SERVER_IP" "$PROXIED"
upsert_record "${TALKS_HOST}" "$SERVER_IP" "$PROXIED"
echo "DNS records configured."

# When using DNS challenge (proxied=true), set SSL mode to "full" so Cloudflare connects via HTTPS
if [ "${CLOUDFLARE_DNS_CHALLENGE:-false}" = "true" ]; then
    echo "Setting Cloudflare SSL mode to 'full'..."
    curl -s -X PATCH \
        "${CF_API}/zones/${CLOUDFLARE_ZONE_ID}/settings/ssl" \
        -H "$AUTH_HEADER" \
        -H "Content-Type: application/json" \
        --data '{"value":"full"}' \
        > /dev/null
    echo "SSL mode set to 'full'."
fi
