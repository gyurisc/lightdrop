using LightDrop.Core.Configuration;

namespace LightDrop.Cli.Commands;

/// <summary>
/// <c>lightdrop daemon</c> — runs the daemon in this process until interrupted.
/// </summary>
internal sealed class DaemonCommand(DaemonEndpointOptions endpoint) : ICliCommand
{
    /// <summary>
    /// Overrides where config and state live.
    /// </summary>
    /// <remarks>
    /// A development escape hatch, like the host and port variables. Two daemons on one machine
    /// must not share a state file, or they would share a device identity and each dismiss the
    /// other's announcements as its own. That is the only way to exercise discovery without a
    /// second computer.
    /// </remarks>
    public const string DataDirectoryEnvironmentVariable = "LIGHTDROP_DATA_DIR";

    public string Name => "daemon";

    public string Description => "Run the LightDrop daemon in the foreground.";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        var dataDirectory = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);

        // In-process rather than spawning anything: one binary is the whole point.
        await Daemon.LightDropDaemon
            .RunAsync(endpoint, string.IsNullOrWhiteSpace(dataDirectory) ? null : dataDirectory, cancellationToken)
            .ConfigureAwait(false);
        return 0;
    }
}
