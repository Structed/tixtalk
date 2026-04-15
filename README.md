# Pretix + Pretalx on Azure

Self-hosted [pretix](https://pretix.eu/) (ticketing) and [pretalx](https://pretalx.com/) (call for papers & scheduling) on an Azure VM. One command provisions the infrastructure, installs Docker, deploys all services, runs migrations, and sets up daily backups.

```bash
ptx provision    # Interactive wizard → fully running deployment
```

## What You Get

| Service | Image | Purpose |
|---------|-------|---------|
| **Caddy** | `caddy:2-alpine` | Reverse proxy + automatic Let's Encrypt TLS |
| **PostgreSQL** | `postgres:16-alpine` | Shared database (pretix + pretalx DBs) |
| **Redis** | `redis:7-alpine` | Shared cache + Celery task queue |
| **Pretix** | `pretix/standalone` | Ticketing at `tickets.yourdomain.com` |
| **Pretalx** | `pretalx/standalone` | CfP/scheduling at `talks.yourdomain.com` |

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
3. VM cloud-init installs Docker, clones repo, starts services, runs migrations, and sets up daily backups
4. Configures the `ptx` CLI to connect to the new server

After ~5 minutes your apps are live. Point DNS and visit them:
- **Pretix**: `https://tickets.yourdomain.com`
- **Pretalx**: `https://talks.yourdomain.com`

### Manual Pulumi setup (advanced)

If you prefer to run Pulumi directly:

```bash
cd infra

# Initialize a Pulumi stack (first time only)
pulumi stack init dev

# Required config
pulumi config set pre-talx-tix:domain yourdomain.com
pulumi config set pre-talx-tix:sshPublicKey "$(cat ~/.ssh/id_rsa.pub)"

# Optional — email (required for ticket confirmations & CfP notifications)
pulumi config set pre-talx-tix:smtpHost smtp.example.com
pulumi config set pre-talx-tix:smtpUser user@example.com
pulumi config set pre-talx-tix:smtpPassword YOUR_PASSWORD --secret
pulumi config set pre-talx-tix:mailFrom noreply@yourdomain.com

# Optional — Cloudflare DNS automation
pulumi config set pre-talx-tix:cloudflareApiToken YOUR_TOKEN --secret
pulumi config set pre-talx-tix:cloudflareZoneId YOUR_ZONE_ID
```

Then deploy, set up DNS, and access your apps:

```bash
pulumi up
```

Cloud-init runs on first boot (~5 min): installs Docker, starts services, runs migrations, sets up daily backups.

Point DNS to the VM IP from `pulumi stack output vmPublicIp`:

```
tickets.yourdomain.com → <vmPublicIp>
talks.yourdomain.com   → <vmPublicIp>
```

If you configured Cloudflare, DNS records are created automatically.

- **Pretix**: `https://tickets.yourdomain.com`
- **Pretalx**: `https://talks.yourdomain.com`

Both apps have web-based setup wizards on first visit.

### Pulumi Outputs

After deployment, `pulumi stack output` displays:

| Output | Description |
|--------|-------------|
| `vmPublicIp` | VM public IP address |
| `sshCommand` | Ready-to-use SSH command |
| `pretixUrl` | `https://tickets.<domain>` |
| `pretalxUrl` | `https://talks.<domain>` |

## Configuration Reference

All configuration is managed via Pulumi config (`pulumi config set <key> <value>`):

| Pulumi Config Key | Required | Default | `.env` Equivalent | Description |
|-------------------|----------|---------|-------------------|-------------|
| `pre-talx-tix:domain` | Yes | — | `DOMAIN` | Your domain (e.g., `yourdomain.com`) |
| `pre-talx-tix:sshPublicKey` | Yes | — | — | SSH public key for VM access |
| `pre-talx-tix:prefix` | No | `pretalxtix` | — | Azure resource name prefix |
| `pre-talx-tix:vmSize` | No | `Standard_B2s` | — | Azure VM SKU |
| `azure-native:location` | No | `westeurope` | — | Azure region |
| `pre-talx-tix:cloudflareApiToken` | No | — | `CLOUDFLARE_API_TOKEN` | Cloudflare API token (use `--secret`) |
| `pre-talx-tix:cloudflareZoneId` | No | — | `CLOUDFLARE_ZONE_ID` | Cloudflare Zone ID |
| `pre-talx-tix:cloudflareDnsChallenge` | No | `false` | `CLOUDFLARE_DNS_CHALLENGE` | Use DNS challenge for TLS |
| `pre-talx-tix:useAzureMail` | No | `true` | — | Use Azure Communication Services for email |
| `pre-talx-tix:acsUseCustomDomain` | No | `false` | — | Use custom domain for ACS (requires Cloudflare) |
| `pre-talx-tix:mailFrom` | No | `noreply@example.com` | `MAIL_FROM` | Email sender address |
| `pre-talx-tix:smtpHost` | No | — | `SMTP_HOST` | SMTP server hostname |
| `pre-talx-tix:smtpPort` | No | `587` | `SMTP_PORT` | SMTP server port |
| `pre-talx-tix:smtpUser` | No | — | `SMTP_USER` | SMTP username |
| `pre-talx-tix:smtpPassword` | No | — | `SMTP_PASSWORD` | SMTP password (use `--secret`) |
| `pre-talx-tix:pretixImageTag` | No | `stable` | `PRETIX_IMAGE_TAG` | Pretix Docker image tag |
| `pre-talx-tix:pretalxImageTag` | No | `latest` | `PRETALX_IMAGE_TAG` | Pretalx Docker image tag |
| `pre-talx-tix:repoUrl` | No | *(this repo)* | — | Git repo to clone on VM |

## Management CLI

Once deployed, manage your server with the cross-platform .NET CLI or directly via SSH.

### Cross-platform .NET CLI (`ptx`)

Runs on **Windows, macOS, and Linux**. Manages your remote server over SSH.

```bash
# Provision a new server (one-command setup)
ptx provision

# Or connect to an existing server
ptx connect azureuser@your-server-ip

# Then manage from anywhere
ptx                      # Interactive menu (Spectre.Console UI)
ptx status               # Service status + URLs
ptx update               # Pull latest images + restart
ptx logs pretix          # Tail pretix logs
ptx backup               # Backup databases
ptx help                 # All commands
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

- **Pretix**: `https://tickets.yourdomain.com/<organizer>/<year>/`
- **Pretalx**: `https://talks.yourdomain.com/<event-slug>/`

## Azure Communication Services (Email)

When using `ptx provision`, you can enable **Azure Communication Services** for email delivery. This is the recommended option when deploying to Azure.

### How it works

| Cloudflare | Domain type | Mail From |
|------------|-------------|-----------|
| ✓ Configured | Custom domain | `noreply@yourdomain.com` |
| ✗ Not configured | Azure-managed | `noreply@xxx.azurecomm.net` |

When using `ptx provision`, you'll be prompted to choose between custom domain (requires Cloudflare) or Azure-managed domain.

### Configuration

```bash
# Enable ACS email (default: true)
pulumi config set pre-talx-tix:useAzureMail true

# Use custom domain (requires Cloudflare)
pulumi config set pre-talx-tix:acsUseCustomDomain true

# Or use Azure-managed domain (default, no Cloudflare needed)
pulumi config set pre-talx-tix:acsUseCustomDomain false

# Disable ACS entirely (use manual SMTP)
pulumi config set pre-talx-tix:useAzureMail false
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
pulumi config set pre-talx-tix:cloudflareApiToken YOUR_TOKEN --secret
pulumi config set pre-talx-tix:cloudflareZoneId YOUR_ZONE_ID
```

### TLS modes

| Mode | `cloudflareDnsChallenge` | Cloudflare proxy | How it works |
|------|-------------------------|-----------------|--------------|
| **HTTP challenge** (default) | `false` | Off (grey cloud) | Caddy validates via port 80. Simpler, standard image. |
| **DNS challenge** | `true` | On (orange cloud) | Caddy validates via Cloudflare API. Hides server IP, full CDN. |

To use DNS challenge:
```bash
pulumi config set pre-talx-tix:cloudflareDnsChallenge true
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

## Alternative: Manual VPS Deployment

If you already have a VPS (or prefer not to use Pulumi), you can deploy directly on any Ubuntu 22.04+ or Debian 12+ server with 2+ GB RAM.

### Prerequisites

- A VPS with SSH access
- A domain with DNS access

### Steps

1. **Set up DNS** — Point `tickets.yourdomain.com` and `talks.yourdomain.com` to your server IP.

2. **Clone and configure:**

```bash
git clone <this-repo>
cd pre-talx-tix-azure

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

5. **Access your apps** at `https://tickets.yourdomain.com` and `https://talks.yourdomain.com`.

See [Configuration Reference](#configuration-reference) for all `.env` variables (the `.env Equivalent` column).

## Destroying Everything

### Pulumi (recommended)

```bash
cd infra
pulumi destroy    # Removes all Azure resources
```

### Manual VPS

```bash
docker compose down -v  # -v removes all data volumes
```
