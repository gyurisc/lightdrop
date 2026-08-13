using LightDrop.Core.Configuration;

namespace LightDrop.Cli.Commands;

/// <summary>
/// <c>lightdrop daemon</c> — runs the daemon in this process until interrupted.
/// </summary>
internal sealed class DaemonCommand(DaemonEndpointOptions endpoint) : ICliCommand
{
    public string Name => "daemon";

    public string Description => "Run the LightDrop daemon in the foreground.";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        // In-process rather than spawning anything: one binary is the whole point.
        await Daemon.LightDropDaemon
            .RunAsync(endpoint, dataDirectory: null, cancellationToken)
            .ConfigureAwait(false);
        return 0;
    }
}
