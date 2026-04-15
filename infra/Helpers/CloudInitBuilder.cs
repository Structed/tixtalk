using System.Text;
using Pulumi;

namespace PreTalxTix.Infra.Helpers;

public record CloudInitConfig
{
    public required string RepoUrl { get; init; }
    public string RepoBranch { get; init; } = ""; // Empty = default branch
    public required string Domain { get; init; }
    public required Output<string> DbUser { get; init; }
    public required Output<string> DbPassword { get; init; }
    public required Output<string> PretixSecretKey { get; init; }
    public required Output<string> PretalxSecretKey { get; init; }
    public string PretixImageTag { get; init; } = "stable";
    public string PretalxImageTag { get; init; } = "latest";
    public string CloudflareApiToken { get; init; } = "";
    public string CloudflareZoneId { get; init; } = "";
    public string CloudflareDnsChallenge { get; init; } = "false";
    public string SmtpHost { get; init; } = "";
    public int SmtpPort { get; init; } = 587;
    public string SmtpUser { get; init; } = "";
    public string SmtpPassword { get; init; } = "";
    public string MailFrom { get; init; } = "noreply@example.com";
}

/// <summary>
/// Builds a cloud-init config that provisions the VM on first boot.
/// Uses write_files for complex content (setup script + .env) and a
/// minimal runcmd to avoid YAML parsing issues.
/// </summary>
public static class CloudInitBuilder
{
    public static Output<string> Build(CloudInitConfig cfg)
    {
        return Output.Tuple(cfg.DbPassword, cfg.PretixSecretKey, cfg.PretalxSecretKey, cfg.DbUser)
            .Apply(t =>
            {
                var (dbPassword, pretixSecret, pretalxSecret, dbUser) = t;
                return Generate(cfg, dbUser, dbPassword, pretixSecret, pretalxSecret);
            });
    }

    private static string Generate(
        CloudInitConfig cfg,
        string dbUser,
        string dbPassword,
        string pretixSecret,
        string pretalxSecret)
    {
        // Build .env content and base64-encode it
        var envContent = BuildEnvContent(cfg, dbUser, dbPassword, pretixSecret, pretalxSecret);
        var envBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(envContent));

