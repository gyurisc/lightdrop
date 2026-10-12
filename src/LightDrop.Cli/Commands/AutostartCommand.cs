using System.Net.Sockets;
using System.Runtime.Versioning;
using LightDrop.Core.Configuration;
using LightDrop.Daemon.Autostart;

namespace LightDrop.Cli.Commands;

/// <summary>
/// <c>lightdrop autostart [on|off]</c> -- runs the daemon at login, so a Mac receives files without
/// a terminal left open.
/// </summary>
/// <remarks>
/// Opt-in, and the only thing LightDrop ever registers with the OS: "no installation" means nothing
/// happens unless the user asks. macOS only for now. On Windows the obvious home, the Run key or
/// Task Scheduler, would open a console window at every logon, because <c>lightdrop.exe</c> is a
/// console program; doing it properly is its own piece of work (<c>docs/DECISIONS.md</c> #33).
/// </remarks>
internal sealed class AutostartCommand(DaemonEndpointOptions endpoint) : ICliCommand
{
    public string Name => "autostart";

    public string Description => "Run the daemon at login: 'on', 'off', or nothing for status.";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            await Console.Error.WriteLineAsync(
                "'lightdrop autostart' is macOS-only for now. On Windows, create a Task Scheduler task "
                + "with an 'At log on' trigger that runs 'lightdrop.exe daemon'.").ConfigureAwait(false);
            return 1;
        }

        var agent = new LaunchAgent(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        switch (args)
        {
            case [] or ["status"]:
                return Status(agent);

            case ["on"]:
                return await OnAsync(agent, cancellationToken).ConfigureAwait(false);

            case ["off"]:
                Console.WriteLine(agent.Disable()
                    ? "Autostart is off. The background daemon has stopped and will not start at login."
                    : "Autostart was already off.");
                return 0;

            default:
                await Console.Error.WriteLineAsync("Usage: lightdrop autostart [on|off]").ConfigureAwait(false);
                return 1;
        }
    }

    [SupportedOSPlatform("macos")]
    private async Task<int> OnAsync(LaunchAgent agent, CancellationToken cancellationToken)
    {
        var executable = Environment.ProcessPath;

        // Under `dotnet lightdrop.dll` the process is the dotnet host, and registering that would
        // run `dotnet daemon` at every login.
        if (executable is null
            || Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            await Console.Error.WriteLineAsync(
                "Run 'autostart on' from the lightdrop executable itself, so login knows what to start.")
                .ConfigureAwait(false);
            return 1;
        }

        // When launchd already runs the job, the daemon holding the port is that one, about to be
        // replaced -- not a terminal session to warn about.
        var alreadyRunning = !agent.GetStatus().Running
            && await DaemonIsListeningAsync(cancellationToken).ConfigureAwait(false);

        agent.Enable(executable);

        Console.WriteLine("Autostart is on. The daemon now starts at login, and restarts if it crashes.");
        Console.WriteLine($"  runs  {executable} daemon");
        Console.WriteLine($"  log   {agent.LogPath}");

        if (alreadyRunning)
        {
            Console.WriteLine();
            Console.WriteLine("A daemon is already running, probably in a terminal. The background one keeps");
            Console.WriteLine("retrying and takes over once you stop that one (Ctrl+C).");
        }

        if (IsBuildOutput(executable))
        {
            Console.WriteLine();
            Console.WriteLine("Note: this binary is inside a build folder (bin/Debug or bin/Release), as with");
            Console.WriteLine("'dotnet run'. It works, but every rebuild replaces it, and the macOS firewall may");
            Console.WriteLine("ask about it again. For everyday use, publish once with -o to a folder that");
            Console.WriteLine("stays put and run 'autostart on' from there.");
        }

        Console.WriteLine();
        Console.WriteLine("Turn it off with 'lightdrop autostart off'.");
        return 0;
    }

    [SupportedOSPlatform("macos")]
    private static int Status(LaunchAgent agent)
    {
        var status = agent.GetStatus();

        if (!status.Installed)
        {
            Console.WriteLine(status.Running
                ? "Autostart is off, but launchd still has the job loaded. Run 'lightdrop autostart off' to clear it."
                : "Autostart is off. Turn it on with 'lightdrop autostart on'.");
            return 0;
        }

        Console.WriteLine("Autostart is on.");
        Console.WriteLine($"  runs     {status.ExecutablePath ?? "(unreadable plist)"} daemon");
        Console.WriteLine($"  running  {(status.Running ? $"yes{(status.ProcessId is { } pid ? $" (pid {pid})" : "")}" : "no")}");
        Console.WriteLine($"  log      {agent.LogPath}");

        if (status.ExecutablePath is { } path && !File.Exists(path))
        {
            Console.WriteLine();
            Console.WriteLine("That executable no longer exists, so nothing starts at login.");
            Console.WriteLine("Run 'lightdrop autostart on' from the lightdrop you want to use.");
        }
        else if (status.ExecutablePath is { } registered
            && Environment.ProcessPath is { } current
            && !string.Equals(Path.GetFullPath(registered), Path.GetFullPath(current), StringComparison.Ordinal))
        {
            Console.WriteLine();
            Console.WriteLine($"That is not the lightdrop you just ran ({current}).");
            Console.WriteLine("Run 'lightdrop autostart on' from this one to switch.");
        }

        return 0;
    }

    /// <summary>Whether something already holds the daemon's loopback port.</summary>
    private async Task<bool> DaemonIsListeningAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.ClientAddress.Host, endpoint.ClientAddress.Port, timeout.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static bool IsBuildOutput(string executable)
    {
        var separator = Path.DirectorySeparatorChar;
        return executable.Contains($"{separator}bin{separator}Debug{separator}", StringComparison.Ordinal)
            || executable.Contains($"{separator}bin{separator}Release{separator}", StringComparison.Ordinal);
    }
}
