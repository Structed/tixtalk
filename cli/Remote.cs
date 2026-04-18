using System.Diagnostics;
using Renci.SshNet;
using Spectre.Console;

namespace PreTalxTix.Cli;

public sealed class Remote
{
    private readonly AppConfig _config;

    public Remote(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Run a manage.sh subcommand (non-interactive, streams output).
    /// Prefers native ssh when an SSH agent is available (1Password, ssh-agent, Pageant).
    /// Falls back to SSH.NET when no agent is detected and keys are unencrypted.
    /// </summary>
    public int RunCommand(string manageArgs)
    {
        EnsureConfigured();

        if (IsSshAgentAvailable())
            return RunNativeSshCommand(manageArgs);

        // Try SSH.NET for non-agent scenarios (unencrypted keys, password auth)
        var cmd = $"cd {_config.ProjectDir} && ./manage.sh {manageArgs}";
        var (user, hostname) = _config.ParseHost();

        using var client = CreateSshClient(user, hostname);
        try
        {
            client.Connect();
        }
        catch (Exception)
        {
            // SSH.NET failed (encrypted key, no agent) — fall back to native ssh
            AnsiConsole.MarkupLine("[grey]SSH.NET auth failed, falling back to native ssh...[/]");
            return RunNativeSshCommand(manageArgs);
        }

        using var command = client.CreateCommand(cmd);
        command.CommandTimeout = TimeSpan.FromMinutes(30);

        var asyncResult = command.BeginExecute();

        // Stream stdout
        using var stdout = command.OutputStream;
        using var stderr = command.ExtendedOutputStream;

        var buffer = new byte[4096];
        while (!asyncResult.IsCompleted || stdout.CanRead)
        {
            var read = stdout.Read(buffer, 0, buffer.Length);
            if (read > 0)
            {
                Console.Write(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
            }
            else if (!asyncResult.IsCompleted)
            {
                Thread.Sleep(100);
            }
        }

        command.EndExecute(asyncResult);

        // Flush any remaining stderr
        if (stderr.CanRead)
        {
            var errBuffer = new byte[4096];
            int errRead;
            while ((errRead = stderr.Read(errBuffer, 0, errBuffer.Length)) > 0)
            {
                Console.Error.Write(System.Text.Encoding.UTF8.GetString(errBuffer, 0, errRead));
            }
        }

        return command.ExitStatus ?? 1;
    }

    /// <summary>
    /// Run a manage.sh subcommand via native ssh (interactive, with TTY).
    /// Used for: logs, shell, restore.
    /// </summary>
    public int RunInteractive(string manageArgs)
    {
        EnsureConfigured();
        return LaunchNativeSsh(manageArgs, interactive: true);
    }

    /// <summary>
    /// Fetch the remote .env file to read DOMAIN for display purposes.
    /// </summary>
    public string? GetRemoteDomain()
    {
        EnsureConfigured();

        if (IsSshAgentAvailable())
            return GetRemoteDomainViaNativeSsh();

        var (user, hostname) = _config.ParseHost();

        try
        {
            using var client = CreateSshClient(user, hostname);
            client.Connect();
            using var cmd = client.RunCommand(
                $"grep '^DOMAIN=' {_config.ProjectDir}/.env 2>/dev/null | cut -d= -f2");
            var domain = cmd.Result.Trim();
            return string.IsNullOrWhiteSpace(domain) || domain == "yourdomain.com" ? null : domain;
        }
        catch
        {
            return GetRemoteDomainViaNativeSsh();
        }
    }

    /// <summary>
    /// Run a non-interactive command via native ssh, streaming stdout/stderr.
    /// Works with SSH agents (1Password, ssh-agent, Pageant).
    /// </summary>
    private int RunNativeSshCommand(string manageArgs)
    {
        return LaunchNativeSsh(manageArgs, interactive: false);
    }

    private int LaunchNativeSsh(string manageArgs, bool interactive)
    {
        var remoteCmd = $"cd {_config.ProjectDir} && ./manage.sh {manageArgs}";

        var sshArgs = new List<string>();
        if (interactive)
            sshArgs.Add("-t");

        // Add connection timeout (30 seconds)
        sshArgs.Add("-o");
        sshArgs.Add("ConnectTimeout=30");

        if (!string.IsNullOrWhiteSpace(_config.KeyFile))
        {
            sshArgs.Add("-i");
            sshArgs.Add(ExpandPath(_config.KeyFile));
        }

        sshArgs.Add(_config.Host);
        sshArgs.Add(remoteCmd);

        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            UseShellExecute = false,
            RedirectStandardOutput = !interactive,
            RedirectStandardError = !interactive,
        };

        foreach (var arg in sshArgs)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                AnsiConsole.MarkupLine("[red]Failed to start ssh process.[/]");
                return 1;
            }

            if (!interactive)
            {
                // Stream stdout and stderr to console
                var stdoutTask = Task.Run(() =>
                {
                    var buf = new char[4096];
                    int read;
                    while ((read = process.StandardOutput.Read(buf, 0, buf.Length)) > 0)
                        Console.Out.Write(buf, 0, read);
                });

                var stderrTask = Task.Run(() =>
                {
                    var buf = new char[4096];
                    int read;
                    while ((read = process.StandardError.Read(buf, 0, buf.Length)) > 0)
                        Console.Error.Write(buf, 0, read);
                });

                Task.WaitAll(stdoutTask, stderrTask);
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to launch ssh:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[grey]Make sure 'ssh' is on your PATH (built-in on Windows 10+, macOS, Linux).[/]");
            return 1;
        }
    }

