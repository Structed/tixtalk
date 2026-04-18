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
    // ACS DNS verification records (JSON) - for custom domains
    public Output<string>? AcsVerificationRecords { get; init; }
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
        var acsRecords = cfg.AcsVerificationRecords ?? Output.Create("");
        
        // Group 1: Database and secrets
        var dbOutputs = Output.Tuple(cfg.DbPassword, cfg.PretixSecretKey, cfg.PretalxSecretKey, cfg.DbUser, cfg.AdminPassword);
        // Group 2: SMTP config
        var smtpOutputs = Output.Tuple(cfg.SmtpHost, cfg.SmtpUser, cfg.SmtpPassword, cfg.MailFrom, acsRecords);
        
        return Output.Tuple(dbOutputs, smtpOutputs)
            .Apply(t =>
            {
                var ((dbPassword, pretixSecret, pretalxSecret, dbUser, adminPassword),
                    (smtpHost, smtpUser, smtpPassword, mailFrom, verificationRecords)) = t;
                return Generate(cfg, dbUser, dbPassword, pretixSecret, pretalxSecret, adminPassword,
                    smtpHost, smtpUser, smtpPassword, mailFrom, verificationRecords);
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
        string mailFrom,
        string acsVerificationRecords)
    {
        // Build .env content and base64-encode it (ensure Unix line endings)
        var envContent = BuildEnvContent(cfg, dbUser, dbPassword, pretixSecret, pretalxSecret,
            smtpHost, smtpUser, smtpPassword, mailFrom);
        var envBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(envContent.Replace("\r\n", "\n")));

        // Build setup script and base64-encode it (ensure Unix line endings)
        var setupScript = BuildSetupScript(cfg, dbUser, adminPassword, acsVerificationRecords);
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
        sb.Append("  - jq\n"); // For JSON parsing of ACS verification records
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

    private static string BuildSetupScript(CloudInitConfig cfg, string dbUser, string adminPassword, string acsVerificationRecords)
    {
        var sb = new StringBuilder();
        sb.Append("#!/bin/bash\n");
        sb.Append("set -euo pipefail\n");
        sb.Append("exec > >(tee -a /var/log/pretalxtix-setup.log) 2>&1\n");
        sb.Append("echo '=== PreTalxTix setup started ==='\n");
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
        sb.Append($"retry 'Git clone' 'git clone{branchArg} {cfg.RepoUrl} /opt/pretalxtix' 3 10 || exit 1\n");
        sb.Append("\n");

        // Move .env file into place
        sb.Append("mv /opt/pretalxtix-env /opt/pretalxtix/.env\n");
        sb.Append("\n");

        // Set up Cloudflare DNS records if configured
        if (!string.IsNullOrEmpty(cfg.CloudflareApiToken))
        {
            sb.Append("echo 'Setting up Cloudflare DNS records...'\n");
            sb.Append("cd /opt/pretalxtix\n");
            sb.Append("bash scripts/cloudflare-dns.sh || echo 'WARNING: Cloudflare DNS setup failed'\n");
            sb.Append("\n");
            
            // Set up ACS email DNS records for custom domain verification
            if (!string.IsNullOrEmpty(acsVerificationRecords))
            {
                sb.Append("echo 'Setting up Azure Communication Services email DNS records...'\n");
                sb.Append("source /opt/pretalxtix/.env\n");
                // Store the verification records JSON
                var escapedRecords = acsVerificationRecords.Replace("'", "'\\''");
                sb.Append($"ACS_RECORDS='{escapedRecords}'\n");
                sb.Append(@"
# Create ACS email DNS records via Cloudflare API
create_dns_record() {
    local type=$1
    local name=$2
    local content=$3
    local ttl=${4:-3600}
    
    # Check if record exists
    local existing=$(curl -s -X GET ""https://api.cloudflare.com/client/v4/zones/$CLOUDFLARE_ZONE_ID/dns_records?type=$type&name=$name"" \
        -H ""Authorization: Bearer $CLOUDFLARE_API_TOKEN"" \
        -H ""Content-Type: application/json"" | jq -r '.result[0].id // empty')
    
    if [ -n ""$existing"" ]; then
        echo ""  Updating existing $type record: $name""
        curl -s -X PUT ""https://api.cloudflare.com/client/v4/zones/$CLOUDFLARE_ZONE_ID/dns_records/$existing"" \
            -H ""Authorization: Bearer $CLOUDFLARE_API_TOKEN"" \
            -H ""Content-Type: application/json"" \
            --data '{""type"":""'""$type""'"",""name"":""'""$name""'"",""content"":""'""$content""'"",""ttl"":'""$ttl""',""proxied"":false}' > /dev/null
    else
        echo ""  Creating $type record: $name""
        curl -s -X POST ""https://api.cloudflare.com/client/v4/zones/$CLOUDFLARE_ZONE_ID/dns_records"" \
            -H ""Authorization: Bearer $CLOUDFLARE_API_TOKEN"" \
            -H ""Content-Type: application/json"" \
            --data '{""type"":""'""$type""'"",""name"":""'""$name""'"",""content"":""'""$content""'"",""ttl"":'""$ttl""',""proxied"":false}' > /dev/null
    fi
}

# Parse and create records from ACS verification JSON
if [ -n ""$ACS_RECORDS"" ] && [ ""$ACS_RECORDS"" != ""{}"" ]; then
    # Domain ownership TXT record
    DOMAIN_NAME=$(echo ""$ACS_RECORDS"" | jq -r '.domain.name // empty')
    DOMAIN_VALUE=$(echo ""$ACS_RECORDS"" | jq -r '.domain.value // empty')
    DOMAIN_TTL=$(echo ""$ACS_RECORDS"" | jq -r '.domain.ttl // 3600')
    if [ -n ""$DOMAIN_NAME"" ] && [ -n ""$DOMAIN_VALUE"" ]; then
        create_dns_record ""TXT"" ""$DOMAIN_NAME"" ""$DOMAIN_VALUE"" ""$DOMAIN_TTL""
    fi
    
    # SPF TXT record
    SPF_NAME=$(echo ""$ACS_RECORDS"" | jq -r '.spf.name // empty')
    SPF_VALUE=$(echo ""$ACS_RECORDS"" | jq -r '.spf.value // empty')
    SPF_TTL=$(echo ""$ACS_RECORDS"" | jq -r '.spf.ttl // 3600')
    if [ -n ""$SPF_NAME"" ] && [ -n ""$SPF_VALUE"" ]; then
        create_dns_record ""TXT"" ""$SPF_NAME"" ""$SPF_VALUE"" ""$SPF_TTL""
    fi
    
    # DKIM CNAME records
    DKIM_NAME=$(echo ""$ACS_RECORDS"" | jq -r '.dkim.name // empty')
    DKIM_VALUE=$(echo ""$ACS_RECORDS"" | jq -r '.dkim.value // empty')
    DKIM_TTL=$(echo ""$ACS_RECORDS"" | jq -r '.dkim.ttl // 3600')
    if [ -n ""$DKIM_NAME"" ] && [ -n ""$DKIM_VALUE"" ]; then
        create_dns_record ""CNAME"" ""$DKIM_NAME"" ""$DKIM_VALUE"" ""$DKIM_TTL""
    fi
    
    DKIM2_NAME=$(echo ""$ACS_RECORDS"" | jq -r '.dkim2.name // empty')
    DKIM2_VALUE=$(echo ""$ACS_RECORDS"" | jq -r '.dkim2.value // empty')
    DKIM2_TTL=$(echo ""$ACS_RECORDS"" | jq -r '.dkim2.ttl // 3600')
    if [ -n ""$DKIM2_NAME"" ] && [ -n ""$DKIM2_VALUE"" ]; then
        create_dns_record ""CNAME"" ""$DKIM2_NAME"" ""$DKIM2_VALUE"" ""$DKIM2_TTL""
    fi
    
    echo 'ACS email DNS records created.'
else
    echo 'No ACS verification records to create.'
fi
");
                sb.Append("\n");
            }
        }

        // Configure sysctl for Redis (avoid background save failures)
        sb.Append("echo 'Configuring system for Redis...'\n");
        sb.Append("sysctl vm.overcommit_memory=1\n");
        sb.Append("echo 'vm.overcommit_memory = 1' >> /etc/sysctl.conf\n");
        sb.Append("\n");

        // Start services - use DNS challenge compose file if configured
        sb.Append("echo 'Starting services...'\n");
        sb.Append("cd /opt/pretalxtix\n");
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

        // Create superuser accounts if admin email is configured
        if (!string.IsNullOrEmpty(cfg.AdminEmail))
        {
            sb.Append("echo 'Creating admin superuser accounts...'\n");
            sb.Append("\n");
            
            // Create Pretix superuser
            sb.Append("echo 'Creating Pretix admin user...'\n");
            sb.Append($"docker compose exec -T -e DJANGO_SUPERUSER_EMAIL='{cfg.AdminEmail}' -e DJANGO_SUPERUSER_PASSWORD='{adminPassword}' pretix pretix createsuperuser --noinput || echo 'WARNING: Pretix superuser creation failed (may already exist)'\n");
            sb.Append("\n");
            
            // Create Pretalx superuser via init command
            sb.Append("echo 'Creating Pretalx admin user and organiser...'\n");
            sb.Append($"docker compose exec -T -e DJANGO_SUPERUSER_EMAIL='{cfg.AdminEmail}' -e DJANGO_SUPERUSER_PASSWORD='{adminPassword}' -e PRETALX_INIT_ORGANISER_NAME='{cfg.OrganiserName}' -e PRETALX_INIT_ORGANISER_SLUG='{cfg.OrganiserSlug}' pretalx pretalx init --noinput || echo 'WARNING: Pretalx init failed (may already be initialized)'\n");
            sb.Append("\n");
            
            sb.Append("echo 'Admin accounts created successfully.'\n");
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
        sb.Append("echo '=== PreTalxTix setup complete ==='\n");

        return sb.ToString();
    }
}
