# Pretix + Pretalx on Azure

Self-hosted [pretix](https://pretix.eu/) (ticketing) and [pretalx](https://pretalx.com/) (call for papers & scheduling) on an Azure VM. One command provisions the infrastructure, installs Docker, deploys all services, runs migrations, and sets up daily backups.

```bash
tixtalk provision    # Interactive wizard → fully running deployment
```

## What You Get

| Service | Image | Purpose |
|---------|-------|---------|
| **Caddy** | `caddy:2-alpine` | Reverse proxy + automatic Let's Encrypt TLS |
| **PostgreSQL** | `postgres:16-alpine` | Shared database (pretix + pretalx DBs) |
| **Redis** | `redis:7-alpine` | Shared cache + Celery task queue |
| **Pretix** | `pretix/standalone` | Ticketing (default: `tickets.<DOMAIN>`) |
| **Pretalx** | `pretalx/standalone` | CfP/scheduling (default: `talks.<DOMAIN>`) |

## Architecture

```
pulumi up
  └─► Azure VM (Ubuntu 24.04 LTS, Standard_B2s)
        ├── VNet + NSG (SSH, HTTP, HTTPS, HTTP/3)
        ├── Static Public IP
        └── cloud-init on first boot:
              ├── Installs Docker
              ├── Clones this repo
              ├── Writes .env (secrets auto-generated)
              └── docker compose up -d
                    ├── Caddy (reverse proxy, auto TLS)
                    ├── PostgreSQL 16
                    ├── Redis 7
                    ├── Pretix
                    └── Pretalx
              Then automatically:
                    ├── Runs database migrations
                    └── Installs daily backup cron
```

Estimated cost: **~$30/month** (Azure Standard_B2s: 2 vCPU, 4 GB RAM, 64 GB SSD).

## Prerequisites

- [.NET 8+ SDK](https://dotnet.microsoft.com/download)
- [Pulumi CLI](https://www.pulumi.com/docs/install/)
- An Azure subscription (`az login` or [Pulumi service account](https://www.pulumi.com/docs/clouds/azure/get-started/))
- A domain with DNS access

> **Already have a VPS?** Skip Pulumi and jump to [Alternative: Manual VPS Deployment](#alternative-manual-vps-deployment).

## Quick Start

### One-command setup (recommended)

```bash
# Install the CLI (requires .NET 8+ SDK)
cd cli && dotnet run -- provision
```

The interactive wizard asks for your domain, SSH key, and Azure region, then:
1. Configures Pulumi stack settings
2. Provisions the Azure VM and networking (`pulumi up`)
3. VM cloud-init installs Docker, clones repo, starts services, runs migrations, sets up periodic tasks, and configures daily backups
4. Configures the `tixtalk` CLI to connect to the new server

After ~5 minutes your apps are live:
- **Pretix**: `https://<TICKETS_HOST>` (default: `tickets.<DOMAIN>`)
- **Pretalx**: `https://<TALKS_HOST>` (default: `talks.<DOMAIN>`)

### Manual Pulumi setup (advanced)

If you prefer to run Pulumi directly:

```bash
cd infra

# Initialize a Pulumi stack (first time only)
pulumi stack init dev

# Required config
pulumi config set tixtalk:domain yourdomain.com
pulumi config set tixtalk:sshPublicKey "$(cat ~/.ssh/id_rsa.pub)"

# Optional — email (required for ticket confirmations & CfP notifications)
pulumi config set tixtalk:smtpHost smtp.example.com
pulumi config set tixtalk:smtpUser user@example.com
pulumi config set tixtalk:smtpPassword YOUR_PASSWORD --secret
pulumi config set tixtalk:mailFrom noreply@yourdomain.com

# Optional — Cloudflare DNS automation
pulumi config set tixtalk:cloudflareApiToken YOUR_TOKEN --secret
pulumi config set tixtalk:cloudflareZoneId YOUR_ZONE_ID
```

Then deploy, set up DNS, and access your apps:

```bash
pulumi up
```

Cloud-init runs on first boot (~5 min): installs Docker, starts services, runs migrations, sets up periodic tasks, and configures daily backups.

Point DNS to the VM IP from `pulumi stack output vmPublicIp`:

```
<TICKETS_HOST> → <vmPublicIp>
<TALKS_HOST>   → <vmPublicIp>
```

If you configured Cloudflare, DNS records are created automatically.

- **Pretix**: `https://<TICKETS_HOST>`
- **Pretalx**: `https://<TALKS_HOST>`

Both apps have web-based setup wizards on first visit.

### Pulumi Outputs

After deployment, `pulumi stack output` displays:

| Output | Description |
|--------|-------------|
| `vmPublicIp` | VM public IP address |
| `sshCommand` | Ready-to-use SSH command |
| `pretixUrl` | `https://<TICKETS_HOST>` |
| `pretalxUrl` | `https://<TALKS_HOST>` |

## Configuration Reference

All configuration is managed via Pulumi config (`pulumi config set <key> <value>`):

| Pulumi Config Key | Required | Default | `.env` Equivalent | Description |
|-------------------|----------|---------|-------------------|-------------|
| `tixtalk:domain` | Yes | — | `DOMAIN` | Your domain (e.g., `yourdomain.com`) |
| `tixtalk:sshPublicKey` | Yes | — | — | SSH public key for VM access |
| `tixtalk:prefix` | No | `tixtalk` | — | Azure resource name prefix |
| `tixtalk:vmSize` | No | `Standard_B2s` | — | Azure VM SKU |
| `tixtalk:sshAllowedCidrs` | No | `["*"]` | — | JSON array of CIDR ranges allowed for SSH (see [Security](#security)) |
| `azure-native:location` | No | `westeurope` | — | Azure region |
| `tixtalk:cloudflareApiToken` | No | — | `CLOUDFLARE_API_TOKEN` | Cloudflare API token (use `--secret`) |
| `tixtalk:cloudflareZoneId` | No | — | `CLOUDFLARE_ZONE_ID` | Cloudflare Zone ID |
| `tixtalk:cloudflareDnsChallenge` | No | `true` | `CLOUDFLARE_DNS_CHALLENGE` | Use DNS challenge for TLS |
| `tixtalk:useAzureMail` | No | `true` | — | Use Azure Communication Services for email |
| `tixtalk:acsUseCustomDomain` | No | `false` | — | Use custom domain for ACS (requires Cloudflare) |
| `tixtalk:mailFrom` | No | `noreply@example.com` | `MAIL_FROM` | Email sender address |
| `tixtalk:smtpHost` | No | — | `SMTP_HOST` | SMTP server hostname |
| `tixtalk:smtpPort` | No | `587` | `SMTP_PORT` | SMTP server port |
| `tixtalk:smtpUser` | No | — | `SMTP_USER` | SMTP username |
| `tixtalk:smtpPassword` | No | — | `SMTP_PASSWORD` | SMTP password (use `--secret`) |
| `tixtalk:pretixImageTag` | No | `stable` | `PRETIX_IMAGE_TAG` | Pretix Docker image tag |
| `tixtalk:pretalxImageTag` | No | `latest` | `PRETALX_IMAGE_TAG` | Pretalx Docker image tag |
| `tixtalk:repoUrl` | No | *(this repo)* | — | Git repo to clone on VM |

## Security

For production deployments, see [SECURITY.md](SECURITY.md) for detailed security guidance.

### Restricting SSH Access

By default, SSH is open to any IP. Restrict it with:

```bash
pulumi config set tixtalk:sshAllowedCidrs '["203.0.113.0/24", "198.51.100.42/32"]'
pulumi up
```

## Management CLI

Once deployed, manage your server with the cross-platform .NET CLI or directly via SSH.

### Cross-platform .NET CLI (`tixtalk`)

Runs on **Windows, macOS, and Linux**. Manages your remote server over SSH.

```bash
# Provision a new server (one-command setup)
tixtalk provision

# Or connect to an existing server
tixtalk connect azureuser@your-server-ip

# Then manage from anywhere
tixtalk                      # Interactive menu (Spectre.Console UI)
tixtalk status               # Service status + URLs
tixtalk update               # Pull latest images + restart
tixtalk upgrade              # Pull latest code + images + restart
tixtalk logs pretix          # Tail pretix logs
tixtalk backup               # Backup databases
tixtalk cron --install  # Install periodic task cron
tixtalk help                 # All commands

# Control SSH access (Azure deployments only)
tixtalk ssh open             # Open SSH from your current IP
tixtalk ssh close            # Block all SSH access
tixtalk ssh status           # Show SSH access state
```

#### Install the CLI

Requires [.NET 8+ SDK](https://dotnet.microsoft.com/download):

```bash
# Run directly from the repo
cd cli
dotnet run -- help

# Or build all platforms at once (Nuke build)
dotnet run --project build -- Publish

# Binaries land in output/publish/win-x64/ and output/publish/linux-x64/
```

Pre-built binaries are available on the [Releases](../../releases) page (`.zip` for Windows, `.deb` for Debian/Ubuntu, `.rpm` for Fedora/RHEL).

### On-server bash CLI (`manage.sh`)

When SSHed into the server directly, use `manage.sh`:

```bash
./manage.sh              # Interactive menu
./manage.sh status       # Service status + URLs
./manage.sh help         # All commands
```

## Local Development

Run the full stack on your local machine without Azure, DNS, or TLS. Requires a clone of this repository (the compose files and `.env.local` must be on disk).

```bash
# Start local dev environment (HTTP only)
./manage.sh dev

# Or via the CLI (from the repo directory):
tixtalk dev up
```

This gives you:
- **Pretix** at `http://localhost:8000`
- **Pretalx** at `http://localhost:8001`
- PostgreSQL + Redis running locally in containers
- No domain, no TLS, no Cloudflare required
- SMTP is not configured — email actions will be silently skipped

The `.env.local` file comes with pre-filled dev credentials. Edit it to customize.

To stop: `./manage.sh dev down` or `tixtalk dev down`

### First-time local setup

After starting for the first time, create admin accounts:

```bash
tixtalk dev superuser
# Or: ./manage.sh dev exec pretix pretix createsuperuser
```

## Staging / Dev Environment (Azure)

Deploy a separate Azure VM with prefixed subdomains (`dev-tickets.<DOMAIN>` / `dev-talks.<DOMAIN>`) that doesn't conflict with production.

### Provision

```bash
tixtalk provision    # Select "dev" when prompted for environment
```

The dev environment automatically:
- Uses `dev-` subdomain prefix (e.g., `dev-tickets.godotfest.com`)
- Creates separate Azure resources (resource group, VM, IP)
- Skips daily backup cron
- Gets its own Pulumi stack

### Test

After cloud-init completes (~5 minutes):
```bash
tixtalk status       # Check services are running
```

### Teardown

When done testing, destroy all dev resources:

```bash
tixtalk teardown     # Select "dev" stack — removes VM, DNS, IP, everything
```

> **Note:** `tixtalk teardown` requires a clone of this repository (it runs `pulumi destroy` from the `infra/` directory).

### Migration note

If you previously provisioned a `dev` stack, its DNS records (`tickets.<DOMAIN>`) may conflict with production. Destroy the old stack and re-provision to get the new prefixed records.

## Day-2 Operations

### Updating Containers

```bash
# Pull latest images and restart
./manage.sh update

# Pin a specific version
./manage.sh update --pretix 2025.1.0

# Update both
./manage.sh update --pretix 2025.1.0 --pretalx 2025.1.0
```

### Upgrading (Code + Containers)

```bash
# Pull latest code from git, then update images and restart
./manage.sh upgrade
```

This runs `git pull` followed by the full update flow. Use this after new features or fixes are pushed to the repo (e.g., the periodic task cron added above).

### Periodic Tasks

Both pretix and pretalx require `runperiodic` to run regularly for background tasks like sending emails, expiring orders, and processing payments. The standalone Docker images do **not** run this automatically.

```bash
# Install the cron job (runs every 5 minutes) — required!
./manage.sh cron --install

# Or run manually
./manage.sh cron

# Remove the cron job
./manage.sh cron --remove
```

For new Pulumi deployments, the cron job is installed automatically. For existing deployments, run `./manage.sh update` after pulling the latest code — it auto-installs the cron if missing.

### Backups

#### Manual backup

```bash
./manage.sh backup
```

Saves gzipped SQL dumps to `backups/` with timestamps.

#### Automatic daily backups

```bash
./manage.sh backup --install-cron
```

Runs at 3:00 AM daily. Backups older than 30 days are auto-deleted.

#### Restore from backup

```bash
# Interactive (lists available backups):
./manage.sh restore

# Direct:
./manage.sh restore backups/pretix_20260324-030000.sql.gz pretix
```

## Yearly Events

Both apps are multi-tenant — create new events in the web UI each year. No infrastructure changes needed:

- **Pretix**: `https://<TICKETS_HOST>/<organizer>/<year>/`
- **Pretalx**: `https://<TALKS_HOST>/<event-slug>/`

## Azure Communication Services (Email)

When using `tixtalk provision`, you can enable **Azure Communication Services** for email delivery. This is the recommended option when deploying to Azure.

### How it works

| Cloudflare | Domain type | Mail From |
|------------|-------------|-----------|
| ✓ Configured | Custom domain | `noreply@yourdomain.com` |
| ✗ Not configured | Azure-managed | `noreply@xxx.azurecomm.net` |

When using `tixtalk provision`, you'll be prompted to choose between custom domain (requires Cloudflare) or Azure-managed domain.

### Configuration

```bash
# Enable ACS email (default: true)
pulumi config set tixtalk:useAzureMail true

# Use custom domain (requires Cloudflare)
pulumi config set tixtalk:acsUseCustomDomain true

# Or use Azure-managed domain (default, no Cloudflare needed)
pulumi config set tixtalk:acsUseCustomDomain false

# Disable ACS entirely (use manual SMTP)
pulumi config set tixtalk:useAzureMail false
```

### Limitations

- **One domain per ACS instance**: A custom domain can only be linked to a single Azure Communication Service. If your domain is already configured with another ACS instance, you must either:
  - Remove the existing ACS domain configuration first
  - Use an Azure-managed domain (`*.azurecomm.net`) temporarily
  - Use manual SMTP configuration instead

- **DNS verification required**: Custom domains require DNS record verification before sending is enabled (~5 minutes after records are created)

- **Entra ID authentication**: SMTP uses Entra ID app credentials (auto-provisioned by Pulumi)

## Cloudflare Integration

### Setup

1. Go to [Cloudflare API Tokens](https://dash.cloudflare.com/profile/api-tokens)
2. Create a token with **Zone > DNS > Edit** permission for your domain
3. Copy your **Zone ID** from the domain's Overview page in Cloudflare
4. Set via Pulumi config:

```bash
pulumi config set tixtalk:cloudflareApiToken YOUR_TOKEN --secret
pulumi config set tixtalk:cloudflareZoneId YOUR_ZONE_ID
```

### TLS modes

| Mode | `CLOUDFLARE_DNS_CHALLENGE` | Cloudflare proxy | How it works |
|------|-------------------------|-----------------|--------------|
| **HTTP challenge** | `false` | Off (grey cloud) | Caddy validates via port 80. Simpler, standard image. |
| **DNS challenge** (Pulumi default) | `true` | On (orange cloud) | Caddy validates via Cloudflare API. Hides server IP, full CDN. |

To use DNS challenge:
```bash
pulumi config set tixtalk:cloudflareDnsChallenge true
```

This builds a custom Caddy image with the Cloudflare plugin (first deploy takes ~1 min longer).

### Manual DNS management

You can also run `scripts/cloudflare-dns.sh` independently to create/update DNS records without redeploying.

## Troubleshooting

### Check service status
```bash
docker compose ps
docker compose logs pretix --tail 50
docker compose logs pretalx --tail 50
```

### TLS certificate not working
For **HTTP challenge** mode (Caddy auto-provisions Let's Encrypt certs):
- DNS A records must point to the server
- Ports 80 and 443 must be open (`sudo ufw status`)

For **DNS challenge** mode (Caddy uses internal TLS, Cloudflare handles edge):
- Cloudflare SSL mode must be set to "Full"
- Cloudflare API token and Zone ID must be configured

Check Caddy logs: `docker compose logs caddy`

### Database connection issues
```bash
docker compose exec postgres psql -U tixtalk -l
```

### Restart everything
```bash
docker compose down && docker compose up -d
```

## Alternative: Manual VPS Deployment

If you already have a VPS (or prefer not to use Pulumi), you can deploy directly on any Ubuntu 22.04+ or Debian 12+ server with 2+ GB RAM.

### Prerequisites

- A VPS with SSH access
- A domain with DNS access

### Steps

1. **Set up DNS** — Point your hostnames (default: `tickets.<DOMAIN>` and `talks.<DOMAIN>`) to your server IP.

2. **Clone and configure:**

```bash
git clone <this-repo>
cd tixtalk

# Install Docker if needed
./manage.sh setup

# Create your configuration
cp .env.example .env
nano .env  # Set DOMAIN, Cloudflare (optional), SMTP settings
```

3. **Deploy:**

```bash
./manage.sh deploy
```

This generates secrets, creates Cloudflare DNS records (if configured), pulls images, and starts everything.

4. **Initialize (first time only):**

```bash
docker compose exec pretix pretix migrate
docker compose exec pretix pretix rebuild
docker compose exec pretalx pretalx migrate
docker compose exec pretalx pretalx rebuild
```

5. **Access your apps** at `https://<TICKETS_HOST>` and `https://<TALKS_HOST>` (defaults to `tickets.<DOMAIN>` and `talks.<DOMAIN>`).

See [Configuration Reference](#configuration-reference) for all `.env` variables (the `.env Equivalent` column).

## Destroying Everything

### Pulumi (recommended)

```bash
tixtalk teardown     # Interactive — select stack (dev or prod)

# Or manually (requires repo checkout):
cd infra
pulumi destroy    # Removes all Azure resources
```

> **Note:** `tixtalk teardown` requires a local clone of this repository (it runs Pulumi from the `infra/` directory).

### Manual VPS

```bash
docker compose down -v  # -v removes all data volumes
```
