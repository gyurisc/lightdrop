using System.Net;
using System.Net.Http.Json;
using LightDrop.Core.Contracts;
using LightDrop.Core.Pairing;
using LightDrop.Daemon.Tests.TestSupport;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// The checks that keep an open pairing window from being hijacked, each tested against a
/// misbehaving other side that a real daemon would never be.
/// </summary>
/// <remarks>
/// While a window is open the peer listener's handshake admits any certificate, so these checks
/// -- not TLS -- are what tie the eventual pin to the device the humans compared digits with.
/// Each one is verified by mutation: replace it with <c>true</c> and the test named for it fails.
/// </remarks>
public sealed class PairingGuardTests(PairingKeys keys) : IClassFixture<PairingKeys>
{
    /// <summary>Sorts first, so a daemon with this id listens.</summary>
    private const string LowId = "11111111111111111111111111111111";

    /// <summary>Sorts last, so a daemon with this id dials.</summary>
    private const string HighId = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    private const string SomeoneElseId = "cccccccccccccccccccccccccccccccc";

    /// <summary>A listening daemon with a window open for <see cref="HighId"/>, which the test plays.</summary>
    private static async Task<TestDaemon> ListenerAwaitingAsync()
    {
        var daemon = await TestDaemon.StartAsync(LowId, "Desk PC");
        daemon.Transport.Announce(HighId, "Mac Mini", address: "127.0.0.1");

        using var started = await daemon.StartPairingAsync("Mac Mini");
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal(PairingRole.Listener, (await daemon.GetPairingAsync()).Role);

        return daemon;
    }

    [Fact]
    public async Task ListenerRefusesHelloFromADeviceItIsNotPairingWith()
    {
        await using var daemon = await ListenerAwaitingAsync();

        using (var stranger = await PeerClient.HelloAsync(keys.Impostor, daemon.PeerAddress, SomeoneElseId))
        {
            Assert.Equal(HttpStatusCode.Conflict, stranger.StatusCode);
        }

        Assert.Equal(PairingState.WaitingForPeer, (await daemon.GetPairingAsync()).State);

        // The control: the expected device is still let in afterwards.
        using var expected = await PeerClient.HelloAsync(keys.Bob, daemon.PeerAddress, HighId);
        Assert.Equal(HttpStatusCode.OK, expected.StatusCode);

        var answer = await expected.Content.ReadFromJsonAsync(LightDropJsonContext.Default.PairHelloResponse);
        Assert.Equal(LowId, answer!.DeviceId);
        Assert.Equal(PairingState.Confirming, (await daemon.GetPairingAsync()).State);
    }

    [Fact]
    public async Task ListenerShowsTheCodeForTheKeyThatSaidHello()
    {
        await using var daemon = await ListenerAwaitingAsync();

        (await PeerClient.HelloAsync(keys.Bob, daemon.PeerAddress, HighId)).Dispose();

        var status = await daemon.GetPairingAsync();
        Assert.Equal(PairingCode.Derive((await daemon.GetKeyAsync()).PublicKeyInfo, keys.Bob.PublicKeyInfo), status.Code);
    }

    [Fact]
    public async Task ListenerAcceptsOnlyOneHello()
    {
        // The first key in is the one the code is derived from; a second hello must not join.
        await using var daemon = await ListenerAwaitingAsync();

        (await PeerClient.HelloAsync(keys.Bob, daemon.PeerAddress, HighId)).Dispose();
        var code = (await daemon.GetPairingAsync()).Code;

        using var second = await PeerClient.HelloAsync(keys.Impostor, daemon.PeerAddress, HighId);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(code, (await daemon.GetPairingAsync()).Code);
    }

    [Fact]
    public async Task ListenerRefusesAConfirmFromAKeyOtherThanTheOneThatJoined()
    {
        await using var daemon = await ListenerAwaitingAsync();

        (await PeerClient.HelloAsync(keys.Bob, daemon.PeerAddress, HighId)).Dispose();
        (await daemon.ConfirmAsync(accepted: true)).Dispose();

        // The impostor gets through TLS -- the window is open -- and says yes on Bob's behalf.
        using (var impostor = await PeerClient.ConfirmAsync(keys.Impostor, daemon.PeerAddress, accepted: true))
        {
            Assert.Equal(HttpStatusCode.Forbidden, impostor.StatusCode);
        }

        Assert.Empty(await daemon.GetTrustedAsync());
        Assert.Equal(PairingState.Confirming, (await daemon.GetPairingAsync()).State);

        // The control: Bob, whose key joined, completes it, and Bob's key is what gets pinned.
        using var bob = await PeerClient.ConfirmAsync(keys.Bob, daemon.PeerAddress, accepted: true);
        Assert.Equal(HttpStatusCode.OK, bob.StatusCode);
        Assert.True((await bob.Content.ReadFromJsonAsync(LightDropJsonContext.Default.PairingDecision))!.Accepted);

        await daemon.WaitForStateAsync(PairingState.Paired);
        Assert.Equal(HighId, Assert.Single(await daemon.GetTrustedAsync()).DeviceId);

        using var pinned = await PeerClient.GetAsync(keys.Bob, daemon.PeerAddress, "health");
        Assert.Equal(HttpStatusCode.NotFound, pinned.StatusCode);
    }

