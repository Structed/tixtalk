using System.Diagnostics;
using Spectre.Console;

namespace TixTalk.Cli;

/// <summary>
/// Manages the local development environment via Docker Compose.
/// </summary>
public static class Dev
{
    private static readonly string[] ComposeArgs =
    [
        "compose",
        "-f", "docker-compose.yml",
        "-f", "docker-compose.local.yml",
        "--env-file", ".env.local",
    ];

    public static int Run(string[] args)
    {
        var repoDir = FindRepoRoot();
        if (repoDir == null)
        {
            AnsiConsole.MarkupLine("[red]Could not find project root (docker-compose.yml).[/]");
            AnsiConsole.MarkupLine("[grey]Run from the tixtalk repository directory, or ensure docker-compose.yml is in a parent directory.[/]");
            return 1;
        }

        if (!File.Exists(Path.Combine(repoDir, ".env.local")))
        {
            AnsiConsole.MarkupLine("[red].env.local not found.[/] Copy the template first:");
            AnsiConsole.MarkupLine("[grey]  The file should already exist in the repo.[/]");
            return 1;
        }

        if (args.Length == 0)
            return ShowMenu(repoDir);

        var subCommand = args[0].ToLowerInvariant();
        var subArgs = args.Skip(1).ToArray();

        return subCommand switch
        {
            "up" => Up(repoDir, subArgs),
            "down" => Down(repoDir, subArgs),
            "wipe" or "reset" => DownWithVolumes(repoDir),
            "status" or "ps" => Status(repoDir),
            "logs" => Logs(repoDir, subArgs),
            "shell" => Shell(repoDir, subArgs),
            "superuser" => SuperUser(repoDir, subArgs),
            "restart" => Restart(repoDir),
            "stop" => RunCompose(repoDir, ["stop"]),
            "start" => RunCompose(repoDir, ["start"]),
            "help" or "-h" or "--help" => ShowDevHelp(),
            _ => UnknownSubCommand(subCommand),
        };
    }

    private static int ShowMenu(string repoDir)
    {
        AnsiConsole.Write(new Rule("[blue]Local Development[/]").RuleStyle("blue"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [green]Pretix:[/]  http://localhost:8000");
        AnsiConsole.MarkupLine("  [green]Pretalx:[/] http://localhost:8001");
        AnsiConsole.WriteLine();

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]What would you like to do?[/]")
                .HighlightStyle("green")
                .AddChoices(
                    "Start (up -d)",
                    "Stop (down)",
                    "Wipe all data (reset)",
                    "Status",
                    "View logs",
                    "Restart",
                    "Open shell",
                    "Create superuser",
                    "Quit"));

        AnsiConsole.WriteLine();

        return choice switch
        {
            "Start (up -d)" => Up(repoDir, []),
            "Stop (down)" => Down(repoDir, []),
            "Wipe all data (reset)" => DownWithVolumes(repoDir),
            "Status" => Status(repoDir),
            "View logs" => PromptLogs(repoDir),
            "Restart" => Restart(repoDir),
            "Open shell" => PromptShell(repoDir),
            "Create superuser" => PromptSuperUser(repoDir),
            "Quit" => 0,
            _ => 0,
        };
    }

    private static int Up(string repoDir, string[] extraArgs)
    {
        AnsiConsole.MarkupLine("[yellow]Starting local dev environment...[/]");
        AnsiConsole.MarkupLine("  [green]Pretix:[/]  http://localhost:8000");
        AnsiConsole.MarkupLine("  [green]Pretalx:[/] http://localhost:8001");
        AnsiConsole.WriteLine();

        var args = new List<string> { "up", "-d" };
        args.AddRange(extraArgs);
        return RunCompose(repoDir, args.ToArray());
    }

    private static int Down(string repoDir, string[] extraArgs)
    {
        var args = new List<string> { "down" };
        args.AddRange(extraArgs);
        return RunCompose(repoDir, args.ToArray());
    }

    private static int DownWithVolumes(string repoDir)
    {
        AnsiConsole.MarkupLine("[yellow]This will remove all data (databases, uploads, etc.).[/]");
        if (!AnsiConsole.Confirm("Are you sure?", false))
        {
            AnsiConsole.MarkupLine("[grey]Aborted.[/]");
            return 0;
        }
        return RunCompose(repoDir, ["down", "-v"]);
    }

    private static int Status(string repoDir)
    {
        return RunCompose(repoDir, ["ps"]);
    }

    private static int Logs(string repoDir, string[] extraArgs)
    {
        var args = new List<string> { "logs", "-f", "--tail", "50" };
        args.AddRange(extraArgs);
        return RunCompose(repoDir, args.ToArray());
    }

    private static int Restart(string repoDir)
    {
        return RunCompose(repoDir, ["restart"]);
    }

    // Alpine-based images (redis, caddy, postgres) don't have bash
    private static readonly HashSet<string> AlpineServices = new(StringComparer.OrdinalIgnoreCase)
        { "redis", "caddy", "postgres" };

