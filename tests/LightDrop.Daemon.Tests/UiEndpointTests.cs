using System.Net;
using LightDrop.Core.Configuration;
using LightDrop.Daemon.Discovery;
using LightDrop.Daemon.Tests.TestSupport;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// The page the daemon serves at its root.
/// </summary>
/// <remarks>
/// Embedded in the assembly rather than published as a file, because LightDrop ships as one
/// executable. A missing embedded resource is a runtime failure, not a build one, so it is worth
/// a test that actually fetches it.
/// </remarks>
public sealed class UiEndpointTests
{
    [Fact]
    public async Task ServesThePageAtTheRoot()
    {
        using var directory = new TempDataDirectory();
        var endpoint = new DaemonEndpointOptions { Host = "127.0.0.1", Port = FreeTcpPort.Get() };
        using var cancellation = new CancellationTokenSource();

        var app = LightDropDaemon.Create(endpoint, directory.FullPath, new NoOpPeerDiscoveryTransport());
        await using (app.ConfigureAwait(false))
        {
            await app.StartAsync(cancellation.Token);

            using var client = new HttpClient { BaseAddress = endpoint.ClientAddress };
            using var response = await client.GetAsync("/", cancellation.Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

            var body = await response.Content.ReadAsStringAsync(cancellation.Token);
            Assert.Contains("LightDrop", body, StringComparison.Ordinal);

            // Proves the real page was served rather than an empty stream: the peer list is the
            // element the page exists for.
            Assert.Contains("id=\"peers\"", body, StringComparison.Ordinal);

            await app.StopAsync(cancellation.Token);
        }
    }
}
