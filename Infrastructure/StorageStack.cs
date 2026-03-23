using Pulumi;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Storage;
using Pulumi.AzureNative.Storage.Inputs;
using PreTalxTixAzure.Helpers;
using StorageSkuArgs = Pulumi.AzureNative.Storage.Inputs.SkuArgs;

namespace PreTalxTixAzure.Infrastructure;

public record StorageResult(
    StorageAccount Account,
    Output<string> AccountName,
    Output<string> AccountKey,
    string PretixShareName,
    string PretalxShareName
);

public static class StorageStack
{
    public const string PretixShare = "pretix-data";
    public const string PretalxShare = "pretalx-data";

    public static StorageResult Create(string prefix, ResourceGroup rg)
    {
        var accountName = NamingConventions.StorageAccountName(prefix);

        var storageAccount = new StorageAccount($"{prefix}-storage", new StorageAccountArgs
        {
            AccountName = accountName,
            ResourceGroupName = rg.Name,
            Kind = Kind.StorageV2,
            Sku = new StorageSkuArgs
            {
                Name = SkuName.Standard_LRS,
            },
        });

        var pretixShare = new Pulumi.AzureNative.Storage.FileShare($"{prefix}-pretix-share", new FileShareArgs
        {
            AccountName = storageAccount.Name,
            ResourceGroupName = rg.Name,
            ShareName = PretixShare,
            ShareQuota = 5, // 5 GiB — enough for media uploads
        });

        var pretalxShare = new Pulumi.AzureNative.Storage.FileShare($"{prefix}-pretalx-share", new FileShareArgs
        {
            AccountName = storageAccount.Name,
            ResourceGroupName = rg.Name,
            ShareName = PretalxShare,
            ShareQuota = 5,
        });

        // Retrieve the primary storage key
        var accountKey = Output.Tuple(rg.Name, storageAccount.Name).Apply(async t =>
        {
            var keys = await ListStorageAccountKeys.InvokeAsync(new ListStorageAccountKeysArgs
            {
                ResourceGroupName = t.Item1,
                AccountName = t.Item2,
            });
            return keys.Keys[0].Value;
        });

        return new StorageResult(
            Account: storageAccount,
            AccountName: storageAccount.Name,
            AccountKey: accountKey,
            PretixShareName: PretixShare,
            PretalxShareName: PretalxShare
        );
    }
}
