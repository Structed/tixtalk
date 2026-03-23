# Pretix + Pretalx on Azure

Fully automated, cost-optimized deployment of [pretix](https://pretix.eu/) (ticketing) and [pretalx](https://pretalx.com/) (call for papers & scheduling) on Azure Container Apps using Pulumi (C#).

## Architecture

| Component | Azure Service | Est. Cost/mo |
|-----------|--------------|-------------|
| Pretix web + worker | Azure Container Apps (0.5 vCPU / 1 Gi) | ~$15 |
| Pretalx web + worker | Azure Container Apps (0.5 vCPU / 1 Gi) | ~$15 |
| Redis (shared cache) | Azure Container Apps (0.25 vCPU / 0.5 Gi) | ~$10 |
| PostgreSQL | Flexible Server (Burstable B1ms) | ~$13 |
| Persistent storage | Azure Files (5 GB × 2) | ~$1 |
| Logging | Log Analytics (30-day retention) | ~$1 |
| **Total** | | **~$55** |

## Prerequisites

- [.NET SDK 8.0+](https://dotnet.microsoft.com/download)
- [Pulumi CLI](https://www.pulumi.com/docs/install/)
- [Azure CLI](https://docs.microsoft.com/cli/azure/install-azure-cli)
- An Azure subscription
- A Pulumi account (free tier works)

## Quick Start

### 1. Clone and configure

```powershell
git clone <this-repo>
cd pre-talx-tix-azure

# Login to Azure and Pulumi
az login
pulumi login

# Create a stack
pulumi stack init dev
```

### 2. Set required configuration

```powershell
# Azure region
pulumi config set azure-native:location westeurope

# Resource name prefix (lowercase, no special chars)
pulumi config set prefix godotfest

# Your public URLs (set after you know the ACA FQDNs or if using custom domains)
pulumi config set pretixUrl https://tickets.example.com
pulumi config set pretalxUrl https://cfp.example.com

# Email sender address
pulumi config set mailFrom noreply@example.com
```

### 3. Set optional SMTP configuration

```powershell
pulumi config set smtpHost smtp.example.com
pulumi config set smtpPort 587
pulumi config set smtpUser your-smtp-user
pulumi config set --secret smtpPassword your-smtp-password
```

### 4. Deploy

```powershell
.\Scripts\Deploy.ps1
```

Or manually:

```powershell
dotnet build
pulumi up
```

### 5. Initialize apps (first deploy only)

```powershell
.\Scripts\InitApps.ps1
```

This runs database migrations and rebuilds static files.

### 6. Access your apps

After deployment, Pulumi outputs the URLs:

```powershell
pulumi stack output pretixUrl
pulumi stack output pretalxUrl
```

## Updating Container Images

Update to a new pretix or pretalx version with zero downtime:

```powershell
# Update pretix to a specific version
.\Scripts\Update.ps1 -PretixTag 2025.1.0

# Update pretalx
.\Scripts\Update.ps1 -PretalxTag 2025.1.0

# Update both at once
.\Scripts\Update.ps1 -PretixTag 2025.1.0 -PretalxTag 2025.1.0

# Skip confirmation prompt
.\Scripts\Update.ps1 -PretixTag stable -AutoApprove
```

## Custom Domains

Azure Container Apps supports custom domains with managed TLS certificates.

1. Add a CNAME record pointing your domain to the ACA FQDN
2. Configure the custom domain in Azure Portal → Container Apps → Custom domains
3. Update the app URL:

```powershell
pulumi config set pretixUrl https://tickets.yourdomain.com
pulumi up
```

## Database Backups

Automated daily backups are enabled (7-day retention). For on-demand backups:

```powershell
.\Scripts\BackupDb.ps1
```

## Configuration Reference

| Key | Required | Default | Description |
|-----|----------|---------|-------------|
| `azure-native:location` | Yes | — | Azure region |
| `prefix` | Yes | — | Resource name prefix |
| `pretixImageTag` | No | `stable` | Pretix Docker image tag |
| `pretalxImageTag` | No | `latest` | Pretalx Docker image tag |
| `pretixUrl` | No | — | Pretix public URL |
| `pretalxUrl` | No | — | Pretalx public URL |
| `mailFrom` | No | `noreply@example.com` | Email sender address |
| `smtpHost` | No | — | SMTP server hostname |
| `smtpPort` | No | `587` | SMTP server port |
| `smtpUser` | No | — | SMTP username |
| `smtpPassword` | No | — | SMTP password (secret) |

## Project Structure

```
├── Program.cs                              # Pulumi entry point
├── Infrastructure/
│   ├── ResourceGroupStack.cs               # Azure Resource Group
│   ├── PostgreSqlStack.cs                  # PostgreSQL Flexible Server
│   ├── StorageStack.cs                     # Azure Storage + File Shares
│   ├── ContainerAppsEnvironmentStack.cs    # ACA environment + storage mounts
│   ├── RedisContainerApp.cs                # Redis container (internal)
│   ├── PretixContainerApp.cs               # Pretix container (external)
│   └── PretalxContainerApp.cs              # Pretalx container (external)
├── Helpers/
│   ├── NamingConventions.cs                # Azure naming helpers
│   └── SecretGenerator.cs                  # Random secret generation
├── Scripts/
│   ├── Deploy.ps1                          # First-time deployment
│   ├── Update.ps1                          # Update container images
│   ├── BackupDb.ps1                        # On-demand DB backup
│   └── InitApps.ps1                        # Post-deploy initialization
├── PreTalxTixAzure.csproj                  # .NET project
├── Pulumi.yaml                             # Pulumi project config
└── Pulumi.dev.yaml                         # Dev stack config
```

## Troubleshooting

### Container won't start
```powershell
az containerapp logs show --name <prefix>-pretix --resource-group <prefix>-rg --type system
az containerapp logs show --name <prefix>-pretix --resource-group <prefix>-rg --type console
```

### Database connection issues
Verify the firewall rule allows Azure services:
```powershell
az postgres flexible-server firewall-rule list --resource-group <prefix>-rg --name <prefix>-pg
```

### Check container app status
```powershell
az containerapp show --name <prefix>-pretix --resource-group <prefix>-rg --query "properties.runningStatus"
```

## Destroying Resources

To tear down all resources:
```powershell
pulumi destroy
pulumi stack rm dev
```
