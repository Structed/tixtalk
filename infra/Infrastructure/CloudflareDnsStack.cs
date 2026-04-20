using Pulumi;
using Pulumi.Cloudflare;
using DnsRecordResponse = Pulumi.AzureNative.Communication.Outputs.DnsRecordResponse;

namespace TixTalk.Infra.Infrastructure;

public record CloudflareDnsArgs
{
    public required string Prefix { get; init; }
    public required string Domain { get; init; }
    public required string CloudflareApiToken { get; init; }
    public required string CloudflareZoneId { get; init; }
    public bool Proxied { get; init; } = false;
    public required Output<string> VmPublicIp { get; init; }
}

public record CloudflareAcsVerificationArgs
{
    public required string Prefix { get; init; }
    public required string CloudflareApiToken { get; init; }
    public required string CloudflareZoneId { get; init; }
    /// <summary>
    /// The verification records from the ACS Domain resource, as individual typed outputs.
    /// </summary>
    public required Output<DnsRecordResponse?> DomainRecord { get; init; }
    public required Output<DnsRecordResponse?> SpfRecord { get; init; }
    public required Output<DnsRecordResponse?> DkimRecord { get; init; }
    public required Output<DnsRecordResponse?> Dkim2Record { get; init; }
}

/// <summary>
/// Result from creating ACS verification DNS records, providing explicit resources
/// for dependency tracking.
/// </summary>
public record CloudflareAcsVerificationResult
{
    public required DnsRecord[] DnsRecords { get; init; }
}

/// <summary>
/// Creates Cloudflare DNS records as Pulumi-managed resources so they are
/// automatically cleaned up on <c>pulumi destroy</c>.
/// </summary>
public static class CloudflareDnsStack
{
    private const string Comment = "Managed by tixtalk";
    private const string AcsComment = "Managed by tixtalk (ACS email)";

    /// <summary>
    /// Creates A records for tickets.{domain} and talks.{domain}.
    /// </summary>
    public static void CreateAppRecords(CloudflareDnsArgs args)
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
    }

    /// <summary>
    /// Creates ACS email verification DNS records (Domain TXT, SPF TXT, DKIM/DKIM2 CNAMEs)
    /// as top-level resources with proper dependency tracking.
    /// Returns the created DNS records so they can be used as explicit dependencies.
    /// </summary>
    public static CloudflareAcsVerificationResult CreateAcsVerificationRecords(CloudflareAcsVerificationArgs args)
    {
        // Separate provider instance for ACS records (app records keep {prefix}-cf for migration)
        var provider = new Provider($"{args.Prefix}-cf-acs", new ProviderArgs
        {
            ApiToken = args.CloudflareApiToken,
        });

        var opts = new CustomResourceOptions { Provider = provider };

        // Helper to declare a DNS record and validate that the verification record
        // has real values when Pulumi resolves the ACS outputs during deployment.
        DnsRecord CreateVerificationRecord(string suffix, string type,
            Output<DnsRecordResponse?> record)
        {
            return new DnsRecord($"{args.Prefix}-acs-{suffix}", new DnsRecordArgs
            {
                ZoneId = args.CloudflareZoneId,
                Name = record.Apply(r =>
                {
                    if (r is null || string.IsNullOrEmpty(r.Name))
                        throw new Exception($"ACS verification record '{suffix}' has no Name. Domain may not be ready.");
                    return r.Name;
                }),
                Type = type,
                Content = record.Apply(r =>
                {
                    if (r is null || string.IsNullOrEmpty(r.Value))
                        throw new Exception($"ACS verification record '{suffix}' has no Value. Domain may not be ready.");
                    return r.Value;
                }),
                Ttl = record.Apply(r => (double)(r?.Ttl ?? 3600)),
                Comment = AcsComment,
            }, opts);
        }

        var records = new[]
        {
            CreateVerificationRecord("domain", "TXT", args.DomainRecord),
            CreateVerificationRecord("spf", "TXT", args.SpfRecord),
            CreateVerificationRecord("dkim", "CNAME", args.DkimRecord),
            CreateVerificationRecord("dkim2", "CNAME", args.Dkim2Record),
        };

        return new CloudflareAcsVerificationResult
        {
            DnsRecords = records,
        };
    }
}
