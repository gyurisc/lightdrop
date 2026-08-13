using System.Net.Http.Json;
using System.Text.Json;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;

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
            var peers = await client
                .GetFromJsonAsync("api/peers", LightDropJsonContext.Default.IReadOnlyListDiscoveredPeer, cancellationToken)
                .ConfigureAwait(false);

            Print(peers ?? []);
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

    private static void Print(IReadOnlyList<DiscoveredPeer> peers)
    {
        if (peers.Count == 0)
        {
            Console.WriteLine("No peers discovered.");
            Console.WriteLine();
            Console.WriteLine("A daemon must be running on another device on the same network.");
            Console.WriteLine("If one is, discovery may be blocked:");
            Console.WriteLine("  macOS    System Settings > Privacy & Security > Local Network");
            Console.WriteLine("  Windows  allow lightdrop through Defender Firewall");
            Console.WriteLine("  Networks that block multicast, including many corporate and guest");
            Console.WriteLine("  networks, prevent discovery entirely.");
            return;
        }

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
}
