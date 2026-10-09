using System.Net.Http.Json;
using System.Text.Json;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;

namespace LightDrop.Cli.Commands;

/// <summary>
/// What the pairing verbs share about talking to the local daemon.
/// </summary>
/// <remarks>
/// Only the newer verbs use this; <c>health</c> and <c>peers</c> predate it and carry their own
/// copies of the same handful of lines, which is not worth churning.
/// </remarks>
internal static class DaemonApi
{
    /// <summary>
    /// Per request. Nothing the pairing verbs ask the daemon blocks on the other device -- the
    /// daemon owns the session and the CLI polls it -- so every call should answer quickly.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public static HttpClient Create(IHttpClientFactory httpClientFactory, DaemonEndpointOptions endpoint)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = endpoint.ClientAddress;
        client.Timeout = RequestTimeout;
        return client;
    }

    /// <summary>The daemon's own explanation of a refusal, or the status code when it gave none.</summary>
    public static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content
                .ReadFromJsonAsync(LightDropJsonContext.Default.ApiError, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(error?.Error))
            {
                return error.Error;
            }
        }
        catch (JsonException)
        {
            // Not one of ours; fall through to the status code.
        }

        return $"The daemon answered HTTP {(int)response.StatusCode}.";
    }

    public static async Task<int> ReportUnreachableAsync(Uri address)
    {
        await Console.Error.WriteLineAsync($"No LightDrop daemon is reachable at {address}.").ConfigureAwait(false);
        await Console.Error.WriteLineAsync("Start one with 'lightdrop daemon'.").ConfigureAwait(false);
        return 1;
    }

    public static async Task<IReadOnlyList<TrustedPeerSummary>> GetTrustedAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var response = await client
            .GetFromJsonAsync("api/trusted", LightDropJsonContext.Default.TrustedPeerListResponse, cancellationToken)
            .ConfigureAwait(false);

        return response?.Peers ?? [];
    }

    /// <summary>The first eight characters of a device id: enough to tell devices apart, short enough to type.</summary>
    public static string ShortId(string deviceId) => deviceId.Length > 8 ? deviceId[..8] : deviceId;
}
