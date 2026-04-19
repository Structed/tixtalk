using Pulumi;
using Pulumi.Command.Local;

namespace TixTalk.Infra.Helpers;

public record DomainVerificationArgs
{
    public required string Prefix { get; init; }
    public required string DomainName { get; init; }
    public required Output<string> EmailServiceName { get; init; }
    public required Output<string> ResourceGroupName { get; init; }
    /// <summary>
    /// Explicit dependencies that must complete before verification starts
    /// (e.g., DNS record resources).
    /// </summary>
    public required Resource[] DependsOn { get; init; }
}

/// <summary>
/// Uses Pulumi.Command.Local to initiate and poll ACS domain verification
/// (Domain, SPF, DKIM, DKIM2) via the Azure CLI.
/// Requires <c>az</c> CLI to be installed and logged in on the machine running <c>pulumi up</c>.
/// Uses PowerShell (pwsh) for cross-platform compatibility (Windows, macOS, Linux).
/// </summary>
public static class DomainVerificationCommand
{
    private const int TimeoutMinutes = 30;
    private const int PollIntervalSeconds = 20;

    /// <summary>
    /// Creates a local command resource that initiates and waits for all four
    /// ACS domain verifications to complete. Returns a <see cref="Command"/>
    /// resource that can be used as a dependency for downstream resources.
    /// </summary>
    public static Command Create(DomainVerificationArgs args)
    {
        var script = Output.Tuple(args.EmailServiceName, args.ResourceGroupName)
            .Apply(t => BuildVerificationScript(args.DomainName, t.Item1, t.Item2));

        return new Command($"{args.Prefix}-domain-verify", new CommandArgs
        {
            Create = script,
            Interpreter = new InputList<string> { "pwsh", "-NonInteractive", "-Command" },
            Triggers = new[]
            {
                args.EmailServiceName.Apply(n => (object)n),
                args.ResourceGroupName.Apply(n => (object)n),
                Output.Create((object)args.DomainName),
            },
        }, new CustomResourceOptions
        {
            DependsOn = args.DependsOn,
        });
    }

    private static string BuildVerificationScript(string domainName, string emailServiceName, string resourceGroupName)
    {
        var maxAttempts = (TimeoutMinutes * 60) / PollIntervalSeconds;

        return $@"
$ErrorActionPreference = 'Stop'

$Domain = '{domainName}'
$EmailService = '{emailServiceName}'
$ResourceGroup = '{resourceGroupName}'
$MaxAttempts = {maxAttempts}
$PollInterval = {PollIntervalSeconds}
$Types = @('Domain', 'SPF', 'DKIM', 'DKIM2')

Write-Host ""Initiating ACS domain verification for $Domain...""

foreach ($Type in $Types) {{
    Write-Host ""  Initiating $Type verification...""
    az communication email domain initiate-verification `
        --domain-name $Domain `
        --email-service-name $EmailService `
        --resource-group $ResourceGroup `
        --verification-type $Type 2>$null
    if ($LASTEXITCODE -ne 0) {{
        Write-Host ""  Warning: initiate-verification for $Type returned exit code $LASTEXITCODE (may already be in progress)""
    }}
}}

Write-Host ""Polling for verification completion (timeout: {TimeoutMinutes} minutes)...""

for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {{
    $allVerified = $true
    $statusSummary = @()

    foreach ($Type in $Types) {{
        $status = az communication email domain show `
            --domain-name $Domain `
            --email-service-name $EmailService `
            --resource-group $ResourceGroup `
            --query ""verificationStates.$Type.status"" -o tsv 2>$null

        if ($status -eq 'Verified') {{
            $statusSummary += ""$Type=OK""
        }} else {{
            $allVerified = $false
            $statusSummary += ""$Type=$status""

            # Re-initiate if verification is NotStarted or Failed
            if ($status -eq 'NotStarted' -or $status -eq 'Failed') {{
                Write-Host ""  Re-initiating $Type verification (status: $status)...""
                az communication email domain initiate-verification `
                    --domain-name $Domain `
                    --email-service-name $EmailService `
                    --resource-group $ResourceGroup `
                    --verification-type $Type 2>$null | Out-Null
            }}
        }}
    }}

    if ($allVerified) {{
        Write-Host ""All verifications passed: $($statusSummary -join ', ')""
        exit 0
    }}

    Write-Host ""  Attempt $attempt/$MaxAttempts - $($statusSummary -join ', ')""
    Start-Sleep -Seconds $PollInterval
}}

Write-Host ""ERROR: Domain verification timed out after {TimeoutMinutes} minutes.""
Write-Host ""Check status: az communication email domain show --domain-name $Domain --email-service-name $EmailService --resource-group $ResourceGroup --query verificationStates""
exit 1
";
    }
}
