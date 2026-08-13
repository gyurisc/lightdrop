using System.Net.Http.Json;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using LightDrop.Daemon.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// <c>GET /api/peers</c> over real HTTP, with discovery driven by a fake transport.
/// </summary>
/// <remarks>
/// Registry behaviour — expiry, deduplication, self-filtering, bounding — is covered by
/// PeerRegistryTests in Core. Repeating it through HTTP would add runtime and coupling for no
/// extra confidence. What is proved here is the wiring: transport event to registry to endpoint.
/// </remarks>
public sealed class PeerEndpointTests
{
    private static async Task<(WebApplication App, HttpClient Client, FakePeerDiscoveryTransport Transport)>
        StartDaemonAsync(TempDataDirectory directory)
    {
        var endpoint = new DaemonEndpointOptions { Host = "127.0.0.1", Port = FreeTcpPort.Get() };
        var transport = new FakePeerDiscoveryTransport();

        var app = LightDropDaemon.Create(endpoint, directory.FullPath, transport);
        await app.StartAsync(CancellationToken.None);

        return (app, new HttpClient { BaseAddress = endpoint.ClientAddress }, transport);
    }

    private static Task<IReadOnlyList<DiscoveredPeer>?> GetPeersAsync(HttpClient client) =>
        client.GetFromJsonAsync("api/peers", LightDropJsonContext.Default.IReadOnlyListDiscoveredPeer, CancellationToken.None);

    [Fact]
    public async Task ReportsNoPeersBeforeAnyAreHeard()
    {
        using var directory = new TempDataDirectory();
        var (app, client, _) = await StartDaemonAsync(directory);

        await using (app)
        {
            using (client)
            {
                Assert.Empty((await GetPeersAsync(client))!);
            }

            await app.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ReportsAPeerAfterTheTransportAnnouncesIt()
    {
        using var directory = new TempDataDirectory();
        var (app, client, transport) = await StartDaemonAsync(directory);

        await using (app)
        {
            using (client)
            {
                transport.Announce("peer-1", "Work Laptop", "windows", protocolVersion: 1, port: 5533);

                var peer = Assert.Single((await GetPeersAsync(client))!);
                Assert.Equal("peer-1", peer.DeviceId);
                Assert.Equal("Work Laptop", peer.DeviceName);
                Assert.Equal("windows", peer.Platform);
                Assert.Equal(1, peer.ProtocolVersion);
                Assert.Equal(5533, peer.Port);
                Assert.Empty(peer.Capabilities);
            }

            await app.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DropsAPeerThatSaysGoodbye()
    {
        using var directory = new TempDataDirectory();
        var (app, client, transport) = await StartDaemonAsync(directory);

        await using (app)
        {
            using (client)
            {
                transport.Announce("peer-1");
                Assert.Single((await GetPeersAsync(client))!);

                transport.Goodbye("peer-1");
                Assert.Empty((await GetPeersAsync(client))!);
            }

            await app.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartsDiscoveryWithThisDevicesIdentityAndPort()
    {
        // The advertised port is the daemon's own. It is not reachable across the network and is
        // not an authorization boundary — it is only what DNS-SD requires in an SRV record.
        using var directory = new TempDataDirectory();
        var (app, client, transport) = await StartDaemonAsync(directory);

        await using (app)
        {
            using (client)
            {
                var health = await client.GetFromJsonAsync(
                    "health", LightDropJsonContext.Default.HealthResponse, CancellationToken.None);

                Assert.NotNull(transport.StartedWith);
                Assert.Equal(health!.DeviceId, transport.StartedWith!.Id);
                Assert.Equal(client.BaseAddress!.Port, transport.StartedOnPort);
            }

            await app.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task KeepsServingWhenDiscoveryCannotStart()
    {
        // The failure this milestone actually expects on a real machine: a denied macOS Local
        // Network permission or a blocked firewall. Discovery is a convenience, not a
        // prerequisite — the daemon must still answer /health and report an empty peer list.
        using var directory = new TempDataDirectory();
        var endpoint = new DaemonEndpointOptions { Host = "127.0.0.1", Port = FreeTcpPort.Get() };

        await using var app = LightDropDaemon.Create(
            endpoint, directory.FullPath, new FakePeerDiscoveryTransport { FailToStart = true });

        await app.StartAsync(CancellationToken.None);

        using (var client = new HttpClient { BaseAddress = endpoint.ClientAddress })
        {
            var health = await client.GetFromJsonAsync(
                "health", LightDropJsonContext.Default.HealthResponse, CancellationToken.None);

            Assert.NotNull(health);
            Assert.Empty((await GetPeersAsync(client))!);
        }

        // Shutdown must stay clean even though start failed.
        await app.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NeverWritesADiscoveredPeerToDisk()
    {
        // The invariant the whole milestone rests on: discovery is presence, not trust. A peer
        // must not be able to reach state.json, directly or indirectly.
        using var directory = new TempDataDirectory();
        var (app, client, transport) = await StartDaemonAsync(directory);

        await using (app)
        {
            using (client)
            {
                await GetPeersAsync(client);
                var stateBefore = await File.ReadAllTextAsync(directory.StateFilePath, CancellationToken.None);

                transport.Announce("peer-1", "Stranger");
                transport.Announce("peer-2", "Another Stranger");
                await GetPeersAsync(client);

                var stateAfter = await File.ReadAllTextAsync(directory.StateFilePath, CancellationToken.None);

                Assert.Equal(stateBefore, stateAfter);
                Assert.DoesNotContain("peer-1", stateAfter, StringComparison.Ordinal);
                Assert.DoesNotContain("Stranger", stateAfter, StringComparison.Ordinal);

                // No sidecar file appeared either: state.json is the only thing discovery could
                // have written to, and it did not.
                Assert.Equal([directory.StateFilePath], Directory.GetFiles(directory.FullPath));
            }

            await app.StopAsync(CancellationToken.None);
        }
    }
}
