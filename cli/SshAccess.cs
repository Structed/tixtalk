using System.Net.Http;
using Spectre.Console;

namespace TixTalk.Cli;

/// <summary>
/// Manages SSH access to Azure VMs by updating NSG rules via Azure CLI.
/// </summary>
public static class SshAccess
{
    private const string SshRuleName = "AllowSSH";
    
    /// <summary>
    /// Opens SSH access from the current public IP or a specified CIDR.
    /// </summary>
    public static int Open(AppConfig config, string? cidr = null)
    {
        if (!ValidateAzureConfig(config))
            return 1;
        
        if (!AzureCli.Validate())
            return 1;
        
        var sourceAddress = cidr;
        if (string.IsNullOrWhiteSpace(sourceAddress))
        {
            sourceAddress = GetCurrentPublicIp();
            if (string.IsNullOrWhiteSpace(sourceAddress))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Could not determine your public IP address.");
                AnsiConsole.MarkupLine("Try specifying a CIDR manually: [yellow]tixtalk ssh open 1.2.3.4/32[/]");
                return 1;
            }
            
            // Ensure it's a CIDR if it's just an IP
            if (!sourceAddress.Contains('/'))
                sourceAddress = $"{sourceAddress}/32";
        }
        
        AnsiConsole.MarkupLine($"Opening SSH access from [yellow]{sourceAddress}[/]...");
        
        var sub = config.SubscriptionId;
        
        var (exitCode, output) = AzureCli.RunCommand(sub,
            "network", "nsg", "rule", "update",
            "--resource-group", config.ResourceGroup,
            "--nsg-name", config.NsgName,
            "--name", SshRuleName,
            "--access", "Allow",
            "--source-address-prefixes", sourceAddress
        );
        
