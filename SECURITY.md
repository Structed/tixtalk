# Security Guide

This document covers security considerations, best practices, and maintenance procedures for the Pretix + Pretalx deployment.

## Table of Contents

- [Network Security](#network-security)
- [Secret Management](#secret-management)
- [SSH Access](#ssh-access)
- [Azure Communication Services Credentials](#azure-communication-services-credentials)
- [Backup Security](#backup-security)
- [Updates and Patching](#updates-and-patching)

## Network Security

### SSH Access Restriction

By default, the Azure NSG allows SSH (port 22) from any IP address. For production deployments, you should restrict this to specific IP ranges.

#### Option 1: Pulumi Config (Recommended)

```bash
# Restrict SSH to your office/VPN IP ranges
pulumi config set pre-talx-tix:sshAllowedCidrs '["203.0.113.0/24", "198.51.100.0/24"]'

# Or allow only a single IP
pulumi config set pre-talx-tix:sshAllowedCidrs '["203.0.113.42/32"]'

# Re-deploy to apply
pulumi up
```

#### Option 2: Azure Portal

1. Navigate to your resource group in Azure Portal
2. Find the Network Security Group (`{prefix}-nsg`)
3. Edit the "AllowSSH" inbound rule
4. Change "Source" from "Any" to "IP Addresses"
5. Enter your allowed CIDR ranges

### Firewall Rules

The following ports are exposed:

| Port | Protocol | Purpose | Notes |
|------|----------|---------|-------|
| 22 | TCP | SSH | Restrict in production |
| 80 | TCP | HTTP | Required for Let's Encrypt |
| 443 | TCP | HTTPS | Application traffic |
| 443 | UDP | HTTP/3 | Optional, improves performance |

### Internal Network

All services communicate over an internal Docker network. Database and Redis connections are not encrypted (SSL disabled) because they never leave the VM. This is standard for single-VM deployments.

For multi-VM deployments, enable SSL:
- PostgreSQL: Set `PRETIX_DATABASE_SSLMODE=require` and configure PostgreSQL SSL
- Redis: Use Redis TLS with stunnel or Redis 6+ native TLS

## Secret Management

### Secrets Location

| Secret | Location | Rotation Period |
|--------|----------|-----------------|
| DB Password | Pulumi state (encrypted) | As needed |
| Pretix Secret Key | Pulumi state (encrypted) | Never (breaks sessions) |
| Pretalx Secret Key | Pulumi state (encrypted) | Never (breaks sessions) |
| Admin Password | Pulumi state (encrypted) | As needed |
| SMTP Password | Pulumi state (encrypted) | Per provider policy |
| ACS Client Secret | Pulumi state (encrypted) | Yearly |
| Cloudflare Token | Pulumi config (encrypted) | As needed |

### Viewing Secrets

```bash
# View admin password
pulumi stack output adminPassword --show-secrets

# View all outputs (secrets masked)
pulumi stack output
```

### Changing Database Password

1. Update Pulumi config and redeploy (regenerates password)
2. Or manually:
   ```bash
   # On the VM
   docker compose exec postgres psql -U pretalxtix -c "ALTER USER pretalxtix PASSWORD 'new_password';"
   
   # Update .env
   sed -i 's/^DB_PASSWORD=.*/DB_PASSWORD=new_password/' .env
   
   # Restart apps
   docker compose restart pretix pretalx
   ```

### Django Secret Keys

**Warning:** Changing `PRETIX_SECRET_KEY` or `PRETALX_SECRET_KEY` will invalidate all existing sessions, password reset tokens, and signed URLs. Only rotate if compromised.

## SSH Access

### On-Demand SSH Access (Azure Deployments)

For Azure deployments, you can dynamically control SSH access using the `ptx` CLI. This lets you keep SSH closed by default and only open it when needed.

```bash
# Open SSH access from your current public IP
ptx ssh open

# Open SSH from a specific IP/CIDR
ptx ssh open 203.0.113.42/32

# Check current SSH access status
ptx ssh status

# Close SSH access (sets NSG rule to deny)
ptx ssh close
```

**Menu option:** The interactive menu (`ptx`) also has "Open SSH access", "Close SSH access", and "SSH access status" options under "Azure SSH Access".

**Setup:** If you used `ptx provision`, Azure resource info is automatically saved. For existing deployments, configure manually:

```bash
ptx ssh config
# Enter: Resource Group name and NSG name
```

### Key-based Authentication Only

The VM is configured with password authentication disabled. Only SSH key authentication is allowed.

### Recommended Practices

1. **Use Ed25519 keys** (more secure than RSA):
   ```bash
   ssh-keygen -t ed25519 -C "your-email@example.com"
   ```

2. **Use a passphrase** on your private key

3. **Use SSH agent forwarding** instead of copying keys to the VM:
   ```bash
   ssh -A azureuser@your-vm-ip
   ```

4. **Audit authorized keys** periodically:
   ```bash
   cat ~/.ssh/authorized_keys
   ```

### Adding Additional SSH Keys

```bash
# On the VM, add to authorized_keys
echo "ssh-ed25519 AAAA... user@host" >> ~/.ssh/authorized_keys
```

### Emergency Access

If you lose SSH access:
1. Use Azure Serial Console (Azure Portal → VM → Serial Console)
2. Or reset SSH key via Azure Portal → VM → Reset Password

## Azure Communication Services Credentials

### Credential Rotation (Yearly)

The ACS application client secret expires after **1 year**. You must rotate it before expiry to maintain email functionality.

#### Check Expiry Date

```bash
# Via Azure CLI
az ad app credential list --id $(pulumi stack output acsAppId --show-secrets 2>/dev/null || echo "check-azure-portal")
```

Or check in Azure Portal:
1. Go to Azure Active Directory → App Registrations
2. Find `{prefix}-email-smtp`
3. Check "Certificates & secrets" → "Client secrets"

#### Rotation Procedure

1. **Create new secret** (Azure Portal or CLI):
   ```bash
   # Get the app ID from Pulumi
   APP_ID=$(az ad app list --display-name "{prefix}-email-smtp" --query "[0].appId" -o tsv)
   
   # Create new secret
   az ad app credential reset --id $APP_ID --append
   ```

2. **Update .env on the VM**:
   ```bash
   # SSH to VM
   ssh azureuser@your-vm-ip
   
   # Update SMTP_PASSWORD in .env
   nano /opt/pretalxtix/.env
   
   # Restart services
   cd /opt/pretalxtix
   docker compose restart pretix pretalx
   ```

3. **Test email sending** in Pretix/Pretalx admin panel

4. **Delete old secret** after confirming new one works

#### Automating Rotation

For production, consider:
- Setting up Azure Key Vault with automatic rotation
- Using managed identities instead of client secrets
- Setting a calendar reminder 1 month before expiry

## Backup Security

### Encryption at Rest

Database backups are stored in `backups/` as gzipped SQL dumps. They are **not encrypted** by default.

#### Encrypting Backups

Modify `scripts/backup.sh` to encrypt backups:

```bash
# Add after the pg_dump line:
# Encrypt with age (https://age-encryption.org/)
age -r age1... "$BACKUP_FILE" > "${BACKUP_FILE}.age" && rm "$BACKUP_FILE"
```

Or use GPG:
```bash
gpg --encrypt --recipient your-key-id "$BACKUP_FILE" && rm "$BACKUP_FILE"
```

### Offsite Backup

By default, backups are only stored on the VM. For disaster recovery, copy them offsite:

```bash
# Example: sync to Azure Blob Storage
az storage blob upload-batch \
  --account-name mystorageaccount \
  --destination backups \
  --source /opt/pretalxtix/backups
```

### Backup Retention

Backups older than 30 days are automatically deleted. Adjust in `scripts/backup.sh`:

```bash
# Change -mtime +30 to your preferred retention (in days)
find "$BACKUP_DIR" -name "*.sql.gz" -mtime +30 -delete
```

## Updates and Patching

### Automatic Security Updates

The VM has `unattended-upgrades` installed, which automatically applies security patches for the OS.

Check status:
```bash
systemctl status unattended-upgrades
cat /var/log/unattended-upgrades/unattended-upgrades.log
```

### Container Updates

Application containers are **not** auto-updated. Update manually:

```bash
# Update to latest stable versions
./manage.sh update

# Or pin specific versions
./manage.sh update --pretix 2025.1.0 --pretalx 2025.1.0
```

**Recommended:** Test updates in a staging environment before production.

### Checking for Vulnerabilities

```bash
# Scan container images
docker scout cves pretix/standalone:stable
docker scout cves pretalx/standalone:latest
```

## Reporting Security Issues

If you discover a security vulnerability in this project, please report it privately by emailing the maintainers. Do not create a public GitHub issue.

For vulnerabilities in Pretix or Pretalx themselves:
- Pretix: https://pretix.eu/about/en/security/
- Pretalx: security@pretalx.org
