using System.Net;
using System.Text.Json;
using LightDrop.Core.Configuration;
using LightDrop.Core.Pairing;

namespace LightDrop.Cli.Commands;

/// <summary>
/// <c>lightdrop unpair &lt;device&gt;</c> -- forgets a paired device on this machine.
/// </summary>
/// <remarks>
/// Matched against <em>paired</em> devices, not discovered ones: unpairing a device that is
/// switched off, or on another network, has to work. Local only -- the other device keeps its own
/// pin until its user unpairs too -- and the message says so, so nobody assumes otherwise.
/// </remarks>
internal sealed class UnpairCommand(IHttpClientFactory httpClientFactory, DaemonEndpointOptions endpoint) : ICliCommand
{
    public string Name => "unpair";

    public string Description => "Forget a paired device on this machine.";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        var query = string.Join(' ', args).Trim();
        if (query.Length == 0)
        {
            await Console.Error.WriteLineAsync("Usage: lightdrop unpair <device name or id prefix>").ConfigureAwait(false);
            await Console.Error.WriteLineAsync("Run 'lightdrop peers --trusted' to list paired devices.").ConfigureAwait(false);
            return 1;
        }

        using var client = DaemonApi.Create(httpClientFactory, endpoint);

        try
        {
            var trusted = await DaemonApi.GetTrustedAsync(client, cancellationToken).ConfigureAwait(false);
            var lookup = PeerLookup.Find(query, trusted, peer => peer.DeviceId, peer => peer.DeviceName);

            switch (lookup.Outcome)
            {
                case PeerLookupOutcome.NotFound:
                    await Console.Error.WriteLineAsync(
                        $"No paired device matches '{query}'. Run 'lightdrop peers --trusted' to list them.")
                        .ConfigureAwait(false);
                    return 1;

                case PeerLookupOutcome.Ambiguous:
                    var names = string.Join(", ", lookup.Candidates.Select(peer =>
                        $"{peer.DeviceName} ({DaemonApi.ShortId(peer.DeviceId)})"));
                    await Console.Error.WriteLineAsync(
                        $"'{query}' matches more than one paired device: {names}. Use more of the device id.")
                        .ConfigureAwait(false);
                    return 1;
            }

            var peer = lookup.Match!;

            using var response = await client
                .DeleteAsync($"api/trusted/{Uri.EscapeDataString(peer.DeviceId)}", cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Console.WriteLine($"{peer.DeviceName} was not paired.");
                return 0;
            }

            if (!response.IsSuccessStatusCode)
            {
                await Console.Error.WriteLineAsync(
                    await DaemonApi.ReadErrorAsync(response, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                return 1;
            }

            Console.WriteLine($"Unpaired {peer.DeviceName}. It can no longer connect to this device.");
            Console.WriteLine($"{peer.DeviceName} keeps its own pairing until you run 'lightdrop unpair' there too.");
            return 0;
        }
        catch (HttpRequestException)
        {
            return await DaemonApi.ReportUnreachableAsync(endpoint.ClientAddress).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync(
                $"Daemon at {endpoint.ClientAddress} did not respond within {DaemonApi.RequestTimeout.TotalSeconds:0} seconds.")
                .ConfigureAwait(false);
            return 1;
        }
        catch (JsonException ex)
        {
            await Console.Error.WriteLineAsync(
                $"Daemon at {endpoint.ClientAddress} returned a response this build could not parse: {ex.Message}")
                .ConfigureAwait(false);
            return 1;
        }
    }
}
