using Pulumi;
using Pulumi.AzureAD;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.Communication;
using Pulumi.AzureNative.Resources;
using Guid = System.Guid;

namespace TixTalk.Infra.Infrastructure;

/// <summary>
/// Output record for phase 1: domain + email service creation.
/// </summary>
public record AzureDomainResult
{
    public required EmailService EmailService { get; init; }
    public required Domain Domain { get; init; }
    public required Output<string> MailFrom { get; init; }
    /// <summary>
    /// DNS verification records needed for custom domains (JSON serialized).
    /// Null when using an Azure-managed domain.
    /// </summary>
    public Output<string>? VerificationRecords { get; init; }
}

/// <summary>
/// Output record for phase 2: communication service + SMTP credentials.
/// </summary>
public record AzureCommunicationResult
{
    public required Output<string> SmtpHost { get; init; }
    public required Output<string> SmtpUser { get; init; }
    public required Output<string> SmtpPassword { get; init; }
    public required Output<string> MailFrom { get; init; }
    public required Output<string> EmailServiceId { get; init; }
    /// <summary>
    /// ACS connection string for REST API / SDK access (e.g. EmailClient).
    /// Allows other projects to use this ACS via Pulumi Stack Reference.
    /// </summary>
    public required Output<string> ConnectionString { get; init; }
}

/// <summary>
/// Arguments for creating the ACS stack.
/// </summary>
public record AzureCommunicationArgs
{
    public required string Prefix { get; init; }
    public required string Domain { get; init; }
    public required ResourceGroup ResourceGroup { get; init; }
    public bool UseCustomDomain { get; init; } = false;
}

/// <summary>
/// Arguments for creating the communication service (phase 2).
/// </summary>
public record AzureCommunicationServiceArgs
{
    public required string Prefix { get; init; }
    public required ResourceGroup ResourceGroup { get; init; }
    public required AzureDomainResult DomainResult { get; init; }
    /// <summary>
    /// Optional explicit dependencies (e.g., verification command) that must
    /// complete before the CommunicationService is created.
    /// </summary>
    public CustomResourceOptions? DependsOn { get; init; }
}

/// <summary>
/// Creates Azure Communication Services with Email capability.
/// Split into two phases to allow DNS verification between domain creation and service linking.
/// <list type="bullet">
///   <item><see cref="CreateDomain"/> — Phase 1: EmailService + Domain + SenderUsername</item>
///   <item><see cref="CreateService"/> — Phase 2: CommunicationService + Entra App + SMTP creds</item>
/// </list>
/// For Azure-managed domains, both phases can be called back-to-back (no verification needed).
/// For custom domains, DNS records must be created and verified between the two phases.
/// </summary>
public static class AzureCommunicationStack
{
    // "Communication and Email Service Owner" built-in role
    // Minimal role for sending email via ACS SMTP
    private const string EmailOwnerRoleId = "09976791-48a7-449e-bb21-39d1a415f350";

    /// <summary>
    /// Phase 1: Creates the Email Service, Domain, and SenderUsername.
    /// For custom domains, the returned VerificationRecords must be used to create DNS records
    /// and initiate verification before calling <see cref="CreateService"/>.
    /// </summary>
    public static AzureDomainResult CreateDomain(AzureCommunicationArgs args)
    {
        // 1. Create the Email Service first (domains belong to email service)
        var emailService = new EmailService($"{args.Prefix}-email", new EmailServiceArgs
        {
            EmailServiceName = $"{args.Prefix}-email",
            ResourceGroupName = args.ResourceGroup.Name,
            DataLocation = "Europe",
            Location = "Global",
            Tags = { { "managed-by", "pulumi" } },
        });

        // 2. Create Domain - Azure-managed or custom
        Output<string> mailFrom;
        Output<string>? verificationRecords = null;
        Domain domain;

        if (args.UseCustomDomain)
        {
            domain = new Domain($"{args.Prefix}-email-domain", new DomainArgs
            {
                DomainName = args.Domain,
                EmailServiceName = emailService.Name,
                ResourceGroupName = args.ResourceGroup.Name,
                DomainManagement = DomainManagement.CustomerManaged,
                Location = "Global",
                UserEngagementTracking = UserEngagementTracking.Disabled,
            });

            mailFrom = Output.Create($"noreply@{args.Domain}");

            verificationRecords = domain.VerificationRecords.Apply(vr =>
            {
                var records = new System.Text.Json.Nodes.JsonObject();
                if (vr?.Domain != null)
                    records["domain"] = System.Text.Json.Nodes.JsonNode.Parse(
                        $"{{\"type\":\"{vr.Domain.Type}\",\"name\":\"{vr.Domain.Name}\",\"value\":\"{vr.Domain.Value}\",\"ttl\":{vr.Domain.Ttl}}}");
                if (vr?.SPF != null)
                    records["spf"] = System.Text.Json.Nodes.JsonNode.Parse(
                        $"{{\"type\":\"{vr.SPF.Type}\",\"name\":\"{vr.SPF.Name}\",\"value\":\"{vr.SPF.Value}\",\"ttl\":{vr.SPF.Ttl}}}");
                if (vr?.DKIM != null)
                    records["dkim"] = System.Text.Json.Nodes.JsonNode.Parse(
                        $"{{\"type\":\"{vr.DKIM.Type}\",\"name\":\"{vr.DKIM.Name}\",\"value\":\"{vr.DKIM.Value}\",\"ttl\":{vr.DKIM.Ttl}}}");
                if (vr?.DKIM2 != null)
                    records["dkim2"] = System.Text.Json.Nodes.JsonNode.Parse(
                        $"{{\"type\":\"{vr.DKIM2.Type}\",\"name\":\"{vr.DKIM2.Name}\",\"value\":\"{vr.DKIM2.Value}\",\"ttl\":{vr.DKIM2.Ttl}}}");
                return records.ToJsonString();
            });
        }
        else
        {
            domain = new Domain($"{args.Prefix}-email-domain", new DomainArgs
            {
                DomainName = "AzureManagedDomain",
                EmailServiceName = emailService.Name,
                ResourceGroupName = args.ResourceGroup.Name,
                DomainManagement = DomainManagement.AzureManaged,
                Location = "Global",
                UserEngagementTracking = UserEngagementTracking.Disabled,
            });

            mailFrom = domain.MailFromSenderDomain.Apply(d => $"noreply@{d}");
        }

        // 3. Create a sender username for 'noreply'
        _ = new SenderUsername($"{args.Prefix}-noreply", new SenderUsernameArgs
        {
            SenderUsername = "noreply",
            Username = "noreply",
            DisplayName = "No Reply",
            DomainName = domain.Name,
            EmailServiceName = emailService.Name,
            ResourceGroupName = args.ResourceGroup.Name,
        });

        return new AzureDomainResult
        {
            EmailService = emailService,
            Domain = domain,
            MailFrom = mailFrom,
            VerificationRecords = verificationRecords,
        };
    }

