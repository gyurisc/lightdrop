using System.Net;
using System.Net.Http.Json;
using LightDrop.Core.Contracts;
using LightDrop.Core.Pairing;
using LightDrop.Daemon.Tests.TestSupport;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// Two real daemons pairing over a real mutual-TLS peer listener, driven through their loopback
/// APIs exactly as two <c>lightdrop pair</c> invocations would drive them.
/// </summary>
/// <remarks>
/// Ids are fixed so the roles are too: <see cref="ListenerId"/> sorts first and listens,
/// <see cref="DialerId"/> dials.
/// </remarks>
public sealed class PairingFlowTests
{
    private const string ListenerId = "11111111111111111111111111111111";
    private const string DialerId = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    private static async Task<(TestDaemon Listener, TestDaemon Dialer)> StartPairAsync()
    {
        var listener = await TestDaemon.StartAsync(ListenerId, "Desk PC");
        var dialer = await TestDaemon.StartAsync(DialerId, "Mac Mini");

        listener.Discover(dialer);
        dialer.Discover(listener);

        return (listener, dialer);
    }

    private static async Task BothShowTheCodeAsync(TestDaemon listener, TestDaemon dialer)
    {
        using (var started = await listener.StartPairingAsync("Mac Mini"))
        {
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        }

        using (var started = await dialer.StartPairingAsync("desk pc"))
        {
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        }

        var listenerSide = await listener.WaitForStateAsync(PairingState.Confirming);
        var dialerSide = await dialer.WaitForStateAsync(PairingState.Confirming);

        // The point of the whole ceremony: derived independently on each side, never sent.
        Assert.NotNull(listenerSide.Code);
        Assert.Equal(listenerSide.Code, dialerSide.Code);
        Assert.Equal(PairingRole.Listener, listenerSide.Role);
        Assert.Equal(PairingRole.Dialer, dialerSide.Role);
    }

    [Fact]
    public async Task BothAcceptingPinsEachSideToTheOther()
    {
        var (listener, dialer) = await StartPairAsync();
        await using (listener)
        await using (dialer)
        {
            await BothShowTheCodeAsync(listener, dialer);

            (await listener.ConfirmAsync(accepted: true)).Dispose();
            (await dialer.ConfirmAsync(accepted: true)).Dispose();

            await listener.WaitForStateAsync(PairingState.Paired);
            await dialer.WaitForStateAsync(PairingState.Paired);

            var listenerTrusts = Assert.Single(await listener.GetTrustedAsync());
            var dialerTrusts = Assert.Single(await dialer.GetTrustedAsync());

            Assert.Equal(DialerId, listenerTrusts.DeviceId);
            Assert.Equal(ListenerId, dialerTrusts.DeviceId);

            // The name pinned is the one discovery sanitized, not anything said during pairing.
            Assert.Equal("Mac Mini", listenerTrusts.DeviceName);
            Assert.Equal("Desk PC", dialerTrusts.DeviceName);

            // And the key pinned is the one each actually holds: each can now pass the other's
            // handshake gate with no window open.
            using var health = await PeerClient.GetAsync(await dialer.GetKeyAsync(), listener.PeerAddress, "health");
            Assert.Equal(HttpStatusCode.NotFound, health.StatusCode);
        }
    }

