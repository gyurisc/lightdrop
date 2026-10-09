using System.Net;
using System.Net.Http.Json;
using LightDrop.Core;
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
    private static async Task<TestDaemon> ListenerAwaitingAsync(TimeProvider? clock = null)
    {
        var daemon = await TestDaemon.StartAsync(LowId, "Desk PC", [], clock);
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
        Assert.Equal(PairingCode.CommitmentLength, answer.Commitment.Length);
    }

    [Fact]
    public async Task ListenerRefusesHelloFromAnotherProtocolVersion()
    {
        // A build that speaks a different pairing exchange would join and then never finish it.
        await using var daemon = await ListenerAwaitingAsync();

        using (var other = await PeerClient.HelloAsync(
                   keys.Bob, daemon.PeerAddress, HighId, protocolVersion: LightDropVersion.Protocol + 1))
        {
            Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        }

        Assert.Equal(PairingState.WaitingForPeer, (await daemon.GetPairingAsync()).State);

        using var same = await PeerClient.HelloAsync(keys.Bob, daemon.PeerAddress, HighId);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
    }

    [Fact]
    public async Task ListenerShowsTheCodeOnlyOnceTheJoinedKeyRevealed()
    {
        await using var daemon = await ListenerAwaitingAsync();
        var listenerKey = (await daemon.GetKeyAsync()).PublicKeyInfo;

        (await PeerClient.HelloAsync(keys.Bob, daemon.PeerAddress, HighId)).Dispose();
        Assert.Null((await daemon.GetPairingAsync()).Code);

        var joined = await PeerClient.JoinAsync(keys.Bob, daemon.PeerAddress, HighId);

        // The nonce revealed is the one committed to at hello, and the code covers both nonces.
        Assert.True(PairingCode.Opens(joined.Commitment, listenerKey, keys.Bob.PublicKeyInfo, joined.ListenerNonce));
        Assert.Equal(
            PairingCode.Derive(keys.Bob.PublicKeyInfo, listenerKey, joined.DialerNonce, joined.ListenerNonce),
            (await daemon.GetPairingAsync()).Code);
    }

    [Fact]
    public async Task ListenerRefusesARevealFromAKeyOtherThanTheOneThatJoined()
    {
        // A stranger revealing first would fix the dialer's half of the code.
        await using var daemon = await ListenerAwaitingAsync();
        (await PeerClient.HelloAsync(keys.Bob, daemon.PeerAddress, HighId)).Dispose();

        using (var impostor = await PeerClient.RevealAsync(keys.Impostor, daemon.PeerAddress, PairingCode.CreateNonce()))
        {
            Assert.Equal(HttpStatusCode.Forbidden, impostor.StatusCode);
        }

        Assert.Equal(PairingState.WaitingForPeer, (await daemon.GetPairingAsync()).State);

        using var bob = await PeerClient.RevealAsync(keys.Bob, daemon.PeerAddress, PairingCode.CreateNonce());
        Assert.Equal(HttpStatusCode.OK, bob.StatusCode);
        Assert.Equal(PairingState.Confirming, (await daemon.GetPairingAsync()).State);
    }

    [Fact]
    public async Task ListenerTakesOnlyTheFirstNonceRevealed()
    {
        // Once the listener has answered, its nonce is public. Accepting a second, different nonce
        // would let the sender search for one that makes this screen show the digits it wants.
        await using var daemon = await ListenerAwaitingAsync();
        var joined = await PeerClient.JoinAsync(keys.Bob, daemon.PeerAddress, HighId);
        var code = (await daemon.GetPairingAsync()).Code;

        using (var another = await PeerClient.RevealAsync(keys.Bob, daemon.PeerAddress, PairingCode.CreateNonce()))
        {
            Assert.Equal(HttpStatusCode.Conflict, another.StatusCode);
        }

        Assert.Equal(code, (await daemon.GetPairingAsync()).Code);

        // A retry of the same nonce gets the same answer.
        using var retry = await PeerClient.RevealAsync(keys.Bob, daemon.PeerAddress, joined.DialerNonce);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(
            joined.ListenerNonce,
            (await retry.Content.ReadFromJsonAsync(LightDropJsonContext.Default.PairingNonce))!.Nonce);
    }

    [Fact]
    public async Task TheJoinedKeyMaySayHelloAgainUntilItRevealed()
    {
        // A dialer whose hello answer was lost on a slow link retries. It must get the same
        // commitment, not a 409 that leaves both sides stuck until the window expires.
        await using var daemon = await ListenerAwaitingAsync();

        using var first = await PeerClient.HelloAsync(keys.Bob, daemon.PeerAddress, HighId);
        using var again = await PeerClient.HelloAsync(keys.Bob, daemon.PeerAddress, HighId);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(
            (await first.Content.ReadFromJsonAsync(LightDropJsonContext.Default.PairHelloResponse))!.Commitment,
            (await again.Content.ReadFromJsonAsync(LightDropJsonContext.Default.PairHelloResponse))!.Commitment);
    }

    [Fact]
    public async Task ListenerAcceptsOnlyOneHello()
    {
        // The first key in is the one the code is derived from; a second hello must not join.
        await using var daemon = await ListenerAwaitingAsync();

        await PeerClient.JoinAsync(keys.Bob, daemon.PeerAddress, HighId);
        var code = (await daemon.GetPairingAsync()).Code;

        using var second = await PeerClient.HelloAsync(keys.Impostor, daemon.PeerAddress, HighId);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(code, (await daemon.GetPairingAsync()).Code);
    }

    [Fact]
    public async Task ListenerRefusesAConfirmFromAKeyOtherThanTheOneThatJoined()
    {
        await using var daemon = await ListenerAwaitingAsync();

        await PeerClient.JoinAsync(keys.Bob, daemon.PeerAddress, HighId);
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

        await PeerClient.JoinAsync(keys.Bob, daemon.PeerAddress, HighId);
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
    public async Task AfterTheDeadlineEvenTheJoinedKeyIsRefusedAtTheHandshake()
    {
        // The joined key's pass through the TLS gate lasts only as long as the session's deadline.
        // Without that bound, a key the humans just rejected would get through indefinitely.
        var clock = new ManualClock();
        await using var daemon = await ListenerAwaitingAsync(clock);

        await PeerClient.JoinAsync(keys.Bob, daemon.PeerAddress, HighId);
        (await daemon.ConfirmAsync(accepted: false)).Dispose();

        using (var beforeDeadline = await PeerClient.GetAsync(keys.Bob, daemon.PeerAddress, "pair/anything"))
        {
            Assert.Equal(HttpStatusCode.NotFound, beforeDeadline.StatusCode);
        }

        clock.Advance(PairingSession.ConfirmTimeout);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => PeerClient.GetAsync(keys.Bob, daemon.PeerAddress, "pair/anything"));
    }

    [Fact]
    public async Task ARetriedConfirmAfterThePinStillHearsYes()
    {
        // The pin happened here, but the answer was lost on the way back. The dialer's retry must
        // hear "yes", not a conflict it reads as "the other side gave up, nothing was saved".
        await using var daemon = await ListenerAwaitingAsync();
        await PeerClient.JoinAsync(keys.Bob, daemon.PeerAddress, HighId);
        (await daemon.ConfirmAsync(accepted: true)).Dispose();

        (await PeerClient.ConfirmAsync(keys.Bob, daemon.PeerAddress, accepted: true)).Dispose();
        await daemon.WaitForStateAsync(PairingState.Paired);

        using var retry = await PeerClient.ConfirmAsync(keys.Bob, daemon.PeerAddress, accepted: true);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.True((await retry.Content.ReadFromJsonAsync(LightDropJsonContext.Default.PairingDecision))!.Accepted);
        Assert.Single(await daemon.GetTrustedAsync());
    }

    [Fact]
    public async Task ADialerThatStopsWaitingDoesNotHoldTheWindowOpen()
    {
        // The dialer says yes, then gives up on the answer (Ctrl+C, a restart, a dropped link).
        // This side's user says yes afterwards. Nothing is left to pin or to tell, so the window
        // must still close at its deadline -- not sit open, admitting any certificate, for ever.
        var clock = new ManualClock();
        await using var daemon = await ListenerAwaitingAsync(clock);
        await PeerClient.JoinAsync(keys.Bob, daemon.PeerAddress, HighId);

        await GiveUpOnAConfirmAsync(daemon);
        (await daemon.ConfirmAsync(accepted: true)).Dispose();

        // The ordinary deadline, not the pin grace: with the dialer's "yes" withdrawn this is a
        // session nobody finished, and it ends on time. (The grace bound is a Core test.)
        clock.Advance(PairingSession.ConfirmTimeout);

        Assert.Equal(PairingState.Expired, (await daemon.WaitForEndAsync()).State);
        Assert.Empty(await daemon.GetTrustedAsync());
        await Assert.ThrowsAsync<HttpRequestException>(
            () => PeerClient.GetAsync(keys.Impostor, daemon.PeerAddress, "pair/anything"));
    }

    [Fact]
    public async Task ADialerThatComesBackAfterGivingUpCanStillFinish()
    {
        // The control for the test above: its "yes" was forgotten, not refused for good.
        await using var daemon = await ListenerAwaitingAsync();
        await PeerClient.JoinAsync(keys.Bob, daemon.PeerAddress, HighId);

        await GiveUpOnAConfirmAsync(daemon);
        (await daemon.ConfirmAsync(accepted: true)).Dispose();

        using var back = await PeerClient.ConfirmAsync(keys.Bob, daemon.PeerAddress, accepted: true);

        Assert.True((await back.Content.ReadFromJsonAsync(LightDropJsonContext.Default.PairingDecision))!.Accepted);
        await daemon.WaitForStateAsync(PairingState.Paired);
        Assert.Equal(HighId, Assert.Single(await daemon.GetTrustedAsync()).DeviceId);
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
    public async Task DialerSendsNothingAfterHelloToAKeyOtherThanTheOneItMet()
    {
        // Hello is answered holding Alice's key; every connection after that presents the
        // impostor's. Neither the dialer's nonce nor its "yes" may reach the impostor, and
        // nothing is pinned.
        await using var fake = await ScriptedPairingListener.StartAsync(
            keys.Alice, answerAs: LowId, keyAfterHello: keys.Impostor);
        await using var dialer = await TestDaemon.StartAsync(HighId, "Mac Mini");
        dialer.Transport.Announce(LowId, "Desk PC", port: fake.Port, address: "127.0.0.1");

        (await dialer.StartPairingAsync("Desk PC")).Dispose();

        var ended = await dialer.WaitForEndAsync();
        Assert.Equal(PairingState.Failed, ended.State);
        Assert.Null(ended.Code);
        Assert.Equal(0, fake.RevealCalls);
        Assert.Equal(0, fake.ConfirmCalls);
        Assert.Empty(await dialer.GetTrustedAsync());
    }

    [Fact]
    public async Task DialerAbandonsAListenerWhoseNonceDoesNotOpenItsCommitment()
    {
        // The listener answering the reveal with a nonce it did not commit to is choosing the
        // digits after seeing the dialer's nonce -- the search the commitment exists to prevent.
        await using var fake = await ScriptedPairingListener.StartAsync(
            keys.Alice, answerAs: LowId, revealAnotherNonce: true);
        await using var dialer = await TestDaemon.StartAsync(HighId, "Mac Mini");
        dialer.Transport.Announce(LowId, "Desk PC", port: fake.Port, address: "127.0.0.1");

        (await dialer.StartPairingAsync("Desk PC")).Dispose();

        var ended = await dialer.WaitForEndAsync();
        Assert.Equal(PairingState.Failed, ended.State);
        Assert.Null(ended.Code);
        Assert.Equal(1, fake.RevealCalls);
        Assert.Equal(0, fake.ConfirmCalls);
        Assert.Empty(await dialer.GetTrustedAsync());
    }

    [Fact]
    public async Task DialerPinsTheKeyItMetAtHelloWhenItStaysTheSame()
    {
        // The control for the two tests above: the same scripted listener, keeping its key and
        // its word, pairs.
        await using var fake = await ScriptedPairingListener.StartAsync(keys.Alice, answerAs: LowId);
        await using var dialer = await TestDaemon.StartAsync(HighId, "Mac Mini");
        dialer.Transport.Announce(LowId, "Desk PC", port: fake.Port, address: "127.0.0.1");

        (await dialer.StartPairingAsync("Desk PC")).Dispose();
        var showing = await dialer.WaitForStateAsync(PairingState.Confirming);

        Assert.Equal(
            PairingCode.Derive((await dialer.GetKeyAsync()).PublicKeyInfo, keys.Alice.PublicKeyInfo, fake.RevealedByDialer!, fake.Nonce),
            showing.Code);

        (await dialer.ConfirmAsync(accepted: true)).Dispose();

        await dialer.WaitForStateAsync(PairingState.Paired);
        Assert.Equal(1, fake.ConfirmCalls);
        Assert.Equal("Desk PC", Assert.Single(await dialer.GetTrustedAsync()).DeviceName);
    }

    /// <summary>
    /// Plays a dialer that says yes and then stops waiting for the answer.
    /// </summary>
    private async Task GiveUpOnAConfirmAsync(TestDaemon daemon)
    {
        using (var gaveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => PeerClient.ConfirmAsync(keys.Bob, daemon.PeerAddress, accepted: true, gaveUp.Token));
        }

        // Lets the listener notice the connection is gone before its own user answers. Not an
        // assertion on timing: if it were too short the session would pin instead, and the test
        // that expects expiry would say so.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
    }
}
