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
/// Builds a cloud-init YAML script that provisions the VM on first boot:
/// installs Docker, clones the repo, writes .env, and starts services.
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
        var sb = new StringBuilder();
        sb.AppendLine("#cloud-config");
        sb.AppendLine("package_update: true");
        sb.AppendLine("package_upgrade: true");
        sb.AppendLine();
        sb.AppendLine("packages:");
        sb.AppendLine("  - git");
        sb.AppendLine("  - curl");
        sb.AppendLine("  - ufw");
        sb.AppendLine();
        sb.AppendLine("runcmd:");

        // Install Docker
        sb.AppendLine("  - curl -fsSL https://get.docker.com | sh");
        sb.AppendLine("  - systemctl enable docker");
        sb.AppendLine("  - systemctl start docker");

        // Configure firewall
        sb.AppendLine("  - ufw allow 22/tcp");
        sb.AppendLine("  - ufw allow 80/tcp");
        sb.AppendLine("  - ufw allow 443/tcp");
        sb.AppendLine("  - ufw allow 443/udp");
        sb.AppendLine("  - ufw --force enable");

        // Enable automatic security updates
        sb.AppendLine("  - apt-get install -y -qq unattended-upgrades");
        sb.AppendLine("  - dpkg-reconfigure -plow unattended-upgrades");

        // Clone the repo (fail early if this doesn't work)
        sb.AppendLine($"  - git clone {cfg.RepoUrl} /opt/pretalxtix || {{ echo 'FATAL: git clone failed'; exit 1; }}");

        // Write .env file
        sb.AppendLine("  - |");
        sb.AppendLine("    cat > /opt/pretalxtix/.env << 'ENVEOF'");
        sb.AppendLine($"    DOMAIN={cfg.Domain}");
        sb.AppendLine($"    DB_USER={dbUser}");
        sb.AppendLine($"    DB_PASSWORD={dbPassword}");
        sb.AppendLine($"    PRETIX_SECRET_KEY={pretixSecret}");
        sb.AppendLine($"    PRETALX_SECRET_KEY={pretalxSecret}");
        sb.AppendLine($"    PRETIX_IMAGE_TAG={cfg.PretixImageTag}");
        sb.AppendLine($"    PRETALX_IMAGE_TAG={cfg.PretalxImageTag}");
        sb.AppendLine($"    CLOUDFLARE_API_TOKEN={cfg.CloudflareApiToken}");
        sb.AppendLine($"    CLOUDFLARE_ZONE_ID={cfg.CloudflareZoneId}");
        sb.AppendLine($"    CLOUDFLARE_DNS_CHALLENGE={cfg.CloudflareDnsChallenge}");
        sb.AppendLine($"    MAIL_FROM={cfg.MailFrom}");
        sb.AppendLine($"    SMTP_HOST={cfg.SmtpHost}");
        sb.AppendLine($"    SMTP_PORT={cfg.SmtpPort}");
        sb.AppendLine($"    SMTP_USER={cfg.SmtpUser}");
        sb.AppendLine($"    SMTP_PASSWORD={cfg.SmtpPassword}");
        sb.AppendLine("    ENVEOF");

        // Start services via docker compose
        sb.AppendLine("  - cd /opt/pretalxtix && docker compose up -d");

        // Wait for PostgreSQL to be healthy before running migrations
        sb.AppendLine("  - |");
        sb.AppendLine("    cd /opt/pretalxtix");
        sb.AppendLine("    echo 'Waiting for PostgreSQL...'");
        sb.AppendLine("    for i in $(seq 1 90); do");
        sb.AppendLine($"      if docker compose exec -T postgres pg_isready -U {dbUser} >/dev/null 2>&1; then");
        sb.AppendLine("        echo 'PostgreSQL is ready.'");
        sb.AppendLine("        break");
        sb.AppendLine("      fi");
        sb.AppendLine("      if [ $i -eq 90 ]; then echo 'ERROR: PostgreSQL failed to start within timeout'; exit 1; fi");
        sb.AppendLine("      echo \"Waiting for PostgreSQL... ($i/90)\"");
        sb.AppendLine("      sleep 5");
        sb.AppendLine("    done");
        sb.AppendLine("    sleep 10");

        // Run pretix migrations and rebuild
        sb.AppendLine("  - cd /opt/pretalxtix && docker compose exec -T pretix pretix migrate");
        sb.AppendLine("  - cd /opt/pretalxtix && docker compose exec -T pretix pretix rebuild");

        // Run pretalx migrations and rebuild
        sb.AppendLine("  - cd /opt/pretalxtix && docker compose exec -T pretalx pretalx migrate");
        sb.AppendLine("  - cd /opt/pretalxtix && docker compose exec -T pretalx pretalx rebuild");

        // Install daily backup cron job
        sb.AppendLine("  - cd /opt/pretalxtix && bash scripts/backup.sh --install-cron");

        return sb.ToString();
    }
}