    private static int Shell(string repoDir, string[] extraArgs)
    {
        var service = extraArgs.Length > 0 ? extraArgs[0] : "pretix";
        var shell = AlpineServices.Contains(service) ? "sh" : "bash";
        return RunCompose(repoDir, ["exec", service, shell]);
    }

    private static int SuperUser(string repoDir, string[] extraArgs)
    {
        var service = extraArgs.Length > 0 ? extraArgs[0] : "both";

        if (service is not "pretix" and not "pretalx" and not "both")
        {
            AnsiConsole.MarkupLine($"[red]Unknown service '{Markup.Escape(service)}'. Use 'pretix', 'pretalx', or 'both'.[/]");
            return 1;
        }

        var exitCode = 0;

        if (service is "pretix" or "both")
        {
            AnsiConsole.Write(new Rule("[green]Pretix Superuser[/]").RuleStyle("green"));
            AnsiConsole.MarkupLine("[grey]Creating admin account for Pretix...[/]");
            AnsiConsole.WriteLine();
            var result = RunCompose(repoDir, ["exec", "pretix", "pretix", "createsuperuser"]);
            if (result != 0) exitCode = result;
        }

        if (service is "pretalx" or "both")
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[green]Pretalx Superuser[/]").RuleStyle("green"));
            AnsiConsole.MarkupLine("[grey]Creating admin account for Pretalx...[/]");
            AnsiConsole.WriteLine();
            var result = RunCompose(repoDir, ["exec", "pretalx", "python", "-m", "pretalx", "createsuperuser"]);
            if (result != 0) exitCode = result;
        }

        return exitCode;
    }

    private static int PromptSuperUser(string repoDir)
    {
        var service = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Create superuser for which service?")
                .AddChoices("Both (Pretix + Pretalx)", "Pretix only", "Pretalx only"));

        var arg = service switch
        {
            "Pretix only" => "pretix",
            "Pretalx only" => "pretalx",
            _ => "both",
        };

        return SuperUser(repoDir, [arg]);
    }

    private static int PromptLogs(string repoDir)
    {
        var service = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Which service?")
                .AddChoices("All services", "pretix", "pretalx", "caddy", "postgres", "redis"));

        var args = service == "All services" ? [] : new[] { service };
        return Logs(repoDir, args);
    }

    private static int PromptShell(string repoDir)
    {
        var service = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Open shell in which container?")
                .AddChoices("pretix", "pretalx", "postgres", "redis", "caddy"));

        return Shell(repoDir, [service]);
    }

    private static int RunCompose(string repoDir, string[] extraArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            WorkingDirectory = repoDir,
            UseShellExecute = false,
        };

        foreach (var arg in ComposeArgs)
            psi.ArgumentList.Add(arg);
        foreach (var arg in extraArgs)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                AnsiConsole.MarkupLine("[red]Failed to start docker process.[/]");
                return 1;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to run docker compose:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[grey]Ensure Docker is installed and running.[/]");
            return 1;
        }
    }

    private static string? FindRepoRoot()
    {
        // Try current directory first, then walk up
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "docker-compose.yml")) &&
                File.Exists(Path.Combine(dir, "docker-compose.local.yml")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        // Try relative to the executable
        var exeDir = AppContext.BaseDirectory;
        dir = exeDir;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "docker-compose.yml")) &&
                File.Exists(Path.Combine(dir, "docker-compose.local.yml")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }

    private static int ShowDevHelp()
    {
        AnsiConsole.Write(new Rule("[blue]Local Development[/]").RuleStyle("blue"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Usage:[/] tixtalk dev [green]<command>[/] [grey][[options]][/]");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Command[/]")
            .AddColumn("[bold]Description[/]");

        table.AddRow("[green]up[/]", "Start the local dev environment (detached)");
        table.AddRow("[green]down[/]", "Stop and remove containers");
        table.AddRow("[green]wipe[/]", "Stop, remove containers, and delete all data (clean slate)");
        table.AddRow("[green]status[/]", "Show container status");
        table.AddRow("[green]logs[/] [[service]]", "Follow container logs (all or specific service)");
        table.AddRow("[green]shell[/] [[service]]", "Open bash in a container (default: pretix)");
        table.AddRow("[green]superuser[/] [[pretix|pretalx]]", "Create admin superuser (default: both)");
        table.AddRow("[green]restart[/]", "Restart all services");
        table.AddRow("[green]stop[/]", "Stop services (keep containers)");
        table.AddRow("[green]start[/]", "Start stopped services");

        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Run [yellow]tixtalk dev[/] without arguments for an interactive menu.");
        AnsiConsole.MarkupLine("[grey]Endpoints: http://localhost:8000 (Pretix) | http://localhost:8001 (Pretalx)[/]");

        return 0;
    }

    private static int UnknownSubCommand(string cmd)
    {
        AnsiConsole.MarkupLine($"[red]Unknown dev subcommand:[/] {Markup.Escape(cmd)}");
        AnsiConsole.MarkupLine("Run [yellow]tixtalk dev help[/] for usage.");
        return 1;
    }
}
