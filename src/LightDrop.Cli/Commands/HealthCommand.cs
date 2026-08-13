using System.Net.Http.Json;
using System.Text.Json;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;

namespace LightDrop.Cli.Commands;

/// <summary>
/// <c>lightdrop health</c> — queries the local daemon and prints what it reports.
/// </summary>
internal sealed class HealthCommand(IHttpClientFactory httpClientFactory, DaemonEndpointOptions endpoint) : ICliCommand
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public string Name => "health";

    public string Description => "Show the local daemon's version, identity and capabilities.";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        var address = endpoint.ClientAddress;

        using var client = httpClientFactory.CreateClient();
        client.BaseAddress = address;
        client.Timeout = RequestTimeout;

        try
        {
            using var response = await client.GetAsync("health", cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                await Console.Error.WriteLineAsync(
                    $"Daemon at {address} returned HTTP {(int)response.StatusCode}.").ConfigureAwait(false);
                return 1;
            }

            var health = await response.Content
                .ReadFromJsonAsync(LightDropJsonContext.Default.HealthResponse, cancellationToken)
                .ConfigureAwait(false);

            if (health is null)
            {
                await Console.Error.WriteLineAsync($"Daemon at {address} returned an empty response.")
                    .ConfigureAwait(false);
                return 1;
            }

            Print(health);
            return 0;
        }
        catch (HttpRequestException)
        {
            // By far the most common case: the daemon simply is not running. Say what to do next
            // rather than dumping a connection-refused stack trace.
            await Console.Error.WriteLineAsync(
                $"No LightDrop daemon is reachable at {address}.").ConfigureAwait(false);
            await Console.Error.WriteLineAsync(
                "Start one with 'lightdrop daemon'.").ConfigureAwait(false);
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

    private static void Print(HealthResponse health)
    {
        Console.WriteLine($"LightDrop {health.Version}");
        Console.WriteLine($"  device        {health.DeviceName}");
        Console.WriteLine($"  id            {health.DeviceId}");
        Console.WriteLine($"  platform      {health.Platform}");
        Console.WriteLine($"  protocol      {health.ProtocolVersion}");
        Console.WriteLine($"  capabilities  {(health.Capabilities.Count == 0
            ? "(none)"
            : string.Join(", ", health.Capabilities))}");
    }
}
