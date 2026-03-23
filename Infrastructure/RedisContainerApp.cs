using Pulumi;
using Pulumi.AzureNative.App;
using Pulumi.AzureNative.App.Inputs;
using Pulumi.AzureNative.Resources;

namespace PreTalxTixAzure.Infrastructure;

public record RedisResult(ContainerApp App, string HostName);

public static class RedisContainerApp
{
    public static RedisResult Create(string prefix, ResourceGroup rg, ManagedEnvironment env)
    {
        var appName = $"{prefix}-redis";

        var app = new ContainerApp(appName, new ContainerAppArgs
        {
            ContainerAppName = appName,
            ResourceGroupName = rg.Name,
            ManagedEnvironmentId = env.Id,
            Configuration = new ConfigurationArgs
            {
                // Internal-only TCP ingress for Redis protocol
                Ingress = new IngressArgs
                {
                    External = false,
                    TargetPort = 6379,
                    ExposedPort = 6379,
                    Transport = IngressTransportMethod.Tcp,
                },
            },
            Template = new TemplateArgs
            {
                Containers = new[]
                {
                    new ContainerArgs
                    {
                        Name = "redis",
                        Image = "redis:7-alpine",
                        Command = new[] { "redis-server", "--appendonly", "yes", "--maxmemory", "128mb", "--maxmemory-policy", "allkeys-lru" },
                        Resources = new ContainerResourcesArgs
                        {
                            Cpu = 0.25,
                            Memory = "0.5Gi",
                        },
                    },
                },
                Scale = new ScaleArgs
                {
                    MinReplicas = 1,
                    MaxReplicas = 1,
                },
            },
        });

        return new RedisResult(App: app, HostName: appName);
    }
}