    [Fact]
    public async Task TheListenerHoldsTheDialersConfirmUntilItsOwnUserAnswers()
    {
        // The long poll. The dialer answers first and its request waits on the listener for the
        // listener's human; nothing in between may cut it off.
        var (listener, dialer) = await StartPairAsync();
        await using (listener)
        await using (dialer)
        {
            await BothShowTheCodeAsync(listener, dialer);

            (await dialer.ConfirmAsync(accepted: true)).Dispose();

            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Equal(PairingState.Confirming, (await dialer.GetPairingAsync()).State);
            Assert.Empty(await dialer.GetTrustedAsync());

            (await listener.ConfirmAsync(accepted: true)).Dispose();

            await listener.WaitForStateAsync(PairingState.Paired);
            await dialer.WaitForStateAsync(PairingState.Paired);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EitherSideDecliningLeavesNeitherPaired(bool listenerDeclines)
    {
        var (listener, dialer) = await StartPairAsync();
        await using (listener)
        await using (dialer)
        {
            await BothShowTheCodeAsync(listener, dialer);

            (await listener.ConfirmAsync(accepted: !listenerDeclines)).Dispose();
            (await dialer.ConfirmAsync(accepted: listenerDeclines)).Dispose();

            Assert.Equal(PairingState.Rejected, (await listener.WaitForEndAsync()).State);
            Assert.Equal(PairingState.Rejected, (await dialer.WaitForEndAsync()).State);

            Assert.Empty(await listener.GetTrustedAsync());
            Assert.Empty(await dialer.GetTrustedAsync());
        }
    }

    [Fact]
    public async Task RefusesToPairWithAPeerThatIsAlreadyPaired()
    {
        // Pairing again would replace a pinned key; that must be an explicit unpair first.
        var (listener, dialer) = await StartPairAsync();
        await using (listener)
        await using (dialer)
        {
            await BothShowTheCodeAsync(listener, dialer);
            (await listener.ConfirmAsync(accepted: true)).Dispose();
            (await dialer.ConfirmAsync(accepted: true)).Dispose();
            await listener.WaitForStateAsync(PairingState.Paired);

            using var again = await listener.StartPairingAsync("Mac Mini");

            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            var error = await again.Content.ReadFromJsonAsync(LightDropJsonContext.Default.ApiError);
            Assert.Contains("unpair", error!.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RefusesASecondSessionWhileOneIsOpen()
    {
        await using var daemon = await TestDaemon.StartAsync(ListenerId, "Desk PC");
        daemon.Transport.Announce(DialerId, "Mac Mini", address: "127.0.0.1");
        daemon.Transport.Announce("dddddddddddddddddddddddddddddddd", "Laptop", address: "127.0.0.1");

        (await daemon.StartPairingAsync("Mac Mini")).Dispose();
        using var second = await daemon.StartPairingAsync("Laptop");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("Mac Mini", (await daemon.GetPairingAsync()).PeerName);
    }

    [Fact]
    public async Task CancellingClosesTheWindowAndASecondSessionMayStart()
    {
        await using var daemon = await TestDaemon.StartAsync(ListenerId, "Desk PC");
        daemon.Transport.Announce(DialerId, "Mac Mini", address: "127.0.0.1");
        daemon.Transport.Announce("dddddddddddddddddddddddddddddddd", "Laptop", address: "127.0.0.1");

        (await daemon.StartPairingAsync("Mac Mini")).Dispose();
        using (var cancelled = await daemon.Api.DeleteAsync("api/pairing"))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        }

        Assert.Equal(PairingState.Cancelled, (await daemon.GetPairingAsync()).State);

        using var next = await daemon.StartPairingAsync("Laptop");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task AnUndiscoveredPeerIsNotFound()
    {
        await using var daemon = await TestDaemon.StartAsync(ListenerId, "Desk PC");

        using var response = await daemon.StartPairingAsync("Mac Mini");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync(LightDropJsonContext.Default.ApiError);
        Assert.Contains("Mac Mini", error!.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAmbiguousNameIsRefusedNamingTheCandidates()
    {
        await using var daemon = await TestDaemon.StartAsync(ListenerId, "Desk PC");
        daemon.Transport.Announce(DialerId, "Mac Mini", address: "127.0.0.1");
        daemon.Transport.Announce("dddddddddddddddddddddddddddddddd", "Mac Mini", address: "127.0.0.1");

        using var response = await daemon.StartPairingAsync("Mac Mini");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync(LightDropJsonContext.Default.ApiError);
        Assert.Contains("eeeeeeee", error!.Error, StringComparison.Ordinal);
        Assert.Contains("dddddddd", error.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABrowserPageCannotStartAPairing()
    {
        // The loopback origin check guards these write endpoints too: a hostile page must not be
        // able to open a window toward a device an attacker controls.
        await using var daemon = await TestDaemon.StartAsync(ListenerId, "Desk PC");
        daemon.Transport.Announce(DialerId, "Mac Mini", address: "127.0.0.1");

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/pairing")
        {
            Content = JsonContent.Create(
                new StartPairingRequest { Peer = "Mac Mini" }, LightDropJsonContext.Default.StartPairingRequest),
        };
        request.Headers.Add("Origin", "http://attacker.example");

        using var response = await daemon.Api.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var status = await daemon.Api.GetAsync("api/pairing");
        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
    }
}
