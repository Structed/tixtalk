using System.Diagnostics;
using Spectre.Console;

namespace TixTalk.Cli;

/// <summary>
/// Shared helpers for running Azure CLI (az) commands.
/// </summary>
public static class AzureCli
{
    /// <summary>
    /// Checks that Azure CLI is installed and working.
    /// </summary>
    public static bool Validate()
    {
        var (exitCode, _) = RunCommand("version", "--output", "none");
        if (exitCode == 0)
            return true;

        AnsiConsole.MarkupLine("[red]Error:[/] Azure CLI (az) not found or not working.");
        AnsiConsole.MarkupLine("Install it from: [blue]https://aka.ms/installazurecli[/]");
        AnsiConsole.MarkupLine("Then run: [yellow]az login[/]");
        return false;
    }

    /// <summary>
    /// Runs an Azure CLI command and returns exit code + captured output.
    /// </summary>
    public static (int ExitCode, string Output) RunCommand(params string[] args)
        => RunCommand(subscription: null, args);

    /// <summary>
    /// Runs an Azure CLI command scoped to a specific subscription.
    /// </summary>
    public static (int ExitCode, string Output) RunCommand(string? subscription, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            var azPath = FindCli();
            if (azPath == null)
                return (1, "Azure CLI not found");

            psi.FileName = azPath;
        }
        else
        {
            psi.FileName = "az";
        }

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        if (!string.IsNullOrWhiteSpace(subscription))
        {
            psi.ArgumentList.Add("--subscription");
            psi.ArgumentList.Add(subscription);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
                return (1, "Failed to start az command");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (1, ex.Message);
        }
    }

    private static string? FindCli()
    {
        // Look for az.exe (not az.cmd) so we can launch directly with UseShellExecute=false
        var possiblePaths = new[]
        {
            @"C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.exe",
            @"C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\wbin\az.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\Azure CLI\wbin\az.exe"),
        };

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
                return path;
        }

        // Try to find via PATH using where.exe
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "az.exe",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadLine();
                process.WaitForExit();
                if (!string.IsNullOrWhiteSpace(output) && File.Exists(output))
                    return output;
            }
        }
        catch
        {
            // Ignore
        }

        return null;
    }
}
