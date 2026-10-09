using System.Net;
using System.Net.Http.Json;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using LightDrop.Daemon.Peers;
using LightDrop.Daemon.Tests.TestSupport;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// The one LAN-reachable socket: when it exists, who gets through its handshake, and what is
/// behind it.
/// </summary>
/// <remarks>
/// The rejections are the tests that matter, and each pairs with an acceptance on the same
/// listener, so a rejection can never pass merely because nothing was listening.
/// </remarks>
public sealed class PeerListenerTests(PairingKeys keys) : IClassFixture<PairingKeys>
{
    private const string DaemonId = "11111111111111111111111111111111";
    private const string BobId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string StrangerId = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    private TrustedPeer Bob => new()
    {
        DeviceId = BobId,
        DeviceName = "Bob",
        PublicKey = Convert.ToBase64String(keys.Bob.PublicKeyInfo),
        PairedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task DoesNotExistWithNothingPairedAndNoWindowOpen()
    {
        // A fresh install has no LAN socket at all -- the same property M1 had.
        await using var daemon = await TestDaemon.StartAsync(DaemonId, "Desk PC");

        Assert.False(await PeerClient.IsListeningAsync(daemon.Endpoint));
    }

    [Fact]
    public async Task OpensWhenAPairingWindowOpens()
    {
        await using var daemon = await TestDaemon.StartAsync(DaemonId, "Desk PC");
        daemon.Transport.Announce(StrangerId, "Mac Mini", address: "127.0.0.1");

        (await daemon.StartPairingAsync("Mac Mini")).Dispose();

        Assert.True(await PeerClient.IsListeningAsync(daemon.Endpoint));
    }

    [Fact]
    public async Task StartsWithTheDaemonWhenAPeerIsAlreadyPinned()
    {
        await using var daemon = await TestDaemon.StartAsync(DaemonId, "Desk PC", Bob);

        using var response = await PeerClient.GetAsync(keys.Bob, daemon.PeerAddress, "health");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RejectsAnUnpinnedCallerAtTheHandshakeWhenNoWindowIsOpen()
    {
        await using var daemon = await TestDaemon.StartAsync(DaemonId, "Desk PC", Bob);

        // The control: the listener is up and the pinned peer gets through.
        using (var pinned = await PeerClient.GetAsync(keys.Bob, daemon.PeerAddress, "pair/anything"))
        {
            Assert.Equal(HttpStatusCode.NotFound, pinned.StatusCode);
        }

        // A perfectly valid certificate, just not one this device pinned. No HTTP status at all:
        // the connection must not get as far as a request.
        await Assert.ThrowsAsync<HttpRequestException>(
            () => PeerClient.GetAsync(keys.Impostor, daemon.PeerAddress, "pair/anything"));
    }

    [Fact]
    public async Task UnpairTakesEffectOnTheNextConnectionWithoutARestart()
    {
        await using var daemon = await TestDaemon.StartAsync(DaemonId, "Desk PC", Bob);

        using (var before = await PeerClient.GetAsync(keys.Bob, daemon.PeerAddress, "health"))
        {
            Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        }

        using (var unpaired = await daemon.Api.DeleteAsync($"api/trusted/{BobId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, unpaired.StatusCode);
        }

        Assert.Empty(await daemon.GetTrustedAsync());

        // The listener is deliberately still running (it is not stopped on unpair), so this is a
        // handshake refusal, not an absent socket.
        Assert.True(await PeerClient.IsListeningAsync(daemon.Endpoint));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => PeerClient.GetAsync(keys.Bob, daemon.PeerAddress, "health"));
    }

    [Theory]
    [InlineData("GET", "health")]
    [InlineData("GET", "")]
    [InlineData("GET", "api/peers")]
    [InlineData("GET", "api/trusted")]
    [InlineData("GET", "api/pairing")]
    [InlineData("POST", "api/pairing")]
    [InlineData("DELETE", "api/trusted/" + BobId)]
    public async Task ServesNoneOfTheLoopbackRoutes(string method, string path)
    {
        // Even to a pinned peer. The loopback app's routes are not in the peer app's table at all;
        // nothing here depends on a Host header or on remembering to exclude a route.
        await using var daemon = await TestDaemon.StartAsync(DaemonId, "Desk PC", Bob);

        using var client = new HttpClient(Pairing.PairingTls.CreateCapturingHandler(keys.Bob, new Pairing.PeerKeyCapture()))
        {
            BaseAddress = daemon.PeerAddress,
        };
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "GET" ? null : new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"{method} /{path} answered {(int)response.StatusCode} on the peer listener.");
        Assert.Single(await daemon.GetTrustedAsync());
    }

    [Fact]
    public async Task RefusesAnOversizedBody()
    {
        // Every pairing body is tiny and the caller may be a stranger.
        await using var daemon = await TestDaemon.StartAsync(DaemonId, "Desk PC", Bob);

        using var client = new HttpClient(Pairing.PairingTls.CreateCapturingHandler(keys.Bob, new Pairing.PeerKeyCapture()))
        {
            BaseAddress = daemon.PeerAddress,
        };

        var padding = new string('x', (int)PeerListener.MaxRequestBodyBytes * 4);
        using var response = await client.PostAsync(
            "pair/hello",
            new StringContent($"{{\"deviceId\":\"{padding}\",\"protocolVersion\":1}}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task UnpairingAnUnknownPeerIsNotFound()
    {
        await using var daemon = await TestDaemon.StartAsync(DaemonId, "Desk PC", Bob);

        using var response = await daemon.Api.DeleteAsync($"api/trusted/{StrangerId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(await daemon.GetTrustedAsync());
    }

    [Fact]
    public async Task TheTrustedListNeverCarriesKeyMaterial()
    {
        await using var daemon = await TestDaemon.StartAsync(DaemonId, "Desk PC", Bob);

        var json = await daemon.Api.GetStringAsync("api/trusted");

        Assert.Contains(BobId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(Bob.PublicKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain("publicKey", json, StringComparison.OrdinalIgnoreCase);

        var parsed = await daemon.Api.GetFromJsonAsync("api/trusted", LightDropJsonContext.Default.TrustedPeerListResponse);
        Assert.Equal("Bob", Assert.Single(parsed!.Peers).DeviceName);
    }
}
