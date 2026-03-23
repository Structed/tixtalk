using Pulumi;
using Pulumi.AzureNative.App;
using Pulumi.AzureNative.App.Inputs;
using Pulumi.AzureNative.Resources;

namespace PreTalxTixAzure.Infrastructure;

public record PretixContainerAppArgs
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

public record PretixResult(ContainerApp App, Output<string> Fqdn);

public static class PretixContainerApp
{
    public static PretixResult Create(PretixContainerAppArgs args)
    {
        var appName = $"{args.Prefix}-pretix";
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
                        Name = "pretix-data",
                        StorageType = StorageType.AzureFile,
                        StorageName = args.StorageMountName,
                    },
                },
                Containers = new[]
                {
                    new ContainerArgs
                    {
                        Name = "pretix",
                        Image = $"pretix/standalone:{args.ImageTag}",
                        Resources = new ContainerResourcesArgs
                        {
                            Cpu = 0.5,
                            Memory = "1Gi",
                        },
                        VolumeMounts = new[]
                        {
                            new VolumeMountArgs
                            {
                                VolumeName = "pretix-data",
                                MountPath = "/data",
                            },
                        },
                        Env = new[]
                        {
                            // Core settings
                            Env("PRETIX_PRETIX_URL", args.SiteUrl),
                            Env("PRETIX_PRETIX_DATADIR", "/data"),
                            Env("PRETIX_PRETIX_TRUST_X_FORWARDED_FOR", "on"),
                            Env("PRETIX_PRETIX_TRUST_X_FORWARDED_PROTO", "on"),

                            // Database
                            Env("PRETIX_DATABASE_BACKEND", "postgresql"),
                            EnvOutput("PRETIX_DATABASE_HOST", args.DbFqdn),
                            Env("PRETIX_DATABASE_PORT", "5432"),
                            Env("PRETIX_DATABASE_NAME", args.DbName),
                            Env("PRETIX_DATABASE_USER", args.DbUser),
                            EnvSecret("PRETIX_DATABASE_PASSWORD", "db-password"),
                            Env("PRETIX_DATABASE_SSLMODE", "require"),

                            // Redis & Celery
                            Env("PRETIX_REDIS_LOCATION", $"redis://{redisHost}:6379/0"),
                            Env("PRETIX_REDIS_SESSIONS", "true"),
                            Env("PRETIX_CELERY_BROKER", $"redis://{redisHost}:6379/1"),
                            Env("PRETIX_CELERY_BACKEND", $"redis://{redisHost}:6379/2"),

                            // Email
                            Env("PRETIX_MAIL_FROM", args.MailFrom),
                            Env("PRETIX_MAIL_HOST", args.SmtpHost),
                            Env("PRETIX_MAIL_PORT", args.SmtpPort.ToString()),
                            Env("PRETIX_MAIL_USER", args.SmtpUser),
                            EnvSecret("PRETIX_MAIL_PASSWORD", "smtp-password"),
                            Env("PRETIX_MAIL_TLS", "on"),

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

        return new PretixResult(
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