        // Build setup script and base64-encode it
        var setupScript = BuildSetupScript(cfg, dbUser);
        var scriptBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(setupScript));

        // Build minimal cloud-init YAML — no block scalars, no heredocs
        var sb = new StringBuilder();
        sb.Append("#cloud-config\n");
        sb.Append("package_update: true\n");
        sb.Append("package_upgrade: true\n");
        sb.Append("\n");
        sb.Append("packages:\n");
        sb.Append("  - git\n");
        sb.Append("  - curl\n");
        sb.Append("  - ufw\n");
        sb.Append("  - cron\n");
        sb.Append("\n");
        sb.Append("write_files:\n");
        sb.Append("  - path: /opt/pretalxtix-env\n");
        sb.Append("    encoding: b64\n");
        sb.Append($"    content: {envBase64}\n");
        sb.Append("  - path: /opt/setup.sh\n");
        sb.Append("    permissions: '0755'\n");
        sb.Append("    encoding: b64\n");
        sb.Append($"    content: {scriptBase64}\n");
        sb.Append("\n");
        sb.Append("runcmd:\n");
        sb.Append("  - [bash, /opt/setup.sh]\n");

        return sb.ToString();
    }

    private static string BuildEnvContent(
        CloudInitConfig cfg,
        string dbUser,
        string dbPassword,
        string pretixSecret,
        string pretalxSecret)
    {
        var sb = new StringBuilder();
        sb.Append($"DOMAIN={cfg.Domain}\n");
        sb.Append($"DB_USER={dbUser}\n");
        sb.Append($"DB_PASSWORD={dbPassword}\n");
        sb.Append($"PRETIX_SECRET_KEY={pretixSecret}\n");
        sb.Append($"PRETALX_SECRET_KEY={pretalxSecret}\n");
        sb.Append($"PRETIX_IMAGE_TAG={cfg.PretixImageTag}\n");
        sb.Append($"PRETALX_IMAGE_TAG={cfg.PretalxImageTag}\n");
        sb.Append($"CLOUDFLARE_API_TOKEN={cfg.CloudflareApiToken}\n");
        sb.Append($"CLOUDFLARE_ZONE_ID={cfg.CloudflareZoneId}\n");
        sb.Append($"CLOUDFLARE_DNS_CHALLENGE={cfg.CloudflareDnsChallenge}\n");
        sb.Append($"MAIL_FROM={cfg.MailFrom}\n");
        sb.Append($"SMTP_HOST={cfg.SmtpHost}\n");
        sb.Append($"SMTP_PORT={cfg.SmtpPort}\n");
        sb.Append($"SMTP_USER={cfg.SmtpUser}\n");
        sb.Append($"SMTP_PASSWORD={cfg.SmtpPassword}\n");
        return sb.ToString();
    }

    private static string BuildSetupScript(CloudInitConfig cfg, string dbUser)
    {
        var sb = new StringBuilder();
        sb.Append("#!/bin/bash\n");
        sb.Append("set -euo pipefail\n");
        sb.Append("exec > >(tee -a /var/log/pretalxtix-setup.log) 2>&1\n");
        sb.Append("echo '=== PreTalxTix setup started ==='\n");
        sb.Append("\n");

        // Install Docker
        sb.Append("echo 'Installing Docker...'\n");
        sb.Append("curl -fsSL https://get.docker.com | sh\n");
        sb.Append("systemctl enable docker\n");
        sb.Append("systemctl start docker\n");
        sb.Append("\n");

        // Add azureuser to docker group (so they can run docker without sudo)
        sb.Append("usermod -aG docker azureuser\n");
        sb.Append("\n");

        // Verify Docker is ready
        sb.Append("echo 'Verifying Docker is ready...'\n");
        sb.Append("for i in $(seq 1 30); do\n");
        sb.Append("  if docker info >/dev/null 2>&1; then\n");
        sb.Append("    echo 'Docker is ready.'\n");
        sb.Append("    break\n");
        sb.Append("  fi\n");
        sb.Append("  if [ \"$i\" -eq 30 ]; then echo 'ERROR: Docker not ready'; exit 1; fi\n");
        sb.Append("  echo \"Waiting for Docker... ($i/30)\"\n");
        sb.Append("  sleep 2\n");
        sb.Append("done\n");
        sb.Append("\n");

        // Configure firewall
        sb.Append("echo 'Configuring firewall...'\n");
        sb.Append("ufw allow 22/tcp\n");
        sb.Append("ufw allow 80/tcp\n");
        sb.Append("ufw allow 443/tcp\n");
        sb.Append("ufw allow 443/udp\n");
        sb.Append("ufw --force enable\n");
        sb.Append("\n");

        // Automatic security updates (DEBIAN_FRONTEND=noninteractive avoids whiptail/dialog)
        sb.Append("DEBIAN_FRONTEND=noninteractive apt-get install -y -qq unattended-upgrades\n");
        sb.Append("DEBIAN_FRONTEND=noninteractive dpkg-reconfigure -plow unattended-upgrades\n");
        sb.Append("\n");

        // Clone the repo (with retry for transient network issues)
        sb.Append("echo 'Cloning repository...'\n");
        var branchArg = string.IsNullOrEmpty(cfg.RepoBranch) ? "" : $" -b {cfg.RepoBranch}";
        sb.Append("for attempt in 1 2 3; do\n");
        sb.Append($"  if git clone{branchArg} {cfg.RepoUrl} /opt/pretalxtix; then\n");
        sb.Append("    echo 'Repository cloned successfully.'\n");
        sb.Append("    break\n");
        sb.Append("  fi\n");
        sb.Append("  if [ \"$attempt\" -eq 3 ]; then echo 'ERROR: Git clone failed after 3 attempts'; exit 1; fi\n");
        sb.Append("  echo \"Git clone failed, retrying in 10s... (attempt $attempt/3)\"\n");
        sb.Append("  sleep 10\n");
        sb.Append("done\n");
        sb.Append("\n");

        // Move .env file into place
        sb.Append("mv /opt/pretalxtix-env /opt/pretalxtix/.env\n");
        sb.Append("\n");

        // Start services
        sb.Append("echo 'Starting services...'\n");
        sb.Append("cd /opt/pretalxtix\n");
        sb.Append("docker compose up -d\n");
        sb.Append("\n");

        // Wait for PostgreSQL
        sb.Append("echo 'Waiting for PostgreSQL...'\n");
        sb.Append("for i in $(seq 1 90); do\n");
        sb.Append($"  if docker compose exec -T postgres pg_isready -U {dbUser} >/dev/null 2>&1; then\n");
        sb.Append("    echo 'PostgreSQL is ready.'\n");
        sb.Append("    break\n");
        sb.Append("  fi\n");
        sb.Append("  if [ \"$i\" -eq 90 ]; then echo 'ERROR: PostgreSQL timeout'; exit 1; fi\n");
        sb.Append("  echo \"Waiting for PostgreSQL... ($i/90)\"\n");
        sb.Append("  sleep 5\n");
        sb.Append("done\n");
        sb.Append("\n");

        // Wait for init-db.sh to create databases (healthcheck passes before init scripts complete)
        sb.Append("echo 'Waiting for databases to be created...'\n");
        sb.Append("for i in $(seq 1 60); do\n");
        sb.Append($"  PRETIX_EXISTS=$(docker compose exec -T postgres psql -U {dbUser} -tAc \"SELECT 1 FROM pg_database WHERE datname='pretix'\" 2>/dev/null || true)\n");
        sb.Append($"  PRETALX_EXISTS=$(docker compose exec -T postgres psql -U {dbUser} -tAc \"SELECT 1 FROM pg_database WHERE datname='pretalx'\" 2>/dev/null || true)\n");
        sb.Append("  if [ \"$PRETIX_EXISTS\" = \"1\" ] && [ \"$PRETALX_EXISTS\" = \"1\" ]; then\n");
        sb.Append("    echo 'Databases pretix and pretalx are ready.'\n");
        sb.Append("    break\n");
        sb.Append("  fi\n");
        sb.Append("  if [ \"$i\" -eq 60 ]; then echo 'ERROR: Database creation timeout'; exit 1; fi\n");
        sb.Append("  echo \"Waiting for databases... ($i/60)\"\n");
        sb.Append("  sleep 5\n");
        sb.Append("done\n");
        sb.Append("\n");

        // Wait for containers to finish their internal migrations and become healthy
        // The pretix/pretalx standalone images run migrations automatically on startup
        // Use container running status as fallback if check command fails
        sb.Append("echo 'Waiting for pretix to be ready...'\n");
        sb.Append("for i in $(seq 1 60); do\n");
        sb.Append("  # Try pretix check command first, fall back to checking if container is running\n");
        sb.Append("  if docker compose exec -T pretix pretix check >/dev/null 2>&1; then\n");
        sb.Append("    echo 'Pretix is ready (check passed).'\n");
        sb.Append("    break\n");
        sb.Append("  elif [ \"$i\" -gt 30 ] && docker compose ps pretix --format '{{.State}}' 2>/dev/null | grep -q 'running'; then\n");
        sb.Append("    echo 'Pretix container is running (check command unavailable).'\n");
        sb.Append("    break\n");
        sb.Append("  fi\n");
        sb.Append("  if [ \"$i\" -eq 60 ]; then echo 'WARNING: Pretix readiness timeout, continuing...'; fi\n");
        sb.Append("  echo \"Waiting for pretix... ($i/60)\"\n");
        sb.Append("  sleep 10\n");
        sb.Append("done\n");
        sb.Append("\n");
        sb.Append("echo 'Waiting for pretalx to be ready...'\n");
        sb.Append("for i in $(seq 1 60); do\n");
        sb.Append("  # Try pretalx check command first, fall back to checking if container is running\n");
        sb.Append("  if docker compose exec -T pretalx pretalx check >/dev/null 2>&1; then\n");
        sb.Append("    echo 'Pretalx is ready (check passed).'\n");
        sb.Append("    break\n");
        sb.Append("  elif [ \"$i\" -gt 30 ] && docker compose ps pretalx --format '{{.State}}' 2>/dev/null | grep -q 'running'; then\n");
        sb.Append("    echo 'Pretalx container is running (check command unavailable).'\n");
        sb.Append("    break\n");
        sb.Append("  fi\n");
        sb.Append("  if [ \"$i\" -eq 60 ]; then echo 'WARNING: Pretalx readiness timeout, continuing...'; fi\n");
        sb.Append("  echo \"Waiting for pretalx... ($i/60)\"\n");
        sb.Append("  sleep 10\n");
        sb.Append("done\n");
        sb.Append("\n");

        // Note: pretix/pretalx standalone containers handle migrations and static files automatically on startup.
        // Do NOT run 'pretix rebuild' or 'pretalx rebuild' here - it causes migration race conditions.

        // Ensure cron service is running before installing cron job
        sb.Append("echo 'Starting cron service...'\n");
        sb.Append("systemctl enable cron\n");
        sb.Append("systemctl start cron\n");
        sb.Append("\n");

        // Install backup cron (non-fatal if it fails)
        sb.Append("echo 'Installing backup cron job...'\n");
        sb.Append("bash scripts/backup.sh --install-cron || echo 'WARNING: Failed to install backup cron'\n");
        sb.Append("\n");
        sb.Append("echo '=== PreTalxTix setup complete ==='\n");

        return sb.ToString();
    }
}
