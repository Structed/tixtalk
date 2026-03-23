using Pulumi;
using Pulumi.AzureNative.App;
using Pulumi.AzureNative.App.Inputs;
using Pulumi.AzureNative.Resources;

namespace PreTalxTixAzure.Infrastructure;

public record PretalxContainerAppArgs
{
    public required string Prefix { get; init; }
    public required ResourceGroup ResourceGroup { get; init; }
    public required ManagedEnvironment Environment { get; init; }
    public required string StorageMountName { get; init; }
    public required Output<string> DbFqdn { get; init; }
    public required string DbName { get; init; }
    public required string DbUser { get; init; }
    public required Output<string> DbPassword { get; init; }
    public required RedisResult Redis { get; init; }
    public required Output<string> SecretKey { get; init; }
    public required string ImageTag { get; init; }
    public required string SiteUrl { get; init; }
    public required string SmtpHost { get; init; }
    public required int SmtpPort { get; init; }
    public required string SmtpUser { get; init; }
    public required Output<string> SmtpPassword { get; init; }
    public required string MailFrom { get; init; }
}

public record PretalxResult(ContainerApp App, Output<string> Fqdn);

public static class PretalxContainerApp
{
    public static PretalxResult Create(PretalxContainerAppArgs args)
    {
        var appName = $"{args.Prefix}-pretalx";
        var redisHost = args.Redis.HostName;

        var app = new ContainerApp(appName, new ContainerAppArgs
        {
            ContainerAppName = appName,
            ResourceGroupName = args.ResourceGroup.Name,
            ManagedEnvironmentId = args.Environment.Id,
            Configuration = new ConfigurationArgs
            {
                Ingress = new IngressArgs
                {
                    External = true,
                    TargetPort = 80,
                    Transport = IngressTransportMethod.Auto,
                    AllowInsecure = false,
                },
                Secrets = new[]
                {
                    new SecretArgs { Name = "db-password", Value = args.DbPassword },
                    new SecretArgs { Name = "secret-key", Value = args.SecretKey },
                    new SecretArgs { Name = "smtp-password", Value = args.SmtpPassword },
                },
            },
            Template = new TemplateArgs
            {
                Volumes = new[]
                {
                    new VolumeArgs
                    {
                        Name = "pretalx-data",
                        StorageType = StorageType.AzureFile,
                        StorageName = args.StorageMountName,
                    },
                },
                Containers = new[]
                {
                    new ContainerArgs
                    {
                        Name = "pretalx",
                        Image = $"pretalx/standalone:{args.ImageTag}",
                        Resources = new ContainerResourcesArgs
                        {
                            Cpu = 0.5,
                            Memory = "1Gi",
                        },
                        VolumeMounts = new[]
                        {
                            new VolumeMountArgs
                            {
                                VolumeName = "pretalx-data",
                                MountPath = "/data",
                            },
                        },
                        Env = new[]
                        {
                            // Core settings
                            Env("PRETALX_SITE_URL", args.SiteUrl),
                            Env("PRETALX_FILESYSTEM_DATA", "/data"),

                            // Database
                            Env("PRETALX_DATABASE_BACKEND", "postgresql"),
                            EnvOutput("PRETALX_DATABASE_HOST", args.DbFqdn),
                            Env("PRETALX_DATABASE_PORT", "5432"),
                            Env("PRETALX_DATABASE_NAME", args.DbName),
                            Env("PRETALX_DATABASE_USER", args.DbUser),
                            EnvSecret("PRETALX_DATABASE_PASSWORD", "db-password"),

                            // Redis & Celery
                            Env("PRETALX_REDIS_LOCATION", $"redis://{redisHost}:6379/3"),
                            Env("PRETALX_CELERY_BROKER", $"redis://{redisHost}:6379/4"),
                            Env("PRETALX_CELERY_BACKEND", $"redis://{redisHost}:6379/5"),

                            // Email
                            Env("PRETALX_MAIL_FROM", args.MailFrom),
                            Env("PRETALX_MAIL_HOST", args.SmtpHost),
                            Env("PRETALX_MAIL_PORT", args.SmtpPort.ToString()),
                            Env("PRETALX_MAIL_USER", args.SmtpUser),
                            EnvSecret("PRETALX_MAIL_PASSWORD", "smtp-password"),

                            // Security
                            EnvSecret("SECRET_KEY", "secret-key"),
                        },
                    },
                },
                Scale = new ScaleArgs
                {
                    MinReplicas = 1,
                    MaxReplicas = 2,
                },
            },
        });

        return new PretalxResult(
            App: app,
            Fqdn: app.Configuration.Apply(c => c?.Ingress?.Fqdn ?? "")
        );
    }

    private static EnvironmentVarArgs Env(string name, string value) =>
        new() { Name = name, Value = value };

    private static EnvironmentVarArgs EnvOutput(string name, Output<string> value) =>
        new() { Name = name, Value = value };

    private static EnvironmentVarArgs EnvSecret(string name, string secretRef) =>
        new() { Name = name, SecretRef = secretRef };
}
