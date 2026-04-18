using System.Text;
using Pulumi;

namespace TixTalk.Infra.Helpers;

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
    // SMTP config - can be Output<string> for ACS-generated values
    public Output<string> SmtpHost { get; init; } = Output.Create("");
    public int SmtpPort { get; init; } = 587;
    public Output<string> SmtpUser { get; init; } = Output.Create("");
    public Output<string> SmtpPassword { get; init; } = Output.Create("");
    public Output<string> MailFrom { get; init; } = Output.Create("noreply@example.com");
    // Admin superuser config
    public string AdminEmail { get; init; } = "";
    public required Output<string> AdminPassword { get; init; }
    public string OrganiserName { get; init; } = "Conference Organiser";
    public string OrganiserSlug { get; init; } = "organiser";
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
        // Combine all Output values - use nested Tuple since Tuple only supports up to 8 args
        // Group 1: Database and secrets
        var dbOutputs = Output.Tuple(cfg.DbPassword, cfg.PretixSecretKey, cfg.PretalxSecretKey, cfg.DbUser, cfg.AdminPassword);
        // Group 2: SMTP config
        var smtpOutputs = Output.Tuple(cfg.SmtpHost, cfg.SmtpUser, cfg.SmtpPassword, cfg.MailFrom);
        
        return Output.Tuple(dbOutputs, smtpOutputs)
            .Apply(t =>
            {
                var ((dbPassword, pretixSecret, pretalxSecret, dbUser, adminPassword),
                    (smtpHost, smtpUser, smtpPassword, mailFrom)) = t;
                return Generate(cfg, dbUser, dbPassword, pretixSecret, pretalxSecret, adminPassword,
                    smtpHost, smtpUser, smtpPassword, mailFrom);
            });
    }

    private static string Generate(
        CloudInitConfig cfg,
        string dbUser,
        string dbPassword,
        string pretixSecret,
        string pretalxSecret,
        string adminPassword,
        string smtpHost,
        string smtpUser,
        string smtpPassword,
        string mailFrom)
    {
        // Build .env content and base64-encode it (ensure Unix line endings)
        var envContent = BuildEnvContent(cfg, dbUser, dbPassword, pretixSecret, pretalxSecret,
            smtpHost, smtpUser, smtpPassword, mailFrom);
        var envBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(envContent.Replace("\r\n", "\n")));

        // Build setup script and base64-encode it (ensure Unix line endings)
        var setupScript = BuildSetupScript(cfg, dbUser, adminPassword);
        var scriptBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(setupScript.Replace("\r\n", "\n")));

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
        sb.Append("  - path: /opt/tixtalk-env\n");
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
        string pretalxSecret,
        string smtpHost,
        string smtpUser,
        string smtpPassword,
        string mailFrom)
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
        sb.Append($"MAIL_FROM={mailFrom}\n");
        sb.Append($"SMTP_HOST={smtpHost}\n");
        sb.Append($"SMTP_PORT={cfg.SmtpPort}\n");
        sb.Append($"SMTP_USER={smtpUser}\n");
        sb.Append($"SMTP_PASSWORD={smtpPassword}\n");
        return sb.ToString();
    }

    private static string BuildSetupScript(CloudInitConfig cfg, string dbUser, string adminPassword)
    {
        var sb = new StringBuilder();
        sb.Append("#!/bin/bash\n");
        sb.Append("set -euo pipefail\n");
        sb.Append("exec > >(tee -a /var/log/tixtalk-setup.log) 2>&1\n");
        sb.Append("echo '=== tixtalk setup started ==='\n");
        sb.Append("\n");
        
        // Define helper functions at the top for reuse
        sb.Append(@"# Helper function: wait for a condition with retries
# Usage: wait_for <name> <check_command> [max_attempts] [sleep_seconds]
wait_for() {
    local name=""$1"" cmd=""$2"" max=""${3:-30}"" sleep_secs=""${4:-5}""
    echo ""Waiting for $name...""
    for i in $(seq 1 ""$max""); do
        if eval ""$cmd"" >/dev/null 2>&1; then
            echo ""$name is ready.""
            return 0
        fi
        if [ ""$i"" -eq ""$max"" ]; then
            echo ""ERROR: $name timeout after $((max * sleep_secs)) seconds""
            return 1
        fi
        echo ""  Waiting for $name... ($i/$max)""
        sleep ""$sleep_secs""
    done
}

# Helper function: retry a command
# Usage: retry <name> <command> [max_attempts] [sleep_seconds]
retry() {
    local name=""$1"" cmd=""$2"" max=""${3:-3}"" sleep_secs=""${4:-10}""
    for attempt in $(seq 1 ""$max""); do
        if eval ""$cmd""; then
            return 0
        fi
        if [ ""$attempt"" -eq ""$max"" ]; then
            echo ""ERROR: $name failed after $max attempts""
            return 1
        fi
        echo ""$name failed, retrying in ${sleep_secs}s... (attempt $attempt/$max)""
        sleep ""$sleep_secs""
    done
}

");

        // Install Docker
        sb.Append("echo 'Installing Docker...'\n");
        sb.Append("curl -fsSL https://get.docker.com | sh\n");
        sb.Append("systemctl enable docker\n");
        sb.Append("systemctl start docker\n");
        sb.Append("\n");

        // Add azureuser to docker group (so they can run docker without sudo)
        sb.Append("usermod -aG docker azureuser\n");
        sb.Append("\n");

        // Verify Docker is ready (using helper)
        sb.Append("wait_for 'Docker' 'docker info' 30 2 || exit 1\n");
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

        // Clone the repo (using retry helper)
        sb.Append("echo 'Cloning repository...'\n");
        var branchArg = string.IsNullOrEmpty(cfg.RepoBranch) ? "" : $" -b {cfg.RepoBranch}";
        sb.Append($"retry 'Git clone' 'git clone{branchArg} {cfg.RepoUrl} /opt/tixtalk' 3 10 || exit 1\n");
        sb.Append("\n");

        // Move .env file into place
        sb.Append("mv /opt/tixtalk-env /opt/tixtalk/.env\n");
        sb.Append("\n");

        // DNS records are now managed by Pulumi (CloudflareDnsStack) instead of cloud-init,
        // so they are automatically cleaned up on `pulumi destroy`.

        // Configure sysctl for Redis (avoid background save failures)
        sb.Append("echo 'Configuring system for Redis...'\n");
        sb.Append("sysctl vm.overcommit_memory=1\n");
        sb.Append("echo 'vm.overcommit_memory = 1' >> /etc/sysctl.conf\n");
        sb.Append("\n");

        // Start services - use DNS challenge compose file if configured
        sb.Append("echo 'Starting services...'\n");
        sb.Append("cd /opt/tixtalk\n");
        if (cfg.CloudflareDnsChallenge == "true")
        {
            sb.Append("docker compose -f docker-compose.yml -f docker-compose.cloudflare.yml up -d --build\n");
        }
        else
        {
            sb.Append("docker compose up -d\n");
        }
        sb.Append("\n");

        // Wait for PostgreSQL (using helper function)
        sb.Append($"wait_for 'PostgreSQL' 'docker compose exec -T postgres pg_isready -U {dbUser}' 90 5 || exit 1\n");
        sb.Append("\n");

        // Wait for databases to be created (custom check - init-db.sh may run after healthcheck passes)
        sb.Append($@"echo 'Waiting for databases to be created...'
wait_for 'pretix database' ""docker compose exec -T postgres psql -U {dbUser} -tAc \""SELECT 1 FROM pg_database WHERE datname='pretix'\"" | grep -q 1"" 60 5 || exit 1
wait_for 'pretalx database' ""docker compose exec -T postgres psql -U {dbUser} -tAc \""SELECT 1 FROM pg_database WHERE datname='pretalx'\"" | grep -q 1"" 60 5 || exit 1
");
        sb.Append("\n");

        // Wait for app containers (with fallback to container running state)
        sb.Append(@"# Wait for pretix with fallback to running state
echo 'Waiting for pretix to be ready...'
for i in $(seq 1 60); do
    if docker compose exec -T pretix pretix check >/dev/null 2>&1; then
        echo 'Pretix is ready (check passed).'
        break
    elif [ ""$i"" -gt 30 ] && docker compose ps pretix --format '{{.State}}' 2>/dev/null | grep -q 'running'; then
        echo 'Pretix container is running (check command unavailable).'
        break
    fi
    [ ""$i"" -eq 60 ] && echo 'WARNING: Pretix readiness timeout, continuing...'
    echo ""  Waiting for pretix... ($i/60)""
    sleep 10
done

# Wait for pretalx with fallback to running state
echo 'Waiting for pretalx to be ready...'
for i in $(seq 1 60); do
    if docker compose exec -T pretalx pretalx check >/dev/null 2>&1; then
        echo 'Pretalx is ready (check passed).'
        break
    elif [ ""$i"" -gt 30 ] && docker compose ps pretalx --format '{{.State}}' 2>/dev/null | grep -q 'running'; then
        echo 'Pretalx container is running (check command unavailable).'
        break
    fi
    [ ""$i"" -eq 60 ] && echo 'WARNING: Pretalx readiness timeout, continuing...'
    echo ""  Waiting for pretalx... ($i/60)""
    sleep 10
done
");
        sb.Append("\n");

        // Note: pretix/pretalx standalone containers handle migrations and static files automatically on startup.
        // Do NOT run 'pretix rebuild' or 'pretalx rebuild' here - it causes migration race conditions.

        // Wait for migrations to complete before creating superuser accounts
        sb.Append(@"# Wait for pretix migrations to finish (standalone container runs them on startup)
echo 'Waiting for pretix migrations to complete...'
for i in $(seq 1 30); do
    if docker compose exec -T pretix pretix migrate --check >/dev/null 2>&1; then
        echo 'Pretix migrations complete.'
        break
    fi
    [ ""$i"" -eq 30 ] && echo 'WARNING: Pretix migration timeout, continuing...'
    echo ""  Waiting for pretix migrations... ($i/30)""
    sleep 20
done

# Wait for pretalx migrations to finish
echo 'Waiting for pretalx migrations to complete...'
for i in $(seq 1 30); do
    if docker compose exec -T pretalx pretalx migrate --check >/dev/null 2>&1; then
        echo 'Pretalx migrations complete.'
        break
    fi
    [ ""$i"" -eq 30 ] && echo 'WARNING: Pretalx migration timeout, continuing...'
    echo ""  Waiting for pretalx migrations... ($i/30)""
    sleep 20
done
");
        sb.Append("\n");

        // Create superuser accounts if admin email is configured
        if (!string.IsNullOrEmpty(cfg.AdminEmail))
        {
            sb.Append("echo 'Creating admin superuser accounts...'\n");
            sb.Append("\n");

            // Define creation functions to avoid eval quoting issues with retry()
            sb.Append($@"create_pretix_admin() {{
    docker compose exec -T \
        -e DJANGO_SUPERUSER_EMAIL='{cfg.AdminEmail}' \
        -e DJANGO_SUPERUSER_PASSWORD='{adminPassword}' \
        pretix pretix createsuperuser --noinput --email '{cfg.AdminEmail}'
}}

create_pretalx_admin() {{
    docker compose exec -T \
        -e DJANGO_SUPERUSER_EMAIL='{cfg.AdminEmail}' \
        -e DJANGO_SUPERUSER_PASSWORD='{adminPassword}' \
        -e PRETALX_INIT_ORGANISER_NAME='{cfg.OrganiserName}' \
        -e PRETALX_INIT_ORGANISER_SLUG='{cfg.OrganiserSlug}' \
        pretalx pretalx init --noinput
}}

");
            // Create Pretix superuser (with retry — migrations may still be running)
            sb.Append("echo 'Creating Pretix admin user...'\n");
            sb.Append("retry 'Pretix superuser' create_pretix_admin 3 15 || true\n");
            sb.Append("\n");
            
            // Create Pretalx superuser via init command (with retry)
            sb.Append("echo 'Creating Pretalx admin user and organiser...'\n");
            sb.Append("retry 'Pretalx init' create_pretalx_admin 3 15 || true\n");
            sb.Append("\n");

            // Verify accounts were created by querying the database
            sb.Append("echo 'Verifying admin accounts...'\n");
            sb.Append($"PRETIX_ADMIN=$(docker compose exec -T postgres psql -U {dbUser} -d pretix -tAc \"SELECT email FROM pretixbase_user WHERE email='{cfg.AdminEmail}' AND is_active=true LIMIT 1\" 2>/dev/null || true)\n");
            sb.Append($"PRETALX_ADMIN=$(docker compose exec -T postgres psql -U {dbUser} -d pretalx -tAc \"SELECT email FROM person_user WHERE email='{cfg.AdminEmail}' AND is_active=true LIMIT 1\" 2>/dev/null || true)\n");
            sb.Append("\n");
            sb.Append($"if [ \"$PRETIX_ADMIN\" = \"{cfg.AdminEmail}\" ]; then\n");
            sb.Append("  echo 'Pretix admin account verified.'\n");
            sb.Append("else\n");
            sb.Append("  echo 'ERROR: Pretix admin account was NOT created!'\n");
            sb.Append($"  echo 'Create manually: docker compose exec -T pretix pretix createsuperuser --email {cfg.AdminEmail}'\n");
            sb.Append("fi\n");
            sb.Append($"if [ \"$PRETALX_ADMIN\" = \"{cfg.AdminEmail}\" ]; then\n");
            sb.Append("  echo 'Pretalx admin account verified.'\n");
            sb.Append("else\n");
            sb.Append("  echo 'ERROR: Pretalx admin account was NOT created!'\n");
            sb.Append("  echo 'Create manually: docker compose exec -T pretalx pretalx init'\n");
            sb.Append("fi\n");
            sb.Append("\n");
        }

        // Ensure cron service is running before installing cron job
        sb.Append("echo 'Starting cron service...'\n");
        sb.Append("systemctl enable cron\n");
        sb.Append("systemctl start cron\n");
        sb.Append("\n");

        // Install backup cron (non-fatal if it fails)
        sb.Append("echo 'Installing backup cron job...'\n");
        sb.Append("bash scripts/backup.sh --install-cron || echo 'WARNING: Failed to install backup cron'\n");
        sb.Append("\n");
        sb.Append("echo '=== tixtalk setup complete ==='\n");

        return sb.ToString();
    }
}
