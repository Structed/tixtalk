using Pulumi;
using PreTalxTix.Infra.Helpers;
using PreTalxTix.Infra.Infrastructure;

return await Deployment.RunAsync(() =>
{
    var config = new Config();
    var prefix = config.Require("prefix");
    var domain = config.Require("domain");
    var sshPublicKey = config.Require("sshPublicKey");
    var vmSize = config.Get("vmSize") ?? "Standard_B2s";
    var pretixImageTag = config.Get("pretixImageTag") ?? "stable";
    var pretalxImageTag = config.Get("pretalxImageTag") ?? "latest";
    var repoUrl = config.Get("repoUrl") ?? "https://github.com/Structed/pre-talx-tix-azure.git";
    var repoBranch = config.Get("repoBranch") ?? ""; // Empty = default branch

    // Optional config
    var cloudflareApiToken = config.Get("cloudflareApiToken") ?? "";
    var cloudflareZoneId = config.Get("cloudflareZoneId") ?? "";
    var cloudflareDnsChallenge = config.Get("cloudflareDnsChallenge") ?? "false";
    
    // Email configuration - ACS is the default
    var useAzureMail = config.GetBoolean("useAzureMail") ?? true;
    var acsUseCustomDomain = config.GetBoolean("acsUseCustomDomain") ?? false;
    var smtpHost = config.Get("smtpHost") ?? "";
    var smtpPort = config.GetInt32("smtpPort") ?? 587;
    var smtpUser = config.Get("smtpUser") ?? "";
    var smtpPassword = config.Get("smtpPassword") ?? "";
    var mailFrom = config.Get("mailFrom") ?? $"noreply@{domain}";

    // Admin superuser config
    var adminEmail = config.Get("adminEmail") ?? "";
    var organiserName = config.Get("organiserName") ?? "Conference Organiser";
    var organiserSlug = config.Get("organiserSlug") ?? "organiser";

    // 1. Resource Group
    var rg = ResourceGroupStack.Create(prefix);

    // 2. Networking (VNet, Subnet, NSG, Public IP, NIC)
    var network = NetworkStack.Create(prefix, rg);

    // 3. Auto-generated secrets (encrypted in Pulumi state)
    var secrets = SecretGenerator.Create(prefix);

    // 4. Azure Communication Services (optional - for email)
    Output<string> finalSmtpHost = Output.Create(smtpHost);
    Output<string> finalSmtpUser = Output.Create(smtpUser);
    Output<string> finalSmtpPassword = Output.Create(smtpPassword);
    Output<string> finalMailFrom = Output.Create(mailFrom);
    Output<string>? acsVerificationRecords = null;
    
    AzureCommunicationResult? acsResult = null;
    if (useAzureMail)
    {
        // Use Azure Communication Services for email
        // Custom domain requires Cloudflare for DNS automation
        acsResult = AzureCommunicationStack.Create(new AzureCommunicationArgs
        {
            Prefix = prefix,
            Domain = domain,
            ResourceGroup = rg,
            UseCustomDomain = acsUseCustomDomain,
        });
        
        finalSmtpHost = acsResult.SmtpHost;
        finalSmtpUser = acsResult.SmtpUser;
        finalSmtpPassword = acsResult.SmtpPassword;
        finalMailFrom = acsResult.MailFrom;
        acsVerificationRecords = acsResult.VerificationRecords;
    }

    // 5. Cloud-init script to bootstrap the VM
    var cloudInit = CloudInitBuilder.Build(new CloudInitConfig
    {
        RepoUrl = repoUrl,
        RepoBranch = repoBranch,
        Domain = domain,
        DbUser = Output.Create("pretalxtix"),
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
        AcsVerificationRecords = acsVerificationRecords,
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

    // Outputs
    var outputs = new Dictionary<string, object?>
    {
        ["resourceGroupName"] = rg.Name,
        ["vmPublicIp"] = vm.PublicIpAddress,
        ["sshCommand"] = vm.PublicIpAddress.Apply(ip => $"ssh azureuser@{ip}"),
        ["pretixUrl"] = $"https://tickets.{domain}",
        ["pretalxUrl"] = $"https://talks.{domain}",
    };

    // Add admin credentials to outputs if admin email is configured
    if (!string.IsNullOrEmpty(adminEmail))
    {
        outputs["adminEmail"] = adminEmail;
        outputs["adminPassword"] = Output.CreateSecret(secrets.AdminPassword.Result);
        outputs["pretixAdminUrl"] = $"https://tickets.{domain}/control/";
        outputs["pretalxAdminUrl"] = $"https://talks.{domain}/orga/";
    }

    // Add ACS info to outputs if enabled
    if (acsResult != null)
    {
        outputs["emailProvider"] = "Azure Communication Services";
        outputs["smtpHost"] = acsResult.SmtpHost;
        outputs["mailFrom"] = acsResult.MailFrom;
    }

    return outputs;
});