    [Fact]
    public async Task AfterTheWindowClosesOnlyTheJoinedKeyIsStillAdmitted()
    {
        // This side said no first, which ends the session here at once. The device that joined
        // must still get through the handshake to learn the answer -- and nobody else may.
        await using var daemon = await ListenerAwaitingAsync();

        (await PeerClient.HelloAsync(keys.Bob, daemon.PeerAddress, HighId)).Dispose();
        (await daemon.ConfirmAsync(accepted: false)).Dispose();
        Assert.Equal(PairingState.Rejected, (await daemon.GetPairingAsync()).State);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => PeerClient.GetAsync(keys.Impostor, daemon.PeerAddress, "pair/anything"));

        using var bob = await PeerClient.ConfirmAsync(keys.Bob, daemon.PeerAddress, accepted: true);
        Assert.Equal(HttpStatusCode.OK, bob.StatusCode);
        Assert.False((await bob.Content.ReadFromJsonAsync(LightDropJsonContext.Default.PairingDecision))!.Accepted);
        Assert.Empty(await daemon.GetTrustedAsync());
    }

    [Fact]
    public async Task ListenerRefusesHelloWhenItIsTheDialer()
    {
        // Roles are fixed by id. A daemon that should be dialing has nothing to join.
        await using var daemon = await TestDaemon.StartAsync(HighId, "Mac Mini");
        daemon.Transport.Announce(LowId, "Desk PC", port: FreeTcpPort.Get(), address: "127.0.0.1");
        (await daemon.StartPairingAsync("Desk PC")).Dispose();

        using var hello = await PeerClient.HelloAsync(keys.Bob, daemon.PeerAddress, LowId);

        Assert.Equal(HttpStatusCode.Conflict, hello.StatusCode);
    }

    [Fact]
    public async Task DialerAbandonsAListenerThatAnswersAsSomeoneElse()
    {
        // Whatever answers at the announced address must claim to be the device the user named.
        await using var fake = await ScriptedPairingListener.StartAsync(keys.Alice, answerAs: SomeoneElseId);
        await using var dialer = await TestDaemon.StartAsync(HighId, "Mac Mini");
        dialer.Transport.Announce(LowId, "Desk PC", port: fake.Port, address: "127.0.0.1");

        (await dialer.StartPairingAsync("Desk PC")).Dispose();

        var ended = await dialer.WaitForEndAsync();
        Assert.Equal(PairingState.Failed, ended.State);
        Assert.Null(ended.Code);
    }

    [Fact]
    public async Task DialerSendsItsConfirmOnlyToTheKeyItMetAtHello()
    {
        // Hello is answered holding Alice's key; every connection after that presents the
        // impostor's. The dialer's "yes" must never reach the impostor, and nothing is pinned.
        await using var fake = await ScriptedPairingListener.StartAsync(
            keys.Alice, answerAs: LowId, keyAfterHello: keys.Impostor);
        await using var dialer = await TestDaemon.StartAsync(HighId, "Mac Mini");
        dialer.Transport.Announce(LowId, "Desk PC", port: fake.Port, address: "127.0.0.1");

        (await dialer.StartPairingAsync("Desk PC")).Dispose();

        var showing = await dialer.WaitForStateAsync(PairingState.Confirming);
        Assert.Equal(PairingCode.Derive((await dialer.GetKeyAsync()).PublicKeyInfo, keys.Alice.PublicKeyInfo), showing.Code);

        (await dialer.ConfirmAsync(accepted: true)).Dispose();

        Assert.Equal(PairingState.Failed, (await dialer.WaitForEndAsync()).State);
        Assert.Equal(0, fake.ConfirmCalls);
        Assert.Empty(await dialer.GetTrustedAsync());
    }

    [Fact]
    public async Task DialerPinsTheKeyItMetAtHelloWhenItStaysTheSame()
    {
        // The control for the test above: the same scripted listener, keeping its key, pairs.
        await using var fake = await ScriptedPairingListener.StartAsync(keys.Alice, answerAs: LowId);
        await using var dialer = await TestDaemon.StartAsync(HighId, "Mac Mini");
        dialer.Transport.Announce(LowId, "Desk PC", port: fake.Port, address: "127.0.0.1");

        (await dialer.StartPairingAsync("Desk PC")).Dispose();
        await dialer.WaitForStateAsync(PairingState.Confirming);
        (await dialer.ConfirmAsync(accepted: true)).Dispose();

        await dialer.WaitForStateAsync(PairingState.Paired);
        Assert.Equal(1, fake.ConfirmCalls);
        Assert.Equal("Desk PC", Assert.Single(await dialer.GetTrustedAsync()).DeviceName);
    }
}
