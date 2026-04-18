using System.Diagnostics;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace PreTalxTix.Cli;

public static partial class Provision
{
    private static readonly string InfraDir = Path.Combine(
        FindRepoRoot() ?? ".", "infra");

    // Validation patterns
    [GeneratedRegex(@"^[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?)*\.[a-zA-Z]{2,}$")]
    private static partial Regex DomainRegex();
    
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();
    
    [GeneratedRegex(@"^ssh-(rsa|ed25519|ecdsa|dss)\s+[A-Za-z0-9+/=]+")]
    private static partial Regex SshKeyRegex();

    public static int Run()
    {
        AnsiConsole.Write(new Rule("[blue]Provision Azure VM[/]").RuleStyle("blue"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("This wizard will create an Azure VM and deploy pretix + pretalx.");
        AnsiConsole.MarkupLine("Prerequisites: [yellow]Pulumi CLI[/], [yellow].NET 8+ SDK[/], [yellow]Azure CLI[/] (logged in)");
        AnsiConsole.WriteLine();

        // Check prerequisites
        if (!CheckPrerequisite("pulumi", "--version", "Pulumi CLI"))
            return 1;

        // Gather configuration with validation
        var domain = AnsiConsole.Prompt(
            new TextPrompt<string>("Your [green]domain[/] (e.g., yourdomain.com):")
                .Validate(d => 
                {
                    if (string.IsNullOrWhiteSpace(d))
                        return ValidationResult.Error("[red]Domain cannot be empty[/]");
                    if (!DomainRegex().IsMatch(d))
                        return ValidationResult.Error("[red]Invalid domain format. Example: yourdomain.com[/]");
                    return ValidationResult.Success();
                }));

        // Derive a resource name prefix from the domain (e.g., "godotfest.org" → "godotfest")
        var defaultPrefix = domain.Split('.')[0].ToLowerInvariant();
        defaultPrefix = Regex.Replace(defaultPrefix, "[^a-z0-9-]", "");
        var prefix = AnsiConsole.Ask("Azure resource name [green]prefix[/]:", defaultPrefix);

        var sshKeyPath = DetectSshKey();
        sshKeyPath = AnsiConsole.Ask("SSH public key file:", sshKeyPath);
        var sshPublicKey = ReadSshPublicKey(sshKeyPath);
        if (sshPublicKey == null) return 1;
        
        // Validate SSH key format
        if (!SshKeyRegex().IsMatch(sshPublicKey))
        {
            AnsiConsole.MarkupLine("[red]Invalid SSH public key format.[/]");
            AnsiConsole.MarkupLine("[grey]Expected format: ssh-rsa AAAA... or ssh-ed25519 AAAA...[/]");
            return 1;
        }

        var region = AnsiConsole.Ask("Azure [green]region[/]:", "westeurope");

        var vmSize = AnsiConsole.Ask("VM size:", "Standard_B2s");

        // Admin account (for pretix/pretalx control panels) with validation
        var adminEmail = AnsiConsole.Prompt(
            new TextPrompt<string>("Admin [green]email[/] (for pretix/pretalx login):")
                .Validate(e =>
                {
                    if (string.IsNullOrWhiteSpace(e))
                        return ValidationResult.Error("[red]Email cannot be empty[/]");
                    if (!EmailRegex().IsMatch(e))
                        return ValidationResult.Error("[red]Invalid email format[/]");
                    return ValidationResult.Success();
                }));

        // Email configuration - Azure Communication Services is the recommended default
        var useAzureMail = AnsiConsole.Confirm(
            "Use [green]Azure Communication Services[/] for email? (Recommended)", true);
        
        string smtpHost = "", smtpUser = "", smtpPassword = "", mailFrom = "";
        int smtpPort = 587;
        bool acsUseCustomDomain = false;
        
        // Track Cloudflare config - may be set by ACS custom domain requirement
        string cfToken = "", cfZoneId = "";
        bool cfDnsChallenge = false;
        bool configureCloudflare = false;
        
        if (useAzureMail)
        {
            // ACS domain type selection
            var domainChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("ACS email domain type:")
                    .AddChoices(new[] {
                        $"Custom domain (noreply@{domain})",
                        "Azure-managed (noreply@xxx.azurecomm.net)"
                    }));
            
            acsUseCustomDomain = domainChoice.StartsWith("Custom");
            
            if (acsUseCustomDomain)
            {
                // Warn about the one-domain-per-ACS-instance Azure limitation
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Panel(
                    "[yellow]Azure enforces that a custom domain can only belong to\n" +
                    "one ACS Email Service at a time — globally across all\n" +
                    "subscriptions and tenants.[/]\n\n" +
                    $"If [green]{domain}[/] is already registered with another ACS\n" +
                    "Email Service, provisioning will fail. You must disconnect\n" +
                    "it from the other ACS first (Azure Portal → Communication\n" +
                    "Services → Email → Domains → Disconnect).")
                    .Header("[red]⚠ Custom Domain Limitation[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Yellow));
                AnsiConsole.WriteLine();

                if (!AnsiConsole.Confirm($"Is [green]{domain}[/] available (not used by another ACS)?", true))
                {
                    // Let the user fall back to Azure-managed domain
                    AnsiConsole.MarkupLine("[grey]Switching to Azure-managed domain instead.[/]");
                    acsUseCustomDomain = false;
                }
            }

            if (acsUseCustomDomain)
            {
                // Custom domain requires Cloudflare for DNS automation
                AnsiConsole.MarkupLine("[yellow]Custom domain requires Cloudflare for DNS automation.[/]");
                cfToken = AnsiConsole.Prompt(
                    new TextPrompt<string>("Cloudflare API token:").Secret());
                cfZoneId = AnsiConsole.Ask<string>("Cloudflare Zone ID:");
                cfDnsChallenge = AnsiConsole.Confirm("Use DNS challenge for TLS (orange-cloud proxy)?", false);
                configureCloudflare = true;
            }
        }
        else
        {
            // Manual SMTP configuration
            smtpHost = AnsiConsole.Ask("SMTP host:", "smtp.azurecomm.net");
            smtpPort = AnsiConsole.Ask("SMTP port:", 587);
            smtpUser = AnsiConsole.Ask<string>("SMTP user:");
            smtpPassword = AnsiConsole.Prompt(
                new TextPrompt<string>("SMTP password:").Secret());
            mailFrom = AnsiConsole.Ask("Mail from address:", $"noreply@{domain}");
        }

        // Optional: Cloudflare (if not already configured via ACS custom domain)
        if (!configureCloudflare)
        {
            configureCloudflare = AnsiConsole.Confirm("Configure Cloudflare DNS automation?", false);
            if (configureCloudflare)
            {
                cfToken = AnsiConsole.Prompt(
                    new TextPrompt<string>("Cloudflare API token:").Secret());
                cfZoneId = AnsiConsole.Ask<string>("Cloudflare Zone ID:");
                cfDnsChallenge = AnsiConsole.Confirm("Use DNS challenge for TLS (orange-cloud proxy)?", false);
            }
        }

        // Summary
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[yellow]Summary[/]").RuleStyle("yellow"));

        var summaryTable = new Table().Border(TableBorder.Rounded);
        summaryTable.AddColumn("Setting");
        summaryTable.AddColumn("Value");
        summaryTable.AddRow("Prefix", prefix);
        summaryTable.AddRow("Domain", domain);
        summaryTable.AddRow("Region", region);
        summaryTable.AddRow("VM Size", vmSize);
        summaryTable.AddRow("SSH Key", sshKeyPath);
        summaryTable.AddRow("Admin Email", adminEmail);
        
        if (useAzureMail)
        {
            var emailDomainType = acsUseCustomDomain ? $"custom ({domain})" : "Azure-managed";
            summaryTable.AddRow("Email", $"[green]Azure Communication Services[/] ({emailDomainType})");
        }
        else
        {
            summaryTable.AddRow("Email", $"SMTP: {smtpHost}");
        }
        
        summaryTable.AddRow("Cloudflare", configureCloudflare ? "enabled" : "[grey]not configured[/]");
        AnsiConsole.Write(summaryTable);

        AnsiConsole.WriteLine();
        if (!AnsiConsole.Confirm("Proceed with provisioning?", true))
        {
            AnsiConsole.MarkupLine("[yellow]Aborted.[/]");
            return 0;
        }

        // Initialize Pulumi stack
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]Step 1/3: Configuring Pulumi[/]").RuleStyle("blue"));

        if (RunPulumi("stack init dev --non-interactive", allowFailure: true) != 0)
        {
            // Stack may already exist — select it
            RunPulumi("stack select dev");
        }

        // Set config values
        SetConfig("azure-native:location", region);
        SetConfig("pre-talx-tix:prefix", prefix);
        SetConfig("pre-talx-tix:domain", domain);
        SetConfig("pre-talx-tix:sshPublicKey", sshPublicKey);
        SetConfig("pre-talx-tix:vmSize", vmSize);
        SetConfig("pre-talx-tix:adminEmail", adminEmail);
        
        // Email configuration
        SetConfig("pre-talx-tix:useAzureMail", useAzureMail.ToString().ToLower());
        if (useAzureMail)
        {
            SetConfig("pre-talx-tix:acsUseCustomDomain", acsUseCustomDomain.ToString().ToLower());
        }
        
        if (!useAzureMail)
        {
            // Manual SMTP configuration
            SetConfig("pre-talx-tix:smtpHost", smtpHost);
            SetConfig("pre-talx-tix:smtpPort", smtpPort.ToString());
            SetConfig("pre-talx-tix:smtpUser", smtpUser);
            SetConfig("pre-talx-tix:smtpPassword", smtpPassword, secret: true);
            SetConfig("pre-talx-tix:mailFrom", mailFrom);
        }

        if (configureCloudflare)
        {
            SetConfig("pre-talx-tix:cloudflareApiToken", cfToken, secret: true);
            SetConfig("pre-talx-tix:cloudflareZoneId", cfZoneId);
            if (cfDnsChallenge)
                SetConfig("pre-talx-tix:cloudflareDnsChallenge", "true");
        }

        // Run pulumi up
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]Step 2/3: Provisioning Azure resources[/]").RuleStyle("blue"));
        AnsiConsole.MarkupLine("[grey]This creates the VM, networking, and starts cloud-init...[/]");
        AnsiConsole.WriteLine();

        var pulumiResult = RunPulumi("up --yes");
        if (pulumiResult != 0)
        {
            AnsiConsole.MarkupLine("[red]Pulumi deployment failed.[/] Check the output above.");

            if (acsUseCustomDomain)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Panel(
                    $"If the error mentions domain registration or [green]{domain}[/],\n" +
                    "the domain is likely already linked to another ACS Email\n" +
                    "Service. To fix this:\n\n" +
                    "  1. Remove the domain from the other ACS instance\n" +
                    "     (Portal → Communication Services → Email → Domains)\n" +
                    "  2. Re-run [yellow]ptx provision[/]\n\n" +
                    "Or retry with [green]Azure-managed domain[/] to avoid the conflict.")
                    .Header("[yellow]Possible cause: ACS domain conflict[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Yellow));
            }

            return 1;
        }

        // Get outputs
        var vmIp = GetPulumiOutput("vmPublicIp");

        // Configure ptx CLI to connect to the new VM
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]Step 3/3: Connecting CLI to new server[/]").RuleStyle("blue"));

        if (!string.IsNullOrWhiteSpace(vmIp))
        {
            var config = AppConfig.Load();
            config.Host = $"azureuser@{vmIp}";
            config.ProjectDir = "/opt/pretalxtix";

            // Try to find the private key matching the public key
            var privateKeyPath = sshKeyPath.Replace(".pub", "");
            if (File.Exists(privateKeyPath))
                config.KeyFile = privateKeyPath;
            
            // Save Azure resource info for SSH access control
            var resourceGroupName = GetPulumiOutput("resourceGroupName");
            if (!string.IsNullOrWhiteSpace(resourceGroupName))
            {
                config.ResourceGroup = resourceGroupName;
                config.NsgName = $"{prefix}-nsg";
                AnsiConsole.MarkupLine($"[green]✓[/] Azure NSG info saved for SSH access control");
            }

            config.Save();
            AnsiConsole.MarkupLine($"[green]✓[/] CLI configured to connect to [yellow]azureuser@{vmIp}[/]");
        }

        // Print final summary
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]Provisioning complete![/]").RuleStyle("green"));
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[grey]The VM is running cloud-init which will:[/]");
        AnsiConsole.MarkupLine("  1. Install Docker");
        AnsiConsole.MarkupLine("  2. Clone the repo and write .env");
        AnsiConsole.MarkupLine("  3. Start all services (docker compose up)");
        AnsiConsole.MarkupLine("  4. Run database migrations");
        AnsiConsole.MarkupLine("  5. Set up daily backups");
        if (useAzureMail && configureCloudflare)
        {
            AnsiConsole.MarkupLine("  6. Configure ACS email DNS records");
        }
        AnsiConsole.MarkupLine("[grey]This takes ~5 minutes. You can monitor with:[/]");
        AnsiConsole.MarkupLine($"  [yellow]ssh azureuser@{vmIp} tail -f /var/log/cloud-init-output.log[/]");
        AnsiConsole.WriteLine();

        if (!string.IsNullOrWhiteSpace(vmIp))
        {
            AnsiConsole.MarkupLine($"  [green]VM IP:[/]   {vmIp}");
            AnsiConsole.MarkupLine($"  [green]SSH:[/]     ssh azureuser@{vmIp}");
        }

        AnsiConsole.MarkupLine($"  [green]Pretix:[/]  https://tickets.{domain}");
        AnsiConsole.MarkupLine($"  [green]Pretalx:[/] https://talks.{domain}");
        AnsiConsole.WriteLine();
        
        // Email configuration info
        if (useAzureMail)
        {
            var emailMailFrom = GetPulumiOutput("mailFrom");
            AnsiConsole.Write(new Rule("[blue]Email Configuration[/]").RuleStyle("blue"));
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("  [green]Provider:[/] Azure Communication Services");
            if (!string.IsNullOrWhiteSpace(emailMailFrom))
            {
                AnsiConsole.MarkupLine($"  [green]From:[/]     {emailMailFrom}");
            }
            if (configureCloudflare)
            {
                AnsiConsole.MarkupLine($"  [green]Domain:[/]   Custom ({domain})");
                AnsiConsole.MarkupLine("[grey]  DNS records will be auto-created via Cloudflare.[/]");
                AnsiConsole.MarkupLine("[grey]  Note: Domain verification may take a few minutes.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("  [green]Domain:[/]   Azure-managed");
            }
            AnsiConsole.WriteLine();
        }

        // Display admin credentials
        var adminPassword = GetPulumiOutput("adminPassword", showSecrets: true);
        if (!string.IsNullOrWhiteSpace(adminPassword))
        {
            AnsiConsole.Write(new Rule("[yellow]Admin Credentials[/]").RuleStyle("yellow"));
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"  [green]Email:[/]    {adminEmail}");
            AnsiConsole.MarkupLine($"  [green]Password:[/] {adminPassword}");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"  [green]Pretix Admin:[/]  https://tickets.{domain}/control/");
            AnsiConsole.MarkupLine($"  [green]Pretalx Admin:[/] https://talks.{domain}/orga/");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Save these credentials! They are stored encrypted in Pulumi state.[/]");
            AnsiConsole.MarkupLine("[grey]Retrieve later with: pulumi stack output adminPassword --show-secrets[/]");
            AnsiConsole.WriteLine();
        }

        if (!configureCloudflare)
        {
            AnsiConsole.MarkupLine("[yellow]Don't forget to set up DNS:[/]");
            AnsiConsole.MarkupLine($"  tickets.{domain} → {vmIp}");
            AnsiConsole.MarkupLine($"  talks.{domain}   → {vmIp}");
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine("Once cloud-init finishes, manage your server with:");
        AnsiConsole.MarkupLine("  [yellow]ptx status[/]     — check service health");
        AnsiConsole.MarkupLine("  [yellow]ptx logs[/]       — view logs");
        AnsiConsole.MarkupLine("  [yellow]ptx update[/]     — update container images");
        AnsiConsole.MarkupLine("  [yellow]ptx backup[/]     — manual backup");

        return 0;
    }

    private static bool CheckPrerequisite(string command, string args, string name)
    {
        try
        {
            var psi = new ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            return true;
        }
        catch
        {
            AnsiConsole.MarkupLine($"[red]{name} not found.[/] Please install it first:");
            AnsiConsole.MarkupLine($"  [blue]https://www.pulumi.com/docs/install/[/]");
            return false;
        }
    }

    private static string DetectSshKey()
    {
        var sshDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

        foreach (var name in new[] { "id_ed25519.pub", "id_rsa.pub", "id_ecdsa.pub" })
        {
            var path = Path.Combine(sshDir, name);
            if (File.Exists(path))
                return path;
        }

        return Path.Combine(sshDir, "id_rsa.pub");
    }

    private static string? ReadSshPublicKey(string path)
    {
        var expanded = path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

        if (!File.Exists(expanded))
        {
            AnsiConsole.MarkupLine($"[red]SSH public key not found:[/] {expanded}");
            AnsiConsole.MarkupLine("Generate one with: [yellow]ssh-keygen -t ed25519[/]");
            return null;
        }

        return File.ReadAllText(expanded).Trim();
    }

    private static int RunPulumi(string args, bool allowFailure = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pulumi",
            WorkingDirectory = InfraDir,
            UseShellExecute = false,
        };

        foreach (var arg in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(arg);

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                if (!allowFailure)
                    AnsiConsole.MarkupLine("[red]Failed to start pulumi.[/]");
                return 1;
            }

            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            if (!allowFailure)
                AnsiConsole.MarkupLine($"[red]Pulumi error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static void SetConfig(string key, string value, bool secret = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pulumi",
            WorkingDirectory = InfraDir,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("config");
        psi.ArgumentList.Add("set");
        psi.ArgumentList.Add(key);

        if (secret)
        {
            psi.ArgumentList.Add("--secret");
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.StandardInput.Write(value);
                proc.StandardInput.Close();
                proc.WaitForExit();
            }
        }
        else
        {
            // Value as a separate argument — handles spaces (e.g., SSH public keys)
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(value);

            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
    }

    private static string GetPulumiOutput(string outputName, bool showSecrets = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pulumi",
            WorkingDirectory = InfraDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("stack");
        psi.ArgumentList.Add("output");
        psi.ArgumentList.Add(outputName);
        if (showSecrets)
            psi.ArgumentList.Add("--show-secrets");

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return "";
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            return output;
        }
        catch
        {
            return "";
        }
    }

    private static string? FindRepoRoot()
    {
        // Walk up from the executing assembly location to find the repo root (contains infra/)
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "infra")))
                return dir;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        // Fallback: try current working directory
        dir = Directory.GetCurrentDirectory();
        for (var i = 0; i < 5; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "infra")))
                return dir;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        return null;
    }
}
