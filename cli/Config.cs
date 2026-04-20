using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace TixTalk.Cli;

[JsonSerializable(typeof(AppConfig))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class AppConfigJsonContext : JsonSerializerContext;

public sealed class AppConfig
{
    public string Host { get; set; } = "";
    public string KeyFile { get; set; } = "";
    public string ProjectDir { get; set; } = "~/tixtalk";
    
    // Azure resource info (for SSH access control)
    public string ResourceGroup { get; set; } = "";
    public string NsgName { get; set; } = "";
    public string SubscriptionId { get; set; } = "";

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".tixtalk");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    public static AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new AppConfig();

        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig) ?? new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, AppConfigJsonContext.Default.AppConfig);
        File.WriteAllText(ConfigPath, json);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
    
    public bool HasAzureConfig => !string.IsNullOrWhiteSpace(ResourceGroup) && !string.IsNullOrWhiteSpace(NsgName);

    public (string User, string Hostname) ParseHost()
    {
        if (Host.Contains('@'))
        {
            var parts = Host.Split('@', 2);
            return (parts[0], parts[1]);
        }
        return ("root", Host);
    }

    public void RunConnect(string? hostArg)
    {
        var host = hostArg;
        if (string.IsNullOrWhiteSpace(host))
        {
            host = AnsiConsole.Ask<string>("SSH connection ([green]user@host[/]):");
        }

        Host = host;

        var keyFile = AnsiConsole.Ask("SSH key file ([grey]leave empty for ssh-agent[/]):", "");
        if (!string.IsNullOrWhiteSpace(keyFile))
            KeyFile = keyFile;

        var projectDir = AnsiConsole.Ask("Remote project directory:", ProjectDir);
        ProjectDir = projectDir;

        Save();

        AnsiConsole.MarkupLine($"[green]✓[/] Saved to [blue]{ConfigPath}[/]");
        AnsiConsole.MarkupLine($"  Host: [yellow]{Host}[/]");
        if (!string.IsNullOrWhiteSpace(KeyFile))
            AnsiConsole.MarkupLine($"  Key:  [yellow]{KeyFile}[/]");
        AnsiConsole.MarkupLine($"  Dir:  [yellow]{ProjectDir}[/]");
    }
}
