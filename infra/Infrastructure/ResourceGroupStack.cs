using Pulumi.AzureNative.Resources;

namespace PreTalxTix.Infra.Infrastructure;

public static class ResourceGroupStack
{
    public static ResourceGroup Create(string prefix)
    {
        return new ResourceGroup($"{prefix}-rg", new ResourceGroupArgs
        {
            ResourceGroupName = $"{prefix}-rg",
        });
    }
}
