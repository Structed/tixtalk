using System.Text;
using Pulumi;

namespace PreTalxTix.Infra.Helpers;

public record CloudInitConfig
{
    public required string RepoUrl { get; init; }
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

        // Configure firewall
        sb.Append("echo 'Configuring firewall...'\n");
        sb.Append("ufw allow 22/tcp\n");
        sb.Append("ufw allow 80/tcp\n");
        sb.Append("ufw allow 443/tcp\n");
        sb.Append("ufw allow 443/udp\n");
        sb.Append("ufw --force enable\n");
        sb.Append("\n");

        // Automatic security updates
        sb.Append("apt-get install -y -qq unattended-upgrades\n");
        sb.Append("dpkg-reconfigure -plow unattended-upgrades\n");
        sb.Append("\n");

        // Clone the repo
        sb.Append("echo 'Cloning repository...'\n");
        sb.Append($"git clone {cfg.RepoUrl} /opt/pretalxtix\n");
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
        sb.Append("sleep 10\n");
        sb.Append("\n");

        // Run migrations
        sb.Append("echo 'Running pretix migrations...'\n");
        sb.Append("docker compose exec -T pretix pretix migrate\n");
        sb.Append("docker compose exec -T pretix pretix rebuild\n");
        sb.Append("\n");
        sb.Append("echo 'Running pretalx migrations...'\n");
        sb.Append("docker compose exec -T pretalx pretalx migrate\n");
        sb.Append("docker compose exec -T pretalx pretalx rebuild\n");
        sb.Append("\n");

        // Install backup cron
        sb.Append("echo 'Installing backup cron job...'\n");
        sb.Append("bash scripts/backup.sh --install-cron\n");
        sb.Append("\n");
        sb.Append("echo '=== PreTalxTix setup complete ==='\n");

        return sb.ToString();
    }
}
