using System.ComponentModel;
using System.Diagnostics;
using LightDrop.Core.Configuration;
using Microsoft.Extensions.Hosting;

namespace LightDrop.Cli.Commands;

/// <summary>
/// <c>lightdrop ui</c> — runs the daemon and opens the page in a browser.
/// </summary>
/// <remarks>
/// Deliberately the same code path as <c>lightdrop daemon</c> with a browser tab on top, rather
/// than a client that probes for a running daemon and attaches to one. Probing first races when
/// two invocations both find nothing and both try to bind, and an attach mode would be a second
/// way for the daemon to be running.
/// <para>
/// The one branch is for the double-click case: launched from a shortcut or an app bundle, a bind
/// failure would otherwise mean nothing visible happens at all.
/// </para>
/// </remarks>
internal sealed class UiCommand(DaemonEndpointOptions endpoint) : ICliCommand
{
    public string Name => "ui";

    public string Description => "Open the LightDrop page in a browser, starting the daemon if needed.";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        var dataDirectory = Environment.GetEnvironmentVariable(DaemonCommand.DataDirectoryEnvironmentVariable);

        var app = Daemon.LightDropDaemon.Create(
            endpoint, string.IsNullOrWhiteSpace(dataDirectory) ? null : dataDirectory);

        await using (app.ConfigureAwait(false))
        {
            try
            {
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (Daemon.LightDropDaemon.IsAddressInUse(ex))
            {
                // Assumed to be our own daemon rather than verified with a probe. If it is not,
                // the cost is a browser tab that does not load -- which says nearly as much, for
                // one user on a fixed port.
                Console.WriteLine($"A LightDrop daemon is already running. Opening {endpoint.ClientAddress}");
                OpenBrowser(endpoint.ClientAddress);
                return 0;
            }

            Console.WriteLine($"LightDrop is running at {endpoint.ClientAddress} — press Ctrl+C to stop.");
            OpenBrowser(endpoint.ClientAddress);

            // Closing the browser tab does not stop this. The daemon is the point of the process;
            // discovery has to keep running for the machine to stay visible to its peers.
            await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    /// <remarks>
    /// Never fatal. A machine with no registered browser, or a desktop session that cannot be
    /// reached, should still leave the daemon running with its address on screen.
    /// </remarks>
    private static void OpenBrowser(Uri address)
    {
        try
        {
            // UseShellExecute is what hands the URL to the OS handler -- ShellExecute on Windows,
            // `open` on macOS. Without it the runtime tries to execute the URL as a program.
            using var browser = Process.Start(new ProcessStartInfo(address.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            Console.WriteLine($"Could not open a browser. Go to {address} yourself.");
        }
    }
}
