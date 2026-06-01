using Spectre.Console;

namespace TixTalk.Cli;

public static class Menu
{
    public static int Show(AppConfig config, Remote remote)
    {
        if (!config.IsConfigured)
        {
            AnsiConsole.Write(new Rule("[blue]Pretix + Pretalx Manager[/]").RuleStyle("blue"));
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]No server configured yet.[/]");
            AnsiConsole.WriteLine();

            var setupChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]What would you like to do?[/]")
                    .HighlightStyle("green")
                    .AddChoices(
                        "Local development",
                        "Provision new server (Azure)",
                        "Connect to existing server",
                        "Quit"));

            return setupChoice switch
            {
                "Local development" => Dev.Run([]),
                "Provision new server (Azure)" => Provision.Run(),
                "Connect to existing server" => ConnectAndContinue(config, remote),
                _ => 0,
            };
        }

        var domain = remote.GetRemoteDomain();
        var domainLabel = domain != null ? $" [grey]({domain})[/]" : "";

        AnsiConsole.Write(new Rule($"[blue]Pretix + Pretalx Manager[/]{domainLabel}")
            .RuleStyle("blue"));
        AnsiConsole.WriteLine();

        if (domain != null)
        {
            var (ticketsHost, talksHost) = remote.GetRemoteHosts(domain);
            AnsiConsole.MarkupLine($"  [green]Pretix:[/]  https://{ticketsHost}");
            AnsiConsole.MarkupLine($"  [green]Pretalx:[/] https://{talksHost}");
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine($"  [grey]Server:[/] [yellow]{config.Host}[/]");
        AnsiConsole.WriteLine();

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]What would you like to do?[/]")
                .HighlightStyle("green")
                .AddChoiceGroup("Operations", new[]
                {
                    "Status",
                    "Update containers",
                    "Upgrade (code + containers)",
                    "View logs",
                    "Restart services",
                    "Backup databases",
                    "Periodic tasks (cron)",
                    "Restore database",
                    "Open shell",
                    "Update DNS records",
                    "Stop services",
                    "Start services",
                })
                .AddChoiceGroup("Local", new[]
                {
                    "Local development",
                })
                .AddChoiceGroup("Azure SSH Access", new[]
                {
                    "Open SSH access",
                    "Close SSH access",
                    "SSH access status",
                })
                .AddChoiceGroup("First-time setup", new[]
                {
                    "Provision new server (Azure)",
                    "Server setup (install Docker)",
                    "Deploy (first time)",
                })
                .AddChoiceGroup("Configuration", new[]
                {
                    "Change connection",
                    "Configure Azure NSG",
                    "Quit",
                }));

        AnsiConsole.WriteLine();

        return choice switch
        {
            "Status" => remote.RunCommand("status"),
            "Update containers" => PromptUpdate(remote),
            "Upgrade (code + containers)" => remote.RunCommand("upgrade"),
            "View logs" => PromptLogs(remote),
            "Restart services" => remote.RunCommand("restart"),
            "Backup databases" => PromptBackup(remote),
            "Periodic tasks (cron)" => PromptCron(remote),
            "Restore database" => remote.RunInteractive("restore"),
            "Open shell" => PromptShell(remote),
            "Update DNS records" => remote.RunCommand("dns"),
            "Stop services" => remote.RunCommand("stop"),
            "Start services" => remote.RunCommand("start"),
            "Local development" => Dev.Run([]),
            "Open SSH access" => SshAccess.Open(config),
            "Close SSH access" => SshAccess.Close(config),
            "SSH access status" => SshAccess.Status(config),
            "Provision new server (Azure)" => Provision.Run(),
            "Server setup (install Docker)" => remote.RunCommand("setup"),
            "Deploy (first time)" => remote.RunCommand("deploy"),
            "Change connection" => ChangeConnection(config),
            "Configure Azure NSG" => ConfigureAzureNsg(config),
            "Quit" => 0,
            _ => 0,
        };
    }

    private static int PromptUpdate(Remote remote)
    {
        var pretixTag = AnsiConsole.Ask("Pretix tag [grey](leave empty to keep current)[/]:", "");
        var pretalxTag = AnsiConsole.Ask("Pretalx tag [grey](leave empty to keep current)[/]:", "");

        var args = "update";
        if (!string.IsNullOrWhiteSpace(pretixTag))
            args += $" --pretix {pretixTag}";
        if (!string.IsNullOrWhiteSpace(pretalxTag))
            args += $" --pretalx {pretalxTag}";

        return remote.RunCommand(args);
    }

    private static int PromptLogs(Remote remote)
    {
        var service = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Which service?")
                .AddChoices("All services", "pretix", "pretalx", "caddy", "postgres", "redis", "cron", "backup-cron"));

        var arg = service == "All services" ? "" : service;
        return remote.RunInteractive($"logs {arg}");
    }

    private static int PromptShell(Remote remote)
    {
        var service = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Open shell in which container?")
                .AddChoices("pretix", "pretalx", "postgres", "redis", "caddy"));

        return remote.RunInteractive($"shell {service}");
    }

    private static int PromptBackup(Remote remote)
    {
        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Backup action:")
                .AddChoices("Run backup now", "Install daily cron job"));

        return action == "Install daily cron job"
            ? remote.RunCommand("backup --install-cron")
            : remote.RunCommand("backup");
    }

    private static int PromptCron(Remote remote)
    {
        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Periodic tasks:")
                .AddChoices("Run periodic tasks now", "Install cron job (every 5 min)", "Remove cron job"));

        return action switch
        {
            "Install cron job (every 5 min)" => remote.RunCommand("cron --install"),
            "Remove cron job" => remote.RunCommand("cron --remove"),
            _ => remote.RunCommand("cron"),
        };
    }

    private static int ChangeConnection(AppConfig config)
    {
        config.RunConnect(null);
        return 0;
    }
    
    private static int ConfigureAzureNsg(AppConfig config)
    {
        SshAccess.Configure(config);
        return 0;
    }

    private static int ConnectAndContinue(AppConfig config, Remote remote)
    {
        config.RunConnect(null);
        AnsiConsole.WriteLine();
        return Show(config, remote);
    }
}
