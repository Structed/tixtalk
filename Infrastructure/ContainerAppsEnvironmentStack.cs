using Pulumi;
using Pulumi.AzureNative.App;
using Pulumi.AzureNative.App.Inputs;
using Pulumi.AzureNative.OperationalInsights;
using Pulumi.AzureNative.OperationalInsights.Inputs;
using Pulumi.AzureNative.Resources;

namespace PreTalxTixAzure.Infrastructure;

public record ContainerAppsEnvironmentResult(
    ManagedEnvironment Environment,
    string PretixStorageName,
    string PretalxStorageName
);

public static class ContainerAppsEnvironmentStack
{
    public static ContainerAppsEnvironmentResult Create(
        string prefix, ResourceGroup rg, StorageResult storage)
    {
        // Log Analytics workspace (required by ACA)
        var logAnalytics = new Workspace($"{prefix}-logs", new WorkspaceArgs
        {
            WorkspaceName = $"{prefix}-logs",
            ResourceGroupName = rg.Name,
            Sku = new WorkspaceSkuArgs
            {
                Name = WorkspaceSkuNameEnum.PerGB2018,
            },
            RetentionInDays = 30,
        });

        var logAnalyticsKey = Output.Tuple(rg.Name, logAnalytics.Name).Apply(async t =>
        {
            var keys = await GetSharedKeys.InvokeAsync(new GetSharedKeysArgs
            {
                ResourceGroupName = t.Item1,
                WorkspaceName = t.Item2,
            });
            return keys.PrimarySharedKey!;
        });

        // ACA Managed Environment (Consumption plan — no dedicated plan needed)
        var env = new ManagedEnvironment($"{prefix}-env", new ManagedEnvironmentArgs
        {
            EnvironmentName = $"{prefix}-env",
            ResourceGroupName = rg.Name,
            AppLogsConfiguration = new AppLogsConfigurationArgs
            {
                Destination = "log-analytics",
                LogAnalyticsConfiguration = new LogAnalyticsConfigurationArgs
                {
                    CustomerId = logAnalytics.CustomerId,
                    SharedKey = logAnalyticsKey,
                },
            },
        });

        // Register Azure Files shares with the ACA environment
        var pretixStorageName = "pretixdata";
        var pretixEnvStorage = new ManagedEnvironmentsStorage($"{prefix}-pretix-envstorage",
            new ManagedEnvironmentsStorageArgs
            {
                ResourceGroupName = rg.Name,
                EnvironmentName = env.Name,
                StorageName = pretixStorageName,
                Properties = new ManagedEnvironmentStoragePropertiesArgs
                {
                    AzureFile = new AzureFilePropertiesArgs
                    {
                        AccountName = storage.AccountName,
                        AccountKey = storage.AccountKey,
                        ShareName = storage.PretixShareName,
                        AccessMode = AccessMode.ReadWrite,
                    },
                },
            });

        var pretalxStorageName = "pretalxdata";
        var pretalxEnvStorage = new ManagedEnvironmentsStorage($"{prefix}-pretalx-envstorage",
            new ManagedEnvironmentsStorageArgs
            {
                ResourceGroupName = rg.Name,
                EnvironmentName = env.Name,
                StorageName = pretalxStorageName,
                Properties = new ManagedEnvironmentStoragePropertiesArgs
                {
                    AzureFile = new AzureFilePropertiesArgs
                    {
                        AccountName = storage.AccountName,
                        AccountKey = storage.AccountKey,
                        ShareName = storage.PretalxShareName,
                        AccessMode = AccessMode.ReadWrite,
                    },
                },
            });

        return new ContainerAppsEnvironmentResult(
            Environment: env,
            PretixStorageName: pretixStorageName,
            PretalxStorageName: pretalxStorageName
        );
    }
}
