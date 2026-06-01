#!/bin/bash
# Initial server setup for Ubuntu/Debian.
# Run as root or with sudo on a fresh Hetzner VPS.
set -euo pipefail

echo "=== Server Setup for Pretix + Pretalx ==="

# Update system
echo "Updating system packages..."
apt-get update -qq && apt-get upgrade -y -qq

# Install Docker if not present
if ! command -v docker &> /dev/null; then
    echo "Installing Docker..."
    curl -fsSL https://get.docker.com | sh
    systemctl enable docker
    systemctl start docker
    echo "Docker installed."
else
    echo "Docker already installed."
fi

# Install docker compose plugin if not present
if ! docker compose version &> /dev/null; then
    echo "Installing Docker Compose plugin..."
    apt-get install -y -qq docker-compose-plugin
    echo "Docker Compose installed."
else
    echo "Docker Compose already installed."
fi

# Configure firewall (ufw)
if command -v ufw &> /dev/null; then
    echo "Configuring firewall..."
    ufw allow 22/tcp   # SSH
    ufw allow 80/tcp   # HTTP (for Let's Encrypt)
    ufw allow 443/tcp  # HTTPS
    ufw allow 443/udp  # HTTP/3
    ufw --force enable
    echo "Firewall configured."
fi

# Configure swap (2 GB) if no swap is active
if [ "$(swapon --show --noheadings | wc -l)" -eq 0 ]; then
    echo "Configuring 2 GB swap..."
    if [ -f /swapfile ]; then
        echo "Reusing existing /swapfile..."
    else
        fallocate -l 2G /swapfile
        chmod 600 /swapfile
        mkswap /swapfile
    fi
    swapon /swapfile
    if ! grep -q '/swapfile' /etc/fstab; then
        echo '/swapfile none swap sw 0 0' >> /etc/fstab
    fi
    echo "Swap configured."
else
    echo "Swap already active — skipping."
fi

# Enable automatic security updates
if ! dpkg -l | grep -q unattended-upgrades; then
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq unattended-upgrades
    DEBIAN_FRONTEND=noninteractive dpkg-reconfigure -plow unattended-upgrades
fi

echo ""
echo "=== Setup complete ==="
echo "Next steps:"
echo "  1. Point DNS records to this server's IP:"
echo "       tickets.yourdomain.com → $(curl -s ifconfig.me 2>/dev/null || echo '<server-ip>')"
echo "       talks.yourdomain.com   → $(curl -s ifconfig.me 2>/dev/null || echo '<server-ip>')"
echo "  2. Edit .env with your domain and SMTP settings"
echo "  3. Run: ./scripts/deploy.sh"
