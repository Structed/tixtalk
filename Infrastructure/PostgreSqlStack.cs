using Pulumi;
using Pulumi.AzureNative.DBforPostgreSQL;
using Pulumi.AzureNative.Resources;
using PgStorageArgs = Pulumi.AzureNative.DBforPostgreSQL.Inputs.StorageArgs;
using PgBackupArgs = Pulumi.AzureNative.DBforPostgreSQL.Inputs.BackupArgs;
using PgHaArgs = Pulumi.AzureNative.DBforPostgreSQL.Inputs.HighAvailabilityArgs;
using PgSkuArgs = Pulumi.AzureNative.DBforPostgreSQL.Inputs.SkuArgs;

namespace PreTalxTixAzure.Infrastructure;

public record PostgreSqlResult(
    Output<string> ServerFqdn,
    string AdminUser,
    string PretixDbName,
    string PretalxDbName
);

public static class PostgreSqlStack
{
    public const string AdminUsername = "pgadmin";
    public const string PretixDatabase = "pretix";
    public const string PretalxDatabase = "pretalx";

    public static PostgreSqlResult Create(string prefix, ResourceGroup rg, Pulumi.Random.RandomPassword adminPassword)
    {
        var server = new Server($"{prefix}-pg", new ServerArgs
        {
            ServerName = $"{prefix}-pg",
            ResourceGroupName = rg.Name,
            CreateMode = CreateMode.Default,
            Version = "16",
            AdministratorLogin = AdminUsername,
            AdministratorLoginPassword = adminPassword.Result,
            Sku = new PgSkuArgs
            {
                Name = "Standard_B1ms",
                Tier = SkuTier.Burstable,
            },
            Storage = new PgStorageArgs
            {
                StorageSizeGB = 32,
            },
            Backup = new PgBackupArgs
            {
                BackupRetentionDays = 7,
                GeoRedundantBackup = GeoRedundantBackupEnum.Disabled,
            },
            HighAvailability = new PgHaArgs
            {
                Mode = HighAvailabilityMode.Disabled,
            },
        });

        // Allow Azure services (including ACA) to connect
        var firewallRule = new FirewallRule($"{prefix}-pg-allow-azure", new FirewallRuleArgs
        {
            ResourceGroupName = rg.Name,
            ServerName = server.Name,
            FirewallRuleName = "AllowAzureServices",
            StartIpAddress = "0.0.0.0",
            EndIpAddress = "0.0.0.0",
        });

        var pretixDb = new Database($"{prefix}-pretix-db", new DatabaseArgs
        {
            ResourceGroupName = rg.Name,
            ServerName = server.Name,
            DatabaseName = PretixDatabase,
            Charset = "UTF8",
            Collation = "en_US.utf8",
        });

        var pretalxDb = new Database($"{prefix}-pretalx-db", new DatabaseArgs
        {
            ResourceGroupName = rg.Name,
            ServerName = server.Name,
            DatabaseName = PretalxDatabase,
            Charset = "UTF8",
            Collation = "en_US.utf8",
        });

        return new PostgreSqlResult(
            ServerFqdn: server.FullyQualifiedDomainName!,
            AdminUser: AdminUsername,
            PretixDbName: PretixDatabase,
            PretalxDbName: PretalxDatabase
        );
    }
}
