using Pulumi;
using PreTalxTixAzure.Helpers;
using PreTalxTixAzure.Infrastructure;

return await Deployment.RunAsync(() =>
{
    var config = new Config();
    var prefix = config.Require("prefix");
    var pretixImageTag = config.Get("pretixImageTag") ?? "stable";
    var pretalxImageTag = config.Get("pretalxImageTag") ?? "latest";
    var pretixUrl = config.Get("pretixUrl") ?? "";
    var pretalxUrl = config.Get("pretalxUrl") ?? "";
    var smtpHost = config.Get("smtpHost") ?? "";
    var smtpPort = config.GetInt32("smtpPort") ?? 587;
    var smtpUser = config.Get("smtpUser") ?? "";
    var smtpPassword = config.GetSecret("smtpPassword") ?? Output.Create("");
    var mailFrom = config.Get("mailFrom") ?? "noreply@example.com";

    // 1. Resource Group
    var rg = ResourceGroupStack.Create(prefix);

    // 2. Auto-generated secrets (encrypted in Pulumi state)
    var secrets = SecretGenerator.Create(prefix);

    // 3. PostgreSQL Flexible Server
    var db = PostgreSqlStack.Create(prefix, rg, secrets.DbPassword);

    // 4. Azure Files for persistent data volumes
    var storage = StorageStack.Create(prefix, rg);

    // 5. Container Apps Environment
    var acaEnv = ContainerAppsEnvironmentStack.Create(prefix, rg, storage);

    // 6. Redis (internal container, shared by both apps)
    var redis = RedisContainerApp.Create(prefix, rg, acaEnv.Environment);

    // 7. Pretix — ticketing
    var pretix = PretixContainerApp.Create(new PretixContainerAppArgs
    {
        Prefix = prefix,
        ResourceGroup = rg,
        Environment = acaEnv.Environment,
        StorageMountName = acaEnv.PretixStorageName,
        DbFqdn = db.ServerFqdn,
        DbName = db.PretixDbName,
        DbUser = db.AdminUser,
        DbPassword = secrets.DbPassword.Result,
        Redis = redis,
        SecretKey = secrets.PretixSecretKey.Result,
        ImageTag = pretixImageTag,
        SiteUrl = pretixUrl,
        SmtpHost = smtpHost,
        SmtpPort = smtpPort,
        SmtpUser = smtpUser,
        SmtpPassword = smtpPassword,
        MailFrom = mailFrom,
    });

    // 8. Pretalx — call for papers & scheduling
    var pretalx = PretalxContainerApp.Create(new PretalxContainerAppArgs
    {
        Prefix = prefix,
        ResourceGroup = rg,
        Environment = acaEnv.Environment,
        StorageMountName = acaEnv.PretalxStorageName,
        DbFqdn = db.ServerFqdn,
        DbName = db.PretalxDbName,
        DbUser = db.AdminUser,
        DbPassword = secrets.DbPassword.Result,
        Redis = redis,
        SecretKey = secrets.PretalxSecretKey.Result,
        ImageTag = pretalxImageTag,
        SiteUrl = pretalxUrl,
        SmtpHost = smtpHost,
        SmtpPort = smtpPort,
        SmtpUser = smtpUser,
        SmtpPassword = smtpPassword,
        MailFrom = mailFrom,
    });

    return new Dictionary<string, object?>
    {
        ["resourceGroupName"] = rg.Name,
        ["pretixFqdn"] = pretix.Fqdn,
        ["pretalxFqdn"] = pretalx.Fqdn,
        ["postgresServerFqdn"] = db.ServerFqdn,
        ["pretixUrl"] = pretix.Fqdn.Apply(fqdn => $"https://{fqdn}"),
        ["pretalxUrl"] = pretalx.Fqdn.Apply(fqdn => $"https://{fqdn}"),
    };
});
