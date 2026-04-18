using Pulumi;
using Pulumi.Cloudflare;

namespace PreTalxTix.Infra.Infrastructure;

public record CloudflareDnsArgs
{
    public required string Prefix { get; init; }
    public required string Domain { get; init; }
    public required string CloudflareApiToken { get; init; }
    public required string CloudflareZoneId { get; init; }
    public bool Proxied { get; init; } = false;
    public required Output<string> VmPublicIp { get; init; }
    /// <summary>
    /// JSON-serialized ACS verification records (from AzureCommunicationStack).
    /// Null when ACS custom domain is not used.
    /// </summary>
    public Output<string>? AcsVerificationRecords { get; init; }
}

/// <summary>
/// Creates Cloudflare DNS records as Pulumi-managed resources so they are
/// automatically cleaned up on <c>pulumi destroy</c>.
/// </summary>
public static class CloudflareDnsStack
{
    private const string Comment = "Managed by ptx";
    private const string AcsComment = "Managed by ptx (ACS email)";

    public static void Create(CloudflareDnsArgs args)
    {
        var provider = new Provider($"{args.Prefix}-cf", new ProviderArgs
        {
            ApiToken = args.CloudflareApiToken,
        });

        var opts = new CustomResourceOptions { Provider = provider };

        // A records for tickets.{domain} and talks.{domain}
        _ = new DnsRecord($"{args.Prefix}-dns-tickets", new DnsRecordArgs
        {
            ZoneId = args.CloudflareZoneId,
            Name = $"tickets.{args.Domain}",
            Type = "A",
            Content = args.VmPublicIp,
            Ttl = 1, // Auto TTL
            Proxied = args.Proxied,
            Comment = Comment,
        }, opts);

        _ = new DnsRecord($"{args.Prefix}-dns-talks", new DnsRecordArgs
        {
            ZoneId = args.CloudflareZoneId,
            Name = $"talks.{args.Domain}",
            Type = "A",
            Content = args.VmPublicIp,
            Ttl = 1,
            Proxied = args.Proxied,
            Comment = Comment,
        }, opts);

        // ACS email verification DNS records (custom domain only)
        if (args.AcsVerificationRecords != null)
        {
            CreateAcsVerificationRecords(args, opts);
        }
    }

    private static void CreateAcsVerificationRecords(CloudflareDnsArgs args, CustomResourceOptions opts)
    {
        args.AcsVerificationRecords!.Apply(json =>
        {
            if (string.IsNullOrEmpty(json) || json == "{}")
                return 0;

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Domain ownership TXT record
            if (root.TryGetProperty("domain", out var domain))
            {
                var name = domain.GetProperty("name").GetString()!;
                var value = domain.GetProperty("value").GetString()!;
                var ttl = domain.GetProperty("ttl").GetInt32();
                _ = new DnsRecord($"{args.Prefix}-acs-domain", new DnsRecordArgs
                {
                    ZoneId = args.CloudflareZoneId,
                    Name = name,
                    Type = "TXT",
                    Content = value,
                    Ttl = ttl,
                    Comment = AcsComment,
                }, opts);
            }

            // SPF TXT record
            if (root.TryGetProperty("spf", out var spf))
            {
                var name = spf.GetProperty("name").GetString()!;
                var value = spf.GetProperty("value").GetString()!;
                var ttl = spf.GetProperty("ttl").GetInt32();
                _ = new DnsRecord($"{args.Prefix}-acs-spf", new DnsRecordArgs
                {
                    ZoneId = args.CloudflareZoneId,
                    Name = name,
                    Type = "TXT",
                    Content = value,
                    Ttl = ttl,
                    Comment = AcsComment,
                }, opts);
            }

            // DKIM CNAME records
            if (root.TryGetProperty("dkim", out var dkim))
            {
                var name = dkim.GetProperty("name").GetString()!;
                var value = dkim.GetProperty("value").GetString()!;
                var ttl = dkim.GetProperty("ttl").GetInt32();
                _ = new DnsRecord($"{args.Prefix}-acs-dkim", new DnsRecordArgs
                {
                    ZoneId = args.CloudflareZoneId,
                    Name = name,
                    Type = "CNAME",
                    Content = value,
                    Ttl = ttl,
                    Comment = AcsComment,
                }, opts);
            }

            if (root.TryGetProperty("dkim2", out var dkim2))
            {
                var name = dkim2.GetProperty("name").GetString()!;
                var value = dkim2.GetProperty("value").GetString()!;
                var ttl = dkim2.GetProperty("ttl").GetInt32();
                _ = new DnsRecord($"{args.Prefix}-acs-dkim2", new DnsRecordArgs
                {
                    ZoneId = args.CloudflareZoneId,
                    Name = name,
                    Type = "CNAME",
                    Content = value,
                    Ttl = ttl,
                    Comment = AcsComment,
                }, opts);
            }

            return 0;
        });
    }
}