        if (exitCode == 0)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] SSH access opened from [yellow]{sourceAddress}[/]");
            AnsiConsole.MarkupLine($"  Connect with: [blue]ssh azureuser@{config.ParseHost().Hostname}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Failed to update NSG rule.");
            if (!string.IsNullOrWhiteSpace(output))
                AnsiConsole.WriteLine(output);
        }
        
        return exitCode;
    }
    
    /// <summary>
    /// Closes SSH access by setting the rule to deny.
    /// </summary>
    public static int Close(AppConfig config)
    {
        if (!ValidateAzureConfig(config))
            return 1;
        
        if (!AzureCli.Validate())
            return 1;
        
        AnsiConsole.MarkupLine("Closing SSH access...");
        
        var (exitCode, output) = AzureCli.RunCommand(config.SubscriptionId,
            "network", "nsg", "rule", "update",
            "--resource-group", config.ResourceGroup,
            "--nsg-name", config.NsgName,
            "--name", SshRuleName,
            "--access", "Deny"
        );
        
        if (exitCode == 0)
        {
            AnsiConsole.MarkupLine("[green]✓[/] SSH access closed (rule set to Deny)");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Failed to update NSG rule.");
            if (!string.IsNullOrWhiteSpace(output))
                AnsiConsole.WriteLine(output);
        }
        
        return exitCode;
    }
    
    /// <summary>
    /// Shows the current SSH access status.
    /// </summary>
    public static int Status(AppConfig config)
    {
        if (!ValidateAzureConfig(config))
            return 1;
        
        if (!AzureCli.Validate())
            return 1;
        
        var (exitCode, output) = AzureCli.RunCommand(config.SubscriptionId,
            "network", "nsg", "rule", "show",
            "--resource-group", config.ResourceGroup,
            "--nsg-name", config.NsgName,
            "--name", SshRuleName,
            "--query", "{access:access, sourceAddressPrefixes:sourceAddressPrefixes, sourceAddressPrefix:sourceAddressPrefix}",
            "--output", "json"
        );
        
        if (exitCode != 0)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Failed to get NSG rule status.");
            if (!string.IsNullOrWhiteSpace(output))
                AnsiConsole.WriteLine(output);
            return exitCode;
        }
        
        // Parse the JSON output
        try
        {
            var json = System.Text.Json.JsonDocument.Parse(output);
            var root = json.RootElement;
            
            var access = root.GetProperty("access").GetString() ?? "Unknown";
            var isAllowed = access.Equals("Allow", StringComparison.OrdinalIgnoreCase);
            
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Property")
                .AddColumn("Value");
            
            table.AddRow("Resource Group", $"[blue]{config.ResourceGroup}[/]");
            table.AddRow("NSG Name", $"[blue]{config.NsgName}[/]");
            table.AddRow("Rule Name", SshRuleName);
            table.AddRow("Access", isAllowed ? "[green]Allow[/]" : "[red]Deny[/]");
            
            // Get source addresses
            var sources = new List<string>();
            if (root.TryGetProperty("sourceAddressPrefixes", out var prefixes) && prefixes.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var prefix in prefixes.EnumerateArray())
                {
                    var val = prefix.GetString();
                    if (!string.IsNullOrEmpty(val))
                        sources.Add(val);
                }
            }
            if (root.TryGetProperty("sourceAddressPrefix", out var singlePrefix) && singlePrefix.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var val = singlePrefix.GetString();
                if (!string.IsNullOrEmpty(val) && val != "*")
                    sources.Add(val);
                else if (val == "*")
                    sources.Add("[yellow]* (any)[/]");
            }
            
            if (sources.Count > 0)
                table.AddRow("Allowed Sources", string.Join(", ", sources));
            else if (isAllowed)
                table.AddRow("Allowed Sources", "[yellow]* (any)[/]");
            
            AnsiConsole.Write(table);
            
            if (isAllowed)
                AnsiConsole.MarkupLine("\n[green]SSH access is currently OPEN[/]");
            else
                AnsiConsole.MarkupLine("\n[red]SSH access is currently CLOSED[/]");
        }
        catch
        {
            // Fallback: just show raw output
            AnsiConsole.WriteLine(output);
        }
        
        return 0;
    }
    
    /// <summary>
    /// Prompts the user to configure Azure resource info.
    /// </summary>
    public static void Configure(AppConfig config)
    {
        AnsiConsole.Write(new Rule("[blue]Azure NSG Configuration[/]").RuleStyle("blue"));
        AnsiConsole.MarkupLine("Configure Azure resource info to enable SSH access control.\n");
        
        var subscriptionId = AnsiConsole.Ask(
            "Subscription ID ([grey]optional[/]):",
            string.IsNullOrWhiteSpace(config.SubscriptionId) ? "" : config.SubscriptionId);
        config.SubscriptionId = subscriptionId.Trim();
        
        var resourceGroup = AnsiConsole.Ask(
            "Resource Group name:", 
            string.IsNullOrWhiteSpace(config.ResourceGroup) ? "" : config.ResourceGroup);
        config.ResourceGroup = resourceGroup;
        
        var nsgName = AnsiConsole.Ask(
            "NSG name:", 
            string.IsNullOrWhiteSpace(config.NsgName) ? "" : config.NsgName);
        config.NsgName = nsgName;
        
        config.Save();
        
        AnsiConsole.MarkupLine($"\n[green]✓[/] Azure config saved");
        if (!string.IsNullOrWhiteSpace(config.SubscriptionId))
            AnsiConsole.MarkupLine($"  Subscription: [yellow]{config.SubscriptionId}[/]");
        AnsiConsole.MarkupLine($"  Resource Group: [yellow]{config.ResourceGroup}[/]");
        AnsiConsole.MarkupLine($"  NSG Name: [yellow]{config.NsgName}[/]");
    }
    
    private static bool ValidateAzureConfig(AppConfig config)
    {
        if (config.HasAzureConfig)
            return true;
        
        AnsiConsole.MarkupLine("[red]Error:[/] Azure resource info not configured.");
        AnsiConsole.MarkupLine("Run [yellow]tixtalk ssh config[/] to set up Azure resource info,");
        AnsiConsole.MarkupLine("or run [yellow]tixtalk provision[/] to deploy a new Azure VM.");
        return false;
    }
    
    private static string? GetCurrentPublicIp()
    {
        // Try services that return IPv4 (Azure NSG doesn't support IPv6 in all configurations)
        var ipv4Services = new[]
        {
            "https://api.ipify.org",        // Always IPv4
            "https://ipv4.icanhazip.com",   // Explicitly IPv4
            "https://checkip.amazonaws.com", // IPv4
        };
        
        foreach (var service in ipv4Services)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var response = client.GetStringAsync(service).GetAwaiter().GetResult();
                var ip = response.Trim();
                
                // Verify it looks like IPv4 (contains dots, no colons)
                if (ip.Contains('.') && !ip.Contains(':'))
                    return ip;
            }
            catch
            {
                // Try next service
            }
        }
        
        return null;
    }
}
