using Spectre.Console;
using PreTalxTix.Cli;

var config = AppConfig.Load();
var remote = new Remote(config);

if (args.Length == 0)
{
    return Menu.Show(config, remote);
}

var command = args[0].ToLowerInvariant();
var commandArgs = args.Skip(1).ToArray();

return command switch
{
    "provision" => Provision.Run(),
    "connect" => Connect(commandArgs),
    "ssh" => SshCommand(commandArgs),
    "status" => remote.RunCommand("status"),
    "deploy" => remote.RunCommand("deploy"),
    "setup" => remote.RunCommand("setup"),
    "update" => remote.RunCommand($"update {string.Join(' ', commandArgs)}"),
    "logs" => remote.RunInteractive($"logs {string.Join(' ', commandArgs)}"),
    "backup" => remote.RunCommand($"backup {string.Join(' ', commandArgs)}"),
    "restore" => remote.RunInteractive($"restore {string.Join(' ', commandArgs)}"),
    "shell" => remote.RunInteractive($"shell {string.Join(' ', commandArgs)}"),
    "dns" => remote.RunCommand("dns"),
    "restart" => remote.RunCommand("restart"),
    "stop" => remote.RunCommand("stop"),
    "start" => remote.RunCommand("start"),
    "help" or "-h" or "--help" => ShowHelp(),
    _ => Unknown(command),
};

int Connect(string[] cArgs)
{
    config.RunConnect(cArgs.Length > 0 ? cArgs[0] : null);
    return 0;
}

int SshCommand(string[] sshArgs)
{
    if (sshArgs.Length == 0)
    {
        AnsiConsole.MarkupLine("[red]Error:[/] Missing subcommand. Usage:");
        AnsiConsole.MarkupLine("  [yellow]ptx ssh open[/]        - Open SSH from your current IP");
        AnsiConsole.MarkupLine("  [yellow]ptx ssh open <cidr>[/] - Open SSH from specified CIDR");
        AnsiConsole.MarkupLine("  [yellow]ptx ssh close[/]       - Close SSH access");
        AnsiConsole.MarkupLine("  [yellow]ptx ssh status[/]      - Show current SSH access status");
        AnsiConsole.MarkupLine("  [yellow]ptx ssh config[/]      - Configure Azure resource info");
        return 1;
    }
    
    var subCommand = sshArgs[0].ToLowerInvariant();
    return subCommand switch
    {
        "open" => SshAccess.Open(config, sshArgs.Length > 1 ? sshArgs[1] : null),
        "close" => SshAccess.Close(config),
        "status" => SshAccess.Status(config),
        "config" => ConfigureSsh(),
        _ => UnknownSshCommand(subCommand)
    };
    
    int ConfigureSsh()
    {
        SshAccess.Configure(config);
        return 0;
    }
    
    int UnknownSshCommand(string cmd)
    {
        AnsiConsole.MarkupLine($"[red]Unknown ssh subcommand:[/] {Markup.Escape(cmd)}");
        AnsiConsole.MarkupLine("Valid subcommands: [yellow]open[/], [yellow]close[/], [yellow]status[/], [yellow]config[/]");
        return 1;
    }
}

int ShowHelp()
{
    AnsiConsole.Write(new Rule("[blue]Pretix + Pretalx CLI[/]").RuleStyle("blue"));
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold]Usage:[/] ptx [green]<command>[/] [grey][[options]][/]");
    AnsiConsole.WriteLine();

    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("[bold]Command[/]")
        .AddColumn("[bold]Description[/]");

    table.AddRow("[green]provision[/]", "Provision a new Azure VM (interactive wizard)");
    table.AddRow("[green]connect[/] <user@host>", "Configure SSH connection to the server");
    table.AddRow("[green]ssh[/] <open|close|status|config>", "Control Azure NSG SSH access");
    table.AddRow("[green]status[/]", "Show service status, URLs, and disk usage");
    table.AddRow("[green]deploy[/]", "First-time deployment (generates secrets, starts services)");
    table.AddRow("[green]setup[/]", "Install Docker & configure firewall on the server");
    table.AddRow("[green]update[/] [[--pretix TAG]] [[--pretalx TAG]]", "Pull latest images and restart");
    table.AddRow("[green]logs[/] [[service]]", "Tail service logs (Ctrl+C to stop)");
    table.AddRow("[green]backup[/] [[--install-cron]]", "Back up databases (or install daily cron)");
    table.AddRow("[green]restore[/]", "Restore database from backup (interactive)");
    table.AddRow("[green]shell[/] [[service]]", "Open a shell in a container (default: pretix)");
    table.AddRow("[green]dns[/]", "Create/update Cloudflare DNS records");
    table.AddRow("[green]restart[/]", "Restart all services");
    table.AddRow("[green]stop[/]", "Stop all services");
    table.AddRow("[green]start[/]", "Start all services");
    table.AddRow("[green]help[/]", "Show this help message");

    AnsiConsole.Write(table);

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("Run [yellow]ptx[/] without arguments for an interactive menu.");
    AnsiConsole.MarkupLine($"Config: [grey]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pretalxtix", "config.json")}[/]");

    return 0;
}

int Unknown(string cmd)
{
    AnsiConsole.MarkupLine($"[red]Unknown command:[/] {Markup.Escape(cmd)}");
    AnsiConsole.MarkupLine("Run [yellow]ptx help[/] for usage.");
    return 1;
}
