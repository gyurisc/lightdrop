using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using LightDrop.Core.Discovery;

namespace LightDrop.Cli.Commands;

/// <summary>
/// <c>lightdrop peers</c> — lists nearby devices the local daemon has heard, or with
/// <c>--trusted</c>, the devices this one is paired with.
/// </summary>
/// <remarks>
/// A discovered device is a stranger unless it is also paired; the PAIRED column says which. The
/// mark comes from this device's own pins, joined by device id -- not from anything the other
/// device announced.
/// </remarks>
internal sealed class PeersCommand(IHttpClientFactory httpClientFactory, DaemonEndpointOptions endpoint) : ICliCommand
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public string Name => "peers";

    public string Description => "List nearby devices; with --trusted, list paired devices.";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        var address = endpoint.ClientAddress;

        var trustedOnly = args.Any(arg => arg is "--trusted" or "-t");
        var unknown = args.FirstOrDefault(arg => arg is not ("--trusted" or "-t"));
        if (unknown is not null)
        {
            await Console.Error.WriteLineAsync($"Unknown option '{unknown}'. Usage: lightdrop peers [--trusted]")
                .ConfigureAwait(false);
            return 1;
        }

        using var client = httpClientFactory.CreateClient();
        client.BaseAddress = address;
        client.Timeout = RequestTimeout;

        try
        {
            var trusted = await DaemonApi.GetTrustedAsync(client, cancellationToken).ConfigureAwait(false);

            if (trustedOnly)
            {
                PrintTrusted(trusted);
                return 0;
            }

            var response = await client
                .GetFromJsonAsync("api/peers", LightDropJsonContext.Default.PeerListResponse, cancellationToken)
                .ConfigureAwait(false);

            if (response is null)
            {
                await Console.Error.WriteLineAsync($"Daemon at {address} returned an empty response.")
                    .ConfigureAwait(false);
                return 1;
            }

            Print(response, trusted);
            return 0;
        }
        catch (HttpRequestException)
        {
            await Console.Error.WriteLineAsync($"No LightDrop daemon is reachable at {address}.").ConfigureAwait(false);
            await Console.Error.WriteLineAsync("Start one with 'lightdrop daemon'.").ConfigureAwait(false);
            return 1;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync(
                $"Daemon at {address} did not respond within {RequestTimeout.TotalSeconds:0} seconds.")
                .ConfigureAwait(false);
            return 1;
        }
        catch (JsonException ex)
        {
            await Console.Error.WriteLineAsync(
                $"Daemon at {address} returned a response this build could not parse: {ex.Message}")
                .ConfigureAwait(false);
            return 1;
        }
    }

    private static void Print(PeerListResponse response, IReadOnlyList<TrustedPeerSummary> trusted)
    {
        var state = PeerSearch.Classify(response, DateTimeOffset.UtcNow);

        if (state != PeerSearchState.PeersFound)
        {
            PrintEmpty(state);
            return;
        }

        var peers = response.Peers;
        var pairedIds = trusted.Select(peer => peer.DeviceId).ToHashSet(StringComparer.Ordinal);

        // Width is bounded because device names are truncated at ingestion.
        var nameWidth = Math.Max(6, peers.Max(peer => peer.DeviceName.Length));

        Console.WriteLine($"{"DEVICE".PadRight(nameWidth)}  {"PLATFORM",-9}  {"PROTO",-5}  {"ID",-8}  PAIRED");

        foreach (var peer in peers)
        {
            var paired = pairedIds.Contains(peer.DeviceId) ? "yes" : "";
            Console.WriteLine(
                $"{peer.DeviceName.PadRight(nameWidth)}  {peer.Platform,-9}  {peer.ProtocolVersion,-5}  {DaemonApi.ShortId(peer.DeviceId),-8}  {paired}");
        }

        var pairedCount = peers.Count(peer => pairedIds.Contains(peer.DeviceId));

        Console.WriteLine();
        Console.WriteLine(pairedCount == 0
            ? $"{peers.Count} peer{(peers.Count == 1 ? "" : "s")} nearby. None are paired; pair one with 'lightdrop pair <device>'."
            : $"{peers.Count} peer{(peers.Count == 1 ? "" : "s")} nearby, {pairedCount} paired.");
    }

    private static void PrintTrusted(IReadOnlyList<TrustedPeerSummary> trusted)
    {
        if (trusted.Count == 0)
        {
            Console.WriteLine("No paired devices. Pair one with 'lightdrop pair <device>'.");
            return;
        }

        var nameWidth = Math.Max(6, trusted.Max(peer => peer.DeviceName.Length));

        Console.WriteLine($"{"DEVICE".PadRight(nameWidth)}  {"ID",-8}  PAIRED ON");

        foreach (var peer in trusted)
        {
            Console.WriteLine(
                $"{peer.DeviceName.PadRight(nameWidth)}  {DaemonApi.ShortId(peer.DeviceId),-8}  " +
                peer.PairedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        Console.WriteLine();
        Console.WriteLine($"{trusted.Count} paired device{(trusted.Count == 1 ? "" : "s")}. Remove one with 'lightdrop unpair <device>'.");
    }

    /// <summary>
    /// Explains an empty list according to what discovery is actually doing.
    /// </summary>
    /// <remarks>
    /// The troubleshooting advice appears only in the one state where it applies. Printing it
    /// while discovery is still settling sends people to look at firewalls and privacy settings
    /// when the honest answer is that the search has not finished.
    /// </remarks>
    private static void PrintEmpty(PeerSearchState state)
    {
        switch (state)
        {
            case PeerSearchState.Searching:
                Console.WriteLine("Searching for peers.");
                Console.WriteLine();
                Console.WriteLine($"Discovery started less than {PeerSearch.SettlesAfter.TotalSeconds:0} seconds");
                Console.WriteLine("ago. Peers usually appear within that time; try again shortly.");
                break;

            case PeerSearchState.Stopped:
                Console.WriteLine("Discovery is not running.");
                Console.WriteLine();
                Console.WriteLine("The daemon started without it, so no peer can ever appear here.");
                Console.WriteLine("This is what a blocked network permission looks like:");
                Console.WriteLine("  macOS    System Settings > Privacy & Security > Local Network");
                Console.WriteLine("  Windows  allow lightdrop through Defender Firewall");
                Console.WriteLine("Check the daemon's output for the reason it could not start.");
                break;

            default:
                Console.WriteLine("No peers discovered.");
                Console.WriteLine();
                Console.WriteLine("Discovery has been running long enough to expect an answer.");
                Console.WriteLine("A daemon must be running on another device on the same network.");
                Console.WriteLine("If one is, discovery may be blocked:");
                Console.WriteLine("  macOS    System Settings > Privacy & Security > Local Network");
                Console.WriteLine("  Windows  allow lightdrop through Defender Firewall");
                Console.WriteLine("  Networks that block multicast, including many corporate and guest");
                Console.WriteLine("  networks, prevent discovery entirely.");
                break;
        }
    }
}
