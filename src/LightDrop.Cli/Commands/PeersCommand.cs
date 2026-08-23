using System.Net.Http.Json;
using System.Text.Json;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using LightDrop.Core.Discovery;

namespace LightDrop.Cli.Commands;

/// <summary>
/// <c>lightdrop peers</c> — lists nearby devices the local daemon has heard.
/// </summary>
/// <remarks>
/// These are strangers, not trusted devices. Nothing has been verified about any of them, and
/// nothing can be sent to them yet.
/// </remarks>
internal sealed class PeersCommand(IHttpClientFactory httpClientFactory, DaemonEndpointOptions endpoint) : ICliCommand
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public string Name => "peers";

    public string Description => "List nearby devices discovered on the local network.";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        var address = endpoint.ClientAddress;

        using var client = httpClientFactory.CreateClient();
        client.BaseAddress = address;
        client.Timeout = RequestTimeout;

        try
        {
            var response = await client
                .GetFromJsonAsync("api/peers", LightDropJsonContext.Default.PeerListResponse, cancellationToken)
                .ConfigureAwait(false);

            if (response is null)
            {
                await Console.Error.WriteLineAsync($"Daemon at {address} returned an empty response.")
                    .ConfigureAwait(false);
                return 1;
            }

            Print(response);
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

    private static void Print(PeerListResponse response)
    {
        var state = PeerSearch.Classify(response, DateTimeOffset.UtcNow);

        if (state != PeerSearchState.PeersFound)
        {
            PrintEmpty(state);
            return;
        }

        var peers = response.Peers;

        // Width is bounded because device names are truncated at ingestion.
        var nameWidth = Math.Max(6, peers.Max(peer => peer.DeviceName.Length));

        Console.WriteLine($"{"DEVICE".PadRight(nameWidth)}  {"PLATFORM",-9}  {"PROTO",-5}  ID");

        foreach (var peer in peers)
        {
            var shortId = peer.DeviceId.Length > 8 ? peer.DeviceId[..8] : peer.DeviceId;
            Console.WriteLine(
                $"{peer.DeviceName.PadRight(nameWidth)}  {peer.Platform,-9}  {peer.ProtocolVersion,-5}  {shortId}");
        }

        Console.WriteLine();
        Console.WriteLine($"{peers.Count} peer{(peers.Count == 1 ? "" : "s")} nearby. None are paired or trusted.");
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