    /// <summary>
    /// Phase 2: Creates the CommunicationService (linked to the domain),
    /// Entra ID App + Service Principal, and SMTP credentials.
    /// For custom domains, the domain must be verified before calling this method.
    /// </summary>
    public static AzureCommunicationResult CreateService(AzureCommunicationServiceArgs args)
    {
        var domainResult = args.DomainResult;

        // 4. Create the Communication Service and link the verified domain
        var communicationService = new CommunicationService($"{args.Prefix}-acs", new CommunicationServiceArgs
        {
            CommunicationServiceName = $"{args.Prefix}-acs",
            ResourceGroupName = args.ResourceGroup.Name,
            DataLocation = "Europe",
            Location = "Global",
            LinkedDomains = { domainResult.Domain.Id },
            Tags = { { "managed-by", "pulumi" } },
        }, args.DependsOn);

        // 5. Create Entra ID Application for SMTP authentication
        var app = new Application($"{args.Prefix}-email-app", new ApplicationArgs
        {
            DisplayName = $"{args.Prefix}-email-smtp",
        });

        // 6. Create Service Principal for the application
        var servicePrincipal = new ServicePrincipal($"{args.Prefix}-email-sp", new ServicePrincipalArgs
        {
            ClientId = app.ClientId,
        });

        // 7. Create client secret (password) for SMTP authentication
        var endDate = DateTime.UtcNow.AddYears(1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var appPassword = new ApplicationPassword($"{args.Prefix}-email-secret", new ApplicationPasswordArgs
        {
            ApplicationId = app.Id,
            DisplayName = "SMTP Auth Secret",
            EndDate = endDate,
        });

        // 8. Get current client config for subscription/tenant info
        var clientConfig = Pulumi.AzureNative.Authorization.GetClientConfig.Invoke();

        // 9. Assign "Communication and Email Service Owner" role to the service principal
        _ = new RoleAssignment($"{args.Prefix}-email-role", new RoleAssignmentArgs
        {
            RoleAssignmentName = Guid.NewGuid().ToString(),
            Scope = communicationService.Id,
            PrincipalId = servicePrincipal.ObjectId,
            PrincipalType = PrincipalType.ServicePrincipal,
            RoleDefinitionId = clientConfig.Apply(c =>
                $"/subscriptions/{c.SubscriptionId}/providers/Microsoft.Authorization/roleDefinitions/{EmailOwnerRoleId}"),
        });

        // 10. Build SMTP credentials
        var smtpUser = Output.Tuple(communicationService.Name, app.ClientId, clientConfig)
            .Apply(t => $"{t.Item1}.{t.Item2}.{t.Item3.TenantId}");

        // 11. Get connection string for REST API / SDK access
        var acsKeys = ListCommunicationServiceKeys.Invoke(new ListCommunicationServiceKeysInvokeArgs
        {
            CommunicationServiceName = communicationService.Name,
            ResourceGroupName = args.ResourceGroup.Name,
        });
        var connectionString = Output.Tuple(communicationService.Name, acsKeys)
            .Apply(t => $"endpoint=https://{t.Item1}.communication.azure.com/;accesskey={t.Item2.PrimaryKey}");

        return new AzureCommunicationResult
        {
            SmtpHost = Output.Create("smtp.azurecomm.net"),
            SmtpUser = smtpUser,
            SmtpPassword = appPassword.Value,
            MailFrom = domainResult.MailFrom,
            EmailServiceId = domainResult.EmailService.Id,
            ConnectionString = connectionString,
        };
    }
}