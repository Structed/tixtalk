using Pulumi;
using TixTalk.Infra.Helpers;
using TixTalk.Infra.Infrastructure;

return await Deployment.RunAsync(() =>
{
    var config = new Config();
    var prefix = config.Require("prefix");
    var domain = config.Require("domain");
    var sshPublicKey = config.Require("sshPublicKey");
    var vmSize = config.Get("vmSize") ?? "Standard_B2s";
    var environment = (config.Get("environment") ?? "prod").Trim().ToLowerInvariant();
    if (environment is not ("dev" or "prod"))
    {
        throw new ArgumentException($"Invalid value for config 'environment': '{environment}'. Allowed values are 'dev' or 'prod'.");
    }
    var pretixImageTag = config.Get("pretixImageTag") ?? "stable";
    var pretalxImageTag = config.Get("pretalxImageTag") ?? "latest";
    var subdomainPrefix = config.Get("subdomainPrefix") ?? (environment == "dev" ? "dev-" : "");
    var repoUrl = config.Get("repoUrl") ?? "https://github.com/Structed/tixtalk.git";
    var repoBranch = config.Get("repoBranch") ?? ""; // Empty = default branch

    // Optional config
    var cloudflareApiToken = config.Get("cloudflareApiToken") ?? "";
    var cloudflareZoneId = config.Get("cloudflareZoneId") ?? "";
    var cloudflareDnsChallenge = config.Get("cloudflareDnsChallenge") ?? "true";
    
    // SSH access restriction (optional - defaults to any)
    var sshAllowedCidrsJson = config.Get("sshAllowedCidrs");
    string[]? sshAllowedCidrs = null;
    if (!string.IsNullOrEmpty(sshAllowedCidrsJson))
    {
        try
        {
            sshAllowedCidrs = System.Text.Json.JsonSerializer.Deserialize<string[]>(sshAllowedCidrsJson);
        }
        catch
        {
            // If it's not valid JSON, treat it as a single CIDR
            sshAllowedCidrs = [sshAllowedCidrsJson];
        }
    }
    
    // Email configuration - ACS is the default
    var useAzureMail = config.GetBoolean("useAzureMail") ?? true;
    var acsUseCustomDomain = config.GetBoolean("acsUseCustomDomain") ?? false;
    var smtpHost = config.Get("smtpHost") ?? "";
    var smtpPort = config.GetInt32("smtpPort") ?? 587;
    var smtpUser = config.Get("smtpUser") ?? "";
    var smtpPassword = config.Get("smtpPassword") ?? "";
    var mailFrom = config.Get("mailFrom") ?? $"noreply@{domain}";

    // Fail fast: custom ACS domain requires Cloudflare for DNS automation
    if (useAzureMail && acsUseCustomDomain &&
        (string.IsNullOrEmpty(cloudflareApiToken) || string.IsNullOrEmpty(cloudflareZoneId)))
    {
        throw new ArgumentException(
            "Custom ACS domain requires Cloudflare DNS automation. " +
            "Set 'cloudflareApiToken' and 'cloudflareZoneId' config values, " +
            "or switch to Azure-managed domain (set 'acsUseCustomDomain' to false).");
    }

    // Admin superuser config
    var adminEmail = config.Get("adminEmail") ?? "";
    var organiserName = config.Get("organiserName") ?? "Conference Organiser";
    var organiserSlug = config.Get("organiserSlug") ?? "organiser";

    // 1. Resource Group
    var rg = ResourceGroupStack.Create(prefix);

    // 2. Networking (VNet, Subnet, NSG, Public IP, NIC)
    var network = NetworkStack.Create(prefix, rg, sshAllowedCidrs);

    // 3. Auto-generated secrets (encrypted in Pulumi state)
    var secrets = SecretGenerator.Create(prefix);

    // 4. Azure Communication Services (optional - for email)
    //    Phase 1: Create domain → verify DNS → Phase 2: Create service
    Output<string> finalSmtpHost = Output.Create(smtpHost);
    Output<string> finalSmtpUser = Output.Create(smtpUser);
    Output<string> finalSmtpPassword = Output.Create(smtpPassword);
    Output<string> finalMailFrom = Output.Create(mailFrom);
    
    // Get the subscription ID for any az CLI commands (ensures they target the same subscription as Pulumi)
    var clientConfig = Pulumi.AzureNative.Authorization.GetClientConfig.Invoke();
    
    AzureCommunicationResult? acsResult = null;
    if (useAzureMail)
    {
        // Phase 1: Create Email Service + Domain + SenderUsername
        var domainResult = AzureCommunicationStack.CreateDomain(new AzureCommunicationArgs
        {
            Prefix = prefix,
            Domain = domain,
            ResourceGroup = rg,
            UseCustomDomain = acsUseCustomDomain,
        });

        InputList<Resource>? serviceDependsOn = null;

        if (acsUseCustomDomain)
        {
            // Create ACS verification DNS records (top-level, proper dependency tracking)
            var acsVerificationDns = CloudflareDnsStack.CreateAcsVerificationRecords(
                new CloudflareAcsVerificationArgs
                {
                    Prefix = prefix,
                    CloudflareApiToken = cloudflareApiToken,
                    CloudflareZoneId = cloudflareZoneId,
                    DomainRecord = domainResult.Domain.VerificationRecords.Apply(vr => vr?.Domain),
                    SpfRecord = domainResult.Domain.VerificationRecords.Apply(vr => vr?.SPF),
                    DkimRecord = domainResult.Domain.VerificationRecords.Apply(vr => vr?.DKIM),
                    Dkim2Record = domainResult.Domain.VerificationRecords.Apply(vr => vr?.DKIM2),
                });

            // Initiate and wait for domain verification (depends on DNS records)
            var verificationCmd = DomainVerificationCommand.Create(new DomainVerificationArgs
            {
                Prefix = prefix,
                DomainName = domain,
                EmailServiceName = domainResult.EmailService.Name,
                ResourceGroupName = rg.Name,
                SubscriptionId = clientConfig.Apply(c => c.SubscriptionId),
                DependsOn = acsVerificationDns.DnsRecords.Cast<Resource>().ToArray(),
            });

            serviceDependsOn = new InputList<Resource> { verificationCmd };
        }

        // Phase 2: Create CommunicationService (with LinkedDomains) + SMTP creds
        // For custom domains, this waits for verification to complete
        acsResult = AzureCommunicationStack.CreateService(new AzureCommunicationServiceArgs
        {
            Prefix = prefix,
            ResourceGroup = rg,
            DomainResult = domainResult,
            DependsOn = serviceDependsOn,
        });
        
        finalSmtpHost = acsResult.SmtpHost;
        finalSmtpUser = acsResult.SmtpUser;
        finalSmtpPassword = acsResult.SmtpPassword;
        finalMailFrom = acsResult.MailFrom;
    }

    // 5. Cloud-init script to bootstrap the VM
    var cloudInit = CloudInitBuilder.Build(new CloudInitConfig
    {
        RepoUrl = repoUrl,
        RepoBranch = repoBranch,
        Domain = domain,
        SubdomainPrefix = subdomainPrefix,
        Environment = environment,
        DbUser = Output.Create("tixtalk"),
        DbPassword = secrets.DbPassword.Result,
        PretixSecretKey = secrets.PretixSecretKey.Result,
        PretalxSecretKey = secrets.PretalxSecretKey.Result,
        PretixImageTag = pretixImageTag,
        PretalxImageTag = pretalxImageTag,
        CloudflareApiToken = cloudflareApiToken,
        CloudflareZoneId = cloudflareZoneId,
        CloudflareDnsChallenge = cloudflareDnsChallenge,
        SmtpHost = finalSmtpHost,
        SmtpPort = smtpPort,
        SmtpUser = finalSmtpUser,
        SmtpPassword = finalSmtpPassword,
        MailFrom = finalMailFrom,
        AdminEmail = adminEmail,
        AdminPassword = secrets.AdminPassword.Result,
        OrganiserName = organiserName,
        OrganiserSlug = organiserSlug,
    });

    // 6. Ubuntu 24.04 LTS Virtual Machine
    var vm = VirtualMachineStack.Create(new VirtualMachineArgs
    {
        Prefix = prefix,
        ResourceGroup = rg,
        Network = network,
        VmSize = vmSize,
        SshPublicKey = sshPublicKey,
        CloudInitScript = cloudInit,
    });

    // 7. Cloudflare app DNS records (A records for tickets/talks subdomains)
    //    Separated from ACS verification DNS to avoid dependency cycle (VM ↔ DNS)
    if (!string.IsNullOrEmpty(cloudflareApiToken) && !string.IsNullOrEmpty(cloudflareZoneId))
    {
        CloudflareDnsStack.CreateAppRecords(new CloudflareDnsArgs
        {
            Prefix = prefix,
            Domain = domain,
            SubdomainPrefix = subdomainPrefix,
            CloudflareApiToken = cloudflareApiToken,
            CloudflareZoneId = cloudflareZoneId,
            Proxied = cloudflareDnsChallenge == "true",
            VmPublicIp = vm.PublicIpAddress,
        });
    }

    // Outputs
    var ticketsHost = $"{subdomainPrefix}tickets.{domain}";
    var talksHost = $"{subdomainPrefix}talks.{domain}";
    var outputs = new Dictionary<string, object?>
    {
        ["environment"] = environment,
        ["resourceGroupName"] = rg.Name,
        ["vmPublicIp"] = vm.PublicIpAddress,
        ["sshCommand"] = vm.PublicIpAddress.Apply(ip => $"ssh azureuser@{ip}"),
        ["ticketsHost"] = ticketsHost,
        ["talksHost"] = talksHost,
        ["pretixUrl"] = $"https://{ticketsHost}",
        ["pretalxUrl"] = $"https://{talksHost}",
    };

    // Add admin credentials to outputs if admin email is configured
    if (!string.IsNullOrEmpty(adminEmail))
    {
        outputs["adminEmail"] = adminEmail;
        outputs["adminPassword"] = Output.CreateSecret(secrets.AdminPassword.Result);
        outputs["pretixAdminUrl"] = $"https://{ticketsHost}/control/";
        outputs["pretalxAdminUrl"] = $"https://{talksHost}/orga/";
    }

    // Add ACS info to outputs if enabled
    if (acsResult != null)
    {
        outputs["emailProvider"] = "Azure Communication Services";
        outputs["smtpHost"] = acsResult.SmtpHost;
        outputs["mailFrom"] = acsResult.MailFrom;
        outputs["acsConnectionString"] = Output.CreateSecret(acsResult.ConnectionString);
    }

    return outputs;
});