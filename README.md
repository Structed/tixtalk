# Pretix + Pretalx on a VPS

Self-hosted [pretix](https://pretix.eu/) (ticketing) and [pretalx](https://pretalx.com/) (call for papers & scheduling) via docker-compose. Optimized for a single cheap VPS (e.g., Hetzner CX33 at ~€5.49/mo).

## What You Get

| Service | Image | Purpose |
|---------|-------|---------|
| **Caddy** | `caddy:2-alpine` | Reverse proxy + automatic Let's Encrypt TLS |
| **PostgreSQL** | `postgres:16-alpine` | Shared database (pretix + pretalx DBs) |
| **Redis** | `redis:7-alpine` | Shared cache + Celery task queue |
| **Pretix** | `pretix/standalone` | Ticketing at `tickets.yourdomain.com` |
| **Pretalx** | `pretalx/standalone` | CfP/scheduling at `talks.yourdomain.com` |

Estimated cost: **~€5-7/month** (Hetzner CX33: 4 vCPU, 8 GB RAM, 80 GB SSD).

## Management CLI

Everything is managed through a single `./manage.sh` script:

```bash
./manage.sh              # Interactive menu
./manage.sh status       # Service status + URLs
./manage.sh update       # Pull latest images + restart
./manage.sh logs pretix  # Tail pretix logs
./manage.sh backup       # Backup databases
./manage.sh help         # All commands
```

**Remote management** from your home PC:
```bash
./manage.sh remote root@your-server-ip          # Interactive menu over SSH
./manage.sh remote root@your-server-ip status   # Direct command over SSH
```

## Prerequisites

- A VPS running Ubuntu 22.04+ or Debian 12+ (2+ GB RAM minimum, 4+ GB recommended)
- A domain with DNS access
- SSH access to the server

## Quick Start

### 1. Set up DNS

**Option A — Cloudflare (automated):** Add your Cloudflare API token and Zone ID to `.env` and `deploy.sh` will create the DNS records automatically.

**Option B — Manual:** Point two A records to your server's IP address:

```
tickets.yourdomain.com → <server-ip>
talks.yourdomain.com   → <server-ip>
```

### 2. Clone and configure

SSH into your server, then:

```bash
git clone <this-repo>
cd pre-talx-tix-azure

# Install Docker if needed
./manage.sh setup

# Create your configuration
cp .env.example .env
nano .env  # Set DOMAIN, Cloudflare (optional), SMTP settings
```

### 3. Deploy

```bash
./manage.sh deploy
```

This will:
- Generate secure random passwords for the database and app secret keys
- Create Cloudflare DNS records (if API token is set)
- Pull all container images
- Start everything

### 4. Initialize (first time only)

Wait ~30 seconds for services to start, then:

```bash
docker compose exec pretix pretix migrate
docker compose exec pretix pretix rebuild
docker compose exec pretalx pretalx migrate
docker compose exec pretalx pretalx rebuild
```

### 5. Access your apps

- **Pretix**: `https://tickets.yourdomain.com`
- **Pretalx**: `https://talks.yourdomain.com`

Both apps have web-based setup wizards on first visit.

## Updating Containers

```bash
# Pull latest images and restart
./manage.sh update

# Pin a specific version
./manage.sh update --pretix 2025.1.0

# Update both
./manage.sh update --pretix 2025.1.0 --pretalx 2025.1.0
```

## Backups

### Manual backup

```bash
./manage.sh backup
```

Saves gzipped SQL dumps to `backups/` with timestamps.

### Automatic daily backups

```bash
./manage.sh backup --install-cron
```

Runs at 3:00 AM daily. Backups older than 30 days are auto-deleted.

### Restore from backup

```bash
# Interactive (lists available backups):
./manage.sh restore

# Direct:
./manage.sh restore backups/pretix_20260324-030000.sql.gz pretix
```

## Yearly Events

Both apps are multi-tenant — create new events in the web UI each year. No infrastructure changes needed:

- **Pretix**: `https://tickets.yourdomain.com/<organizer>/<year>/`
- **Pretalx**: `https://talks.yourdomain.com/<event-slug>/`

## Cloudflare Integration

### Setup

1. Go to [Cloudflare API Tokens](https://dash.cloudflare.com/profile/api-tokens)
2. Create a token with **Zone > DNS > Edit** permission for your domain
3. Copy your **Zone ID** from the domain's Overview page in Cloudflare
4. Add both to `.env`:

```bash
CLOUDFLARE_API_TOKEN=your-token-here
CLOUDFLARE_ZONE_ID=your-zone-id-here
```

### TLS modes

| Mode | `CLOUDFLARE_DNS_CHALLENGE` | Cloudflare proxy | How it works |
|------|---------------------------|-----------------|--------------|
| **HTTP challenge** (default) | `false` | Off (grey cloud) | Caddy validates via port 80. Simpler, standard image. |
| **DNS challenge** | `true` | On (orange cloud) | Caddy validates via Cloudflare API. Hides server IP, full CDN. |

To use DNS challenge:
```bash
CLOUDFLARE_DNS_CHALLENGE=true
```

This builds a custom Caddy image with the Cloudflare plugin (first deploy takes ~1 min longer).

### Manual DNS management

You can also run `scripts/cloudflare-dns.sh` independently to create/update DNS records without redeploying.

## Configuration Reference

All configuration is in `.env`:

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `DOMAIN` | Yes | — | Your domain (e.g., `yourdomain.com`) |
| `CLOUDFLARE_API_TOKEN` | No | — | Cloudflare API token (automates DNS) |
| `CLOUDFLARE_ZONE_ID` | No | — | Cloudflare Zone ID |
| `CLOUDFLARE_DNS_CHALLENGE` | No | `false` | Use DNS challenge for TLS (enables proxy) |
| `DB_USER` | No | `pretalxtix` | PostgreSQL username |
| `DB_PASSWORD` | No | auto-generated | PostgreSQL password |
| `PRETIX_SECRET_KEY` | No | auto-generated | Pretix secret key |
| `PRETALX_SECRET_KEY` | No | auto-generated | Pretalx secret key |
| `PRETIX_IMAGE_TAG` | No | `stable` | Pretix Docker image tag |
| `PRETALX_IMAGE_TAG` | No | `latest` | Pretalx Docker image tag |
| `MAIL_FROM` | Yes | — | Email sender address |
| `SMTP_HOST` | Yes | — | SMTP server hostname |
| `SMTP_PORT` | No | `587` | SMTP server port |
| `SMTP_USER` | Yes | — | SMTP username |
| `SMTP_PASSWORD` | Yes | — | SMTP password |

## Troubleshooting

### Check service status
```bash
docker compose ps
docker compose logs pretix --tail 50
docker compose logs pretalx --tail 50
```

### TLS certificate not working
Caddy auto-provisions Let's Encrypt certs. Ensure:
- DNS A records are pointing to the server
- Ports 80 and 443 are open (`sudo ufw status`)
- Check Caddy logs: `docker compose logs caddy`

### Database connection issues
```bash
docker compose exec postgres psql -U pretalxtix -l
```

### Restart everything
```bash
docker compose down && docker compose up -d
```

## Destroying Everything

```bash
docker compose down -v  # -v removes all data volumes
```