    private string? GetRemoteDomainViaNativeSsh()
    {
        try
        {
            var remoteCmd = $"grep '^DOMAIN=' {_config.ProjectDir}/.env 2>/dev/null | cut -d= -f2";

            var psi = new ProcessStartInfo
            {
                FileName = "ssh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (!string.IsNullOrWhiteSpace(_config.KeyFile))
            {
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(ExpandPath(_config.KeyFile));
            }

            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("BatchMode=yes");
            psi.ArgumentList.Add(_config.Host);
            psi.ArgumentList.Add(remoteCmd);

            using var process = Process.Start(psi);
            if (process == null) return null;

            var domain = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            return string.IsNullOrWhiteSpace(domain) || domain == "yourdomain.com" ? null : domain;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Detect if an SSH agent is available (1Password, ssh-agent, Pageant).
    /// </summary>
    private static bool IsSshAgentAvailable()
    {
        // Windows: 1Password and OpenSSH use a named pipe
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(@"\\.\pipe\openssh-ssh-agent"))
                return true;
        }

        // Unix: ssh-agent / 1Password set SSH_AUTH_SOCK
        var authSock = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        if (!string.IsNullOrWhiteSpace(authSock))
            return true;

        return false;
    }

    private SshClient CreateSshClient(string user, string hostname)
    {
        var connectionInfo = CreateConnectionInfo(user, hostname);
        return new SshClient(connectionInfo);
    }

    private ConnectionInfo CreateConnectionInfo(string user, string hostname)
    {
        var authMethods = new List<AuthenticationMethod>();

        // Try explicit key file first
        if (!string.IsNullOrWhiteSpace(_config.KeyFile))
        {
            var keyPath = ExpandPath(_config.KeyFile);
            if (File.Exists(keyPath))
            {
                var pkFile = TryLoadPrivateKey(keyPath);
                if (pkFile != null)
                    authMethods.Add(new PrivateKeyAuthenticationMethod(user, pkFile));
            }
        }

        // Try default key locations
        var sshDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

        foreach (var keyName in new[] { "id_ed25519", "id_rsa", "id_ecdsa" })
        {
            var keyPath = Path.Combine(sshDir, keyName);
            if (File.Exists(keyPath))
            {
                var pkFile = TryLoadPrivateKey(keyPath);
                if (pkFile != null)
                    authMethods.Add(new PrivateKeyAuthenticationMethod(user, pkFile));
            }
        }

        if (authMethods.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No usable SSH keys found. Falling back to password auth.[/]");
            var password = AnsiConsole.Prompt(
                new TextPrompt<string>("SSH password:").Secret());
            authMethods.Add(new PasswordAuthenticationMethod(user, password));
        }

        return new ConnectionInfo(hostname, 22, user, authMethods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(30), // Connection timeout
        };
    }

    private static PrivateKeyFile? TryLoadPrivateKey(string keyPath)
    {
        try
        {
            return new PrivateKeyFile(keyPath);
        }
        catch (Renci.SshNet.Common.SshPassPhraseNullOrEmptyException)
        {
            // Key is encrypted — can't use via SSH.NET without agent support.
            // Native ssh (with agent) will handle this instead.
            return null;
        }
        catch
        {
            return null;
        }
    }

    private void EnsureConfigured()
    {
        if (!_config.IsConfigured)
        {
            AnsiConsole.MarkupLine("[red]Not connected.[/] Run [yellow]ptx connect <user@host>[/] first.");
            Environment.Exit(1);
        }
    }

    private static string ExpandPath(string path)
    {
        if (path.StartsWith('~'))
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        return path;
    }
}
