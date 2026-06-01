using System.Diagnostics;
using Spectre.Console;

namespace TixTalk.Cli;

/// <summary>
/// Tears down a Pulumi stack by running <c>pulumi destroy</c>.
/// </summary>
public static class Teardown
{
    public static int Run()
    {
        AnsiConsole.Write(new Rule("[red]Teardown Environment[/]").RuleStyle("red"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]This will destroy all Azure resources for the selected stack.[/]");
        AnsiConsole.WriteLine();

        // Select stack
        var stack = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Which [green]stack[/] to tear down?")
                .AddChoices(new[] { "dev", "prod" }));

        if (stack == "prod")
        {
            AnsiConsole.MarkupLine("[red bold]WARNING: You are about to destroy the PRODUCTION environment![/]");
            if (!AnsiConsole.Confirm("[red]Are you absolutely sure?[/]", false))
            {
                AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                return 0;
            }
        }

        if (!AnsiConsole.Confirm($"Destroy all resources in the [yellow]{stack}[/] stack?", false))
        {
            AnsiConsole.MarkupLine("[grey]Aborted.[/]");
            return 0;
        }

        // Find infra directory
        var infraDir = FindInfraDir();
        if (infraDir == null)
        {
            AnsiConsole.MarkupLine("[red]Could not find infra/ directory with Pulumi.yaml.[/]");
            AnsiConsole.MarkupLine("[grey]The teardown command requires the tixtalk repository checkout.[/]");
            AnsiConsole.MarkupLine("[grey]Clone the repo and run from within it, or cd to the repo root.[/]");
            return 1;
        }

        // Select the stack
        AnsiConsole.MarkupLine($"[grey]Selecting stack: {stack}[/]");
        if (RunPulumi($"stack select {stack}", infraDir) != 0)
        {
            AnsiConsole.MarkupLine($"[red]Stack '{stack}' not found. Has it been provisioned?[/]");
            return 1;
        }

        // Run pulumi destroy
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Running pulumi destroy...[/]");
        AnsiConsole.WriteLine();

        var result = RunPulumi("destroy --yes", infraDir);
        if (result != 0)
        {
            AnsiConsole.MarkupLine("[red]Teardown failed.[/] Check the output above.");
            return 1;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]✓[/] Stack [yellow]{stack}[/] destroyed successfully.");
        AnsiConsole.MarkupLine("[grey]All Azure resources have been removed.[/]");

        // Warn about stale connection config
        var config = AppConfig.Load();
        if (config.IsConfigured)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[yellow]Note:[/] CLI config still points to [yellow]{config.Host}[/].");
            AnsiConsole.MarkupLine("Run [blue]tixtalk connect[/] to reconfigure or provision a new server.");
        }

        return 0;
    }

    private static string? FindInfraDir()
    {
        // Walk up from current directory
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            var infraPath = Path.Combine(dir, "infra");
            if (Directory.Exists(infraPath) && File.Exists(Path.Combine(infraPath, "Pulumi.yaml")))
                return Path.GetFullPath(infraPath);
            dir = Directory.GetParent(dir)?.FullName;
        }

        // Walk up from the executable location
        dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var infraPath = Path.Combine(dir, "infra");
            if (Directory.Exists(infraPath) && File.Exists(Path.Combine(infraPath, "Pulumi.yaml")))
                return Path.GetFullPath(infraPath);
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        return null;
    }

    private static int RunPulumi(string arguments, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pulumi",
            WorkingDirectory = workingDir,
            UseShellExecute = false,
        };

        foreach (var arg in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(arg);

        try
        {
            var process = Process.Start(psi);
            process?.WaitForExit();
            return process?.ExitCode ?? 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to run pulumi:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}
