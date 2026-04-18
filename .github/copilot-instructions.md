# Copilot Instructions

## Build

```powershell
# Build the CLI project
dotnet build cli/

# Publish all platforms (Nuke)
dotnet run --project build -- Publish

# Full release build including .zip/.deb/.rpm (requires nfpm on PATH)
dotnet run --project build -- Package
```

There are no tests or linters configured in this project.

## Architecture

This project deploys [pretix](https://pretix.eu/) (ticketing) and [pretalx](https://pretalx.com/) (CfP/scheduling) to an Azure VM. It has two deployment paths:

1. **Automated (recommended)**: `tixtalk provision` — interactive wizard provisions an Azure VM via Pulumi, runs cloud-init to install Docker, clone the repo, write `.env`, start services, run migrations, and set up daily backups.
2. **Manual VPS**: Clone the repo on any Ubuntu/Debian server, configure `.env`, and run `manage.sh deploy`.

**Services** (defined in `docker-compose.yml`):

```
Caddy (reverse proxy, auto TLS)
├── tickets.{DOMAIN} → pretix:80
└── talks.{DOMAIN}   → pretalx:80

PostgreSQL 16 (shared, two databases: pretix, pretalx)
Redis 7 (shared, DB-index isolated)
Pretix (pretix/standalone)
Pretalx (pretalx/standalone)
```

**Azure infrastructure** (defined in `infra/`, provisioned via Pulumi):

```
Resource Group
├── VNet + Subnet + NSG (SSH, HTTP, HTTPS, HTTP/3)
├── Static Public IP + NIC
├── Ubuntu 24.04 LTS VM (cloud-init bootstrapped)
└── Azure Communication Services (optional, for email)
    ├── Email Service + Domain (Azure-managed or custom)
    ├── Entra ID App + Service Principal (SMTP auth)
    └── Role Assignment (Communication and Email Service Owner)
```

All docker-compose configuration is via `.env` (copied from `.env.example`). For Pulumi deployments, secrets are auto-generated and injected via cloud-init.

The primary user interfaces are:
- **`tixtalk` CLI** (cross-platform .NET app in `cli/`) — provisions Azure VMs, manages remote servers over SSH
- **`manage.sh`** (bash, on-server) — interactive menu or subcommands (e.g., `./manage.sh status`). Supports remote management via `./manage.sh remote user@host [cmd]`.

## Conventions

### Redis DB index allocation

A single Redis instance is shared; apps are isolated by database number:

| DB | Owner | Purpose |
|----|-------|---------|
| 0 | pretix | Cache/sessions |
| 1 | pretix | Celery broker |
| 2 | pretix | Celery backend |
| 3 | pretalx | Cache |
| 4 | pretalx | Celery broker |
| 5 | pretalx | Celery backend |

### Scripts

`manage.sh` is the primary on-server entry point. It delegates to individual scripts in `scripts/`:

- `setup.sh` — install Docker, configure firewall (run once)
- `deploy.sh` — generate secrets, create DNS records, start services (run once)
- `update.sh` — pull latest images + restart
- `backup.sh` — pg_dump both databases (supports `--install-cron`)
- `restore.sh` — restore from a backup file
- `init-db.sh` — PostgreSQL entrypoint script (creates both databases on first start)
- `cloudflare-dns.sh` — create/update Cloudflare A records via API (called by deploy.sh)

### Env var formats

- **Pretix**: `PRETIX_{SECTION}_{KEY}` — e.g., `PRETIX_DATABASE_HOST`, `PRETIX_REDIS_LOCATION`
- **Pretalx**: `PRETALX_{SECTION}_{KEY}` — e.g., `PRETALX_DB_TYPE`, `PRETALX_DB_HOST`, `PRETALX_DB_PASS` (note: `_PASS` not `_PASSWORD`; `PRETALX_REDIS` not `PRETALX_REDIS_LOCATION`; does **not** support `DATABASE_URL`)

### TLS and DNS

Two TLS modes controlled by `CLOUDFLARE_DNS_CHALLENGE` in `.env`:

- **HTTP challenge** (`false`, default) — standard `caddy:2-alpine` image, validates via port 80. Uses `caddy/Caddyfile`. Cloudflare proxy must be off (grey-cloud).
- **DNS challenge** (`true`) — custom Caddy image built from `caddy/Dockerfile` with cloudflare plugin. Uses `caddy/Caddyfile.dns`. Works with Cloudflare proxy on (orange-cloud).

When DNS challenge is enabled, `deploy.sh` and `update.sh` use the compose override: `docker compose -f docker-compose.yml -f docker-compose.cloudflare.yml`.

### .NET CLI (`cli/`)

A cross-platform .NET 8 console app for Azure provisioning and remote server management. Runs on Windows, macOS, and Linux.

- **Spectre.Console** for terminal UI (interactive menus, styled output)
- **SSH.NET** for non-interactive remote commands (status, deploy, backup, etc.)
- Falls back to native `ssh -t` for interactive commands (logs, shell, restore)

Structure:
- `Program.cs` — entry point, command dispatch (no args = interactive menu; `provision` = Azure wizard)
- `Config.cs` — manages `~/.tixtalk/config.json` (SSH host, key file, remote project dir). Uses source-generated JSON serialization.
- `Remote.cs` — SSH execution (SSH.NET for commands, native ssh for TTY)
- `Menu.cs` — Spectre.Console selection prompt, mirrors manage.sh menu. First-run flow offers "Provision new server (Azure)" or "Connect to existing server".
- `Provision.cs` — interactive Azure provisioning wizard. Gathers domain, SSH key, region, VM size, admin email, email provider (ACS or manual SMTP), and Cloudflare config. Drives Pulumi (`pulumi stack init`, `pulumi config set`, `pulumi up`) against the `infra/` project, then auto-configures the CLI to connect to the new VM.

The CLI proxies most commands to `manage.sh` on the remote server — it's a cross-platform SSH client wrapper. The bash scripts (`manage.sh` + `scripts/`) remain the single source of truth for server-side operations. The `provision` command is the exception: it runs Pulumi locally.

### Pulumi infrastructure (`infra/`)

A .NET 8 Pulumi project (`TixTalk.Infra`) that provisions Azure resources. Uses `Pulumi.AzureNative`, `Pulumi.AzureAD`, and `Pulumi.Random`.

Structure:
- `Program.cs` — Pulumi entry point, reads config, orchestrates all stacks, defines outputs
- `Infrastructure/ResourceGroupStack.cs` — creates the Azure Resource Group
- `Infrastructure/NetworkStack.cs` — VNet, Subnet, NSG (SSH/HTTP/HTTPS/HTTP3), Public IP, NIC
- `Infrastructure/VirtualMachineStack.cs` — Ubuntu 24.04 LTS VM with cloud-init
- `Infrastructure/AzureCommunicationStack.cs` — Azure Communication Services for email (Email Service, Domain, Sender Username, Communication Service, Entra ID App + Service Principal, role assignment). Supports Azure-managed or custom domains.
- `Helpers/SecretGenerator.cs` — auto-generates DB password, secret keys, admin password (encrypted in Pulumi state)
- `Helpers/CloudInitBuilder.cs` — builds the cloud-init script that bootstraps Docker, clones the repo, writes `.env`, starts services, runs migrations, and sets up backups

Key Pulumi config keys (set via `pulumi config set tixtalk:<key>`):
- `domain` (required), `sshPublicKey` (required)
- `prefix`, `vmSize`, `adminEmail`, `repoUrl`, `repoBranch`
- `useAzureMail`, `acsUseCustomDomain`
- `smtpHost`, `smtpPort`, `smtpUser`, `smtpPassword`, `mailFrom`
- `cloudflareApiToken`, `cloudflareZoneId`, `cloudflareDnsChallenge`

### Build system (`build/`)

Uses **Nuke.Build** (.NET build automation). The build project is at `build/_build.csproj`. Build logic is split across partial classes:

- `Build.cs` — `Clean`, `Restore`, `Compile`, `Publish` targets
- `Build.Package.cs` — `Package` target (zip + nfpm)

**Targets** (in dependency order):
- `Clean` → removes `output/` and `cli/bin/`, `cli/obj/`
- `Restore` → `dotnet restore` on `cli/TixTalk.Cli.csproj`
- `Compile` → `dotnet build` (default target)
- `Publish` → self-contained, trimmed, single-file publish for `win-x64` and `linux-x64` into `output/publish/{rid}/`
- `Package` → creates `output/packages/tixtalk-win-x64.zip`, `.deb`, and `.rpm` (requires `nfpm` on PATH)

**Linux packaging**: `nfpm.yaml` at repo root defines `.deb` and `.rpm` package metadata. The `VERSION` env var sets the package version.

**Release workflow**: `.github/workflows/release.yml` triggers on `v*` tag push. Publishes binaries, creates `.zip`/`.deb`/`.rpm`, and uploads to a GitHub Release.
