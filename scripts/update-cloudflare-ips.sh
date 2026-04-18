#!/bin/bash
# Update Cloudflare IP ranges in Caddyfile.dns
#
# Cloudflare periodically updates their IP ranges. Run this script to fetch
# the latest ranges and update the trusted_proxies directive in Caddyfile.dns.
#
# Source: https://www.cloudflare.com/ips/
#
# Usage: ./scripts/update-cloudflare-ips.sh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
CADDYFILE="$PROJECT_DIR/caddy/Caddyfile.dns"

if [[ ! -f "$CADDYFILE" ]]; then
    echo "ERROR: Caddyfile.dns not found at $CADDYFILE"
    exit 1
fi

echo "Fetching current Cloudflare IP ranges..."

# Fetch IPv4 and IPv6 ranges
IPV4=$(curl -s https://www.cloudflare.com/ips-v4 | tr '\n' ' ')
IPV6=$(curl -s https://www.cloudflare.com/ips-v6 | tr '\n' ' ')

if [[ -z "$IPV4" ]]; then
    echo "ERROR: Failed to fetch Cloudflare IPv4 ranges"
    exit 1
fi

# Combine all ranges (space-separated for Caddy)
ALL_IPS="$IPV4 $IPV6"
# Trim trailing whitespace
ALL_IPS=$(echo "$ALL_IPS" | xargs)

echo "Found IP ranges:"
echo "  IPv4: $(echo "$IPV4" | wc -w) ranges"
echo "  IPv6: $(echo "$IPV6" | wc -w) ranges"

# Create the new trusted_proxies line
# Note: This is a simplified approach - for complex Caddyfiles, consider using sed more carefully
CURRENT=$(grep -oP 'trusted_proxies \K[^\n]+' "$CADDYFILE" | head -1 || echo "")

if [[ -z "$CURRENT" ]]; then
    echo "WARNING: Could not find existing trusted_proxies in Caddyfile.dns"
    echo "Manual update required. Add this to your reverse_proxy blocks:"
    echo "  trusted_proxies $ALL_IPS"
    exit 0
fi

echo ""
echo "Current trusted_proxies:"
echo "  $(echo "$CURRENT" | cut -c1-60)..."
echo ""
echo "New trusted_proxies:"
echo "  $(echo "$ALL_IPS" | cut -c1-60)..."

# Check if update is needed
if [[ "$CURRENT" == "$ALL_IPS" ]]; then
    echo ""
    echo "No changes needed - IP ranges are already up to date."
    exit 0
fi

# Create backup
cp "$CADDYFILE" "${CADDYFILE}.bak"
echo ""
echo "Backup created: ${CADDYFILE}.bak"

# Update the file - replace all occurrences of trusted_proxies
# Use sed with a more specific pattern to only match the IP lists after trusted_proxies
sed -i "s|trusted_proxies $CURRENT|trusted_proxies $ALL_IPS|g" "$CADDYFILE"

echo "Updated $CADDYFILE"
echo ""
echo "To apply changes:"
echo "  docker compose restart caddy"
echo ""
echo "Or if using DNS challenge mode:"
echo "  docker compose -f docker-compose.yml -f docker-compose.cloudflare.yml up -d --build caddy"
