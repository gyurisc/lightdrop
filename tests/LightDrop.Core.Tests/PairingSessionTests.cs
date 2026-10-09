using LightDrop.Core.Pairing;
using LightDrop.Core.Tests.Fakes;

namespace LightDrop.Core.Tests;

/// <summary>
/// The rules of one pairing window: who dials, what moves it forward, when it dies.
/// </summary>
/// <remarks>
/// The property everything else rests on is that a session reaches "pin" only when <em>both</em>
/// humans said yes, inside the deadline, about a key that actually joined. So most of these
/// tests are about the ways it must not get there.
/// </remarks>
public sealed class PairingSessionTests
{
    private const string LowerId = "1111aaaa";
    private const string HigherId = "eeee0000";

    private static readonly byte[] LocalKey = [1, 2, 3, 4];
    private static readonly byte[] PeerKey = [5, 6, 7, 8];
    private static readonly byte[] OtherKey = [9, 9, 9, 9];

    private static readonly byte[] DialerNonce = Enumerable.Repeat((byte)0x11, PairingCode.NonceLength).ToArray();

    /// <summary>A session on the listening side: this device has the lower id.</summary>
    private static PairingSession Create(FakeTimeProvider clock) =>
        new(LowerId, LocalKey, HigherId, "Work Laptop", clock);

    /// <summary>A session on the dialing side: this device has the higher id.</summary>
    private static PairingSession CreateDialer(FakeTimeProvider clock) =>
        new(HigherId, LocalKey, LowerId, "Work Laptop", clock);

    /// <summary>A listening session the test, playing the dialer, has joined and revealed to.</summary>
    private static PairingSession Joined(FakeTimeProvider clock)
    {
        var session = Create(clock);
        Assert.NotNull(session.JoinAsListener(PeerKey));
        Assert.NotNull(session.RevealAsListener(DialerNonce));
        return session;
    }

    [Fact]
    public void LowerIdListensAndHigherIdDials()
    {
        // Deterministic from the two ids alone, so both machines agree on roles without talking,
        // and never both dial or both wait.
        Assert.Equal(PairingRole.Listener, PairingSession.RoleFor(LowerId, HigherId));
        Assert.Equal(PairingRole.Dialer, PairingSession.RoleFor(HigherId, LowerId));
    }

    [Fact]
    public void RolesCompareOrdinallyNotCulturally()
    {
        // Ordinal: 'B' (0x42) sorts before 'a' (0x61). A culture-aware compare would put them the
        // other way round, and two machines with different cultures could then both dial.
        Assert.Equal(PairingRole.Listener, PairingSession.RoleFor("B", "a"));
        Assert.Equal(PairingRole.Dialer, PairingSession.RoleFor("a", "B"));
    }

    [Fact]
    public void RefusesToPairWithItself()
    {
        Assert.Throws<ArgumentException>(() => PairingSession.RoleFor(LowerId, LowerId));
    }

    [Fact]
    public void StartsWaitingForThePeerWithNoCode()
    {
        var session = Create(new FakeTimeProvider());

        Assert.Equal(PairingState.WaitingForPeer, session.State);
        Assert.Null(session.Snapshot().Code);
        Assert.True(session.IsOpen);
    }

    [Fact]
    public void TheListenerShowsTheCodeOnlyOnceTheDialerRevealed()
    {
        var session = Create(new FakeTimeProvider());

        var commitment = session.JoinAsListener(PeerKey);
        Assert.NotNull(commitment);
        Assert.Equal(PairingState.WaitingForPeer, session.State);
        Assert.Null(session.Snapshot().Code);

        var listenerNonce = session.RevealAsListener(DialerNonce);
        Assert.NotNull(listenerNonce);

        // The nonce handed back is the one committed to, and the code covers both nonces.
        Assert.True(PairingCode.Opens(commitment, LocalKey, PeerKey, listenerNonce));
        Assert.Equal(PairingState.Confirming, session.State);
        Assert.Equal(PairingCode.Derive(PeerKey, LocalKey, DialerNonce, listenerNonce), session.Snapshot().Code);
    }

    [Fact]
    public void TheDialerShowsTheCodeOnlyOnceTheCommitmentOpens()
    {
        var session = CreateDialer(new FakeTimeProvider());
        var listenerNonce = PairingCode.CreateNonce();

        var dialerNonce = session.JoinAsDialer(PeerKey, PairingCode.Commit(PeerKey, LocalKey, listenerNonce));
        Assert.NotNull(dialerNonce);
        Assert.Equal(PairingState.WaitingForPeer, session.State);

        Assert.True(session.OpenAsDialer(listenerNonce));
        Assert.Equal(PairingState.Confirming, session.State);
        Assert.Equal(PairingCode.Derive(LocalKey, PeerKey, dialerNonce, listenerNonce), session.Snapshot().Code);
    }

    [Fact]
    public void BothSidesOfAnHonestExchangeShowTheSameCode()
    {
        var clock = new FakeTimeProvider();
        var listener = new PairingSession(LowerId, LocalKey, HigherId, "Mac Mini", clock);
        var dialer = new PairingSession(HigherId, PeerKey, LowerId, "Desk PC", clock);

        var commitment = listener.JoinAsListener(PeerKey)!;
        var dialerNonce = dialer.JoinAsDialer(LocalKey, commitment)!;
        var listenerNonce = listener.RevealAsListener(dialerNonce)!;
        Assert.True(dialer.OpenAsDialer(listenerNonce));

        Assert.NotNull(listener.Snapshot().Code);
        Assert.Equal(listener.Snapshot().Code, dialer.Snapshot().Code);
    }

    [Fact]
    public void TheDialerFailsTheSessionWhenTheNonceDoesNotOpenTheCommitment()
    {
        // A listener that changes its nonce after seeing the dialer's is choosing the digits.
        var session = CreateDialer(new FakeTimeProvider());
        session.JoinAsDialer(PeerKey, PairingCode.Commit(PeerKey, LocalKey, PairingCode.CreateNonce()));

        Assert.False(session.OpenAsDialer(PairingCode.CreateNonce()));

        Assert.Equal(PairingState.Failed, session.State);
        Assert.Null(session.Snapshot().Code);
        Assert.False(session.Decide(accepted: true));
    }

    [Fact]
    public void TheListenerTakesOnlyTheFirstRevealedNonce()
    {
        // After answering, this side's nonce is public. Accepting a second, different nonce would
        // let the sender pick one that makes the digits come out as it likes.
        var session = Create(new FakeTimeProvider());
        session.JoinAsListener(PeerKey);
        var listenerNonce = session.RevealAsListener(DialerNonce);
        var code = session.Snapshot().Code;

        Assert.Null(session.RevealAsListener(PairingCode.CreateNonce()));
        Assert.Equal(code, session.Snapshot().Code);

        // A retry of the same nonce, from a dialer that lost the first answer, gets the same answer.
        Assert.Equal(listenerNonce, session.RevealAsListener(DialerNonce));
    }

    [Fact]
    public void NothingIsRevealedBeforeAKeyJoined()
    {
        var session = Create(new FakeTimeProvider());

        Assert.Null(session.RevealAsListener(DialerNonce));
        Assert.Equal(PairingState.WaitingForPeer, session.State);
    }

    [Fact]
    public void AMalformedNonceIsRefused()
    {
        var session = Create(new FakeTimeProvider());
        session.JoinAsListener(PeerKey);

        Assert.Null(session.RevealAsListener(DialerNonce[..^1]));
        Assert.Equal(PairingState.WaitingForPeer, session.State);
    }

    [Fact]
    public void EachStepBelongsToOneRole()
    {
        // A programming error, not a peer's doing: the coordinator picks the steps by role.
        var listener = Create(new FakeTimeProvider());
        var dialer = CreateDialer(new FakeTimeProvider());

        Assert.Throws<InvalidOperationException>(() => listener.JoinAsDialer(PeerKey, new byte[PairingCode.CommitmentLength]));
        Assert.Throws<InvalidOperationException>(() => listener.OpenAsDialer(DialerNonce));
        Assert.Throws<InvalidOperationException>(() => dialer.JoinAsListener(PeerKey));
        Assert.Throws<InvalidOperationException>(() => dialer.RevealAsListener(DialerNonce));
    }

    [Fact]
    public void OnlyOnePeerCanJoin()
    {
        // The window accepts any certificate, so the first key to join is the only one this
        // session will ever compare digits over. A second joiner must not replace it.
        var session = Create(new FakeTimeProvider());
        session.JoinAsListener(PeerKey);

        Assert.Null(session.JoinAsListener(OtherKey));
        Assert.True(session.IsPeerKey(PeerKey));
        Assert.False(session.IsPeerKey(OtherKey));
    }

    [Fact]
    public void TheJoinedKeySayingHelloAgainGetsTheSameCommitment()
    {
        // A dialer whose first answer was lost in transit says hello again. A fresh commitment
        // would mean a fresh nonce; the same one lets it carry on.
        var session = Create(new FakeTimeProvider());
        var first = session.JoinAsListener(PeerKey);

        Assert.Equal(first, session.JoinAsListener(PeerKey));
    }

    [Fact]
    public void HelloIsOverOnceTheCodeIsShowing()
    {
        var session = Joined(new FakeTimeProvider());

        Assert.Null(session.JoinAsListener(PeerKey));
    }

    [Fact]
    public void AKeyEqualToThisDevicesOwnCannotJoin()
    {
        var listener = Create(new FakeTimeProvider());
        var dialer = CreateDialer(new FakeTimeProvider());

        Assert.Null(listener.JoinAsListener(LocalKey));
        Assert.Null(dialer.JoinAsDialer(LocalKey, new byte[PairingCode.CommitmentLength]));
        Assert.Equal(PairingState.WaitingForPeer, listener.State);
        Assert.Equal(PairingState.WaitingForPeer, dialer.State);
    }

    [Fact]
    public void NoKeyMatchesBeforeAnyoneJoined()
    {
        var session = Create(new FakeTimeProvider());

        Assert.False(session.IsPeerKey(PeerKey));
        Assert.Null(session.PeerPublicKey);
    }

    [Fact]
    public void BothAcceptingIsReadyToPin()
    {
        var session = Joined(new FakeTimeProvider());

        Assert.True(session.Decide(accepted: true));
        Assert.False(session.BothAccepted);

        Assert.True(session.RecordPeerDecision(accepted: true));
        Assert.True(session.BothAccepted);

        session.MarkPinned();
        Assert.Equal(PairingState.Paired, session.State);
        Assert.False(session.IsOpen);
    }

    [Fact]
    public void TheOrderOfTheTwoDecisionsDoesNotMatter()
    {
        var session = Joined(new FakeTimeProvider());

        session.RecordPeerDecision(accepted: true);
        session.Decide(accepted: true);

        Assert.True(session.BothAccepted);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void EitherSideDecliningRejectsWithoutPinning(bool local, bool peer)
    {
        var session = Joined(new FakeTimeProvider());

        session.Decide(local);
        session.RecordPeerDecision(peer);

        Assert.Equal(PairingState.Rejected, session.State);
        Assert.False(session.BothAccepted);
        Assert.Throws<InvalidOperationException>(session.MarkPinned);
    }

    [Fact]
    public void ARejectionSaysWhichSideDeclined()
    {
        var here = Joined(new FakeTimeProvider());
        here.Decide(accepted: false);

        var there = Joined(new FakeTimeProvider());
        there.RecordPeerDecision(accepted: false);

        Assert.Contains("this device", here.Snapshot().Reason, StringComparison.Ordinal);
        Assert.Contains("other device", there.Snapshot().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ALocalDecisionCannotBeChanged()
    {
        // A "yes" sent after a "no" must not resurrect a rejected session.
        var session = Joined(new FakeTimeProvider());

        session.Decide(accepted: false);

        Assert.False(session.Decide(accepted: true));
        Assert.Equal(PairingState.Rejected, session.State);
    }

    [Fact]
    public void APeerDecisionCanBeRepeatedButNotChanged()
    {
        // Repeated because a dialer may retry a confirm whose response it never saw; changed
        // would let the second answer overrule the first.
        var session = Joined(new FakeTimeProvider());

        Assert.True(session.RecordPeerDecision(accepted: true));
        Assert.True(session.RecordPeerDecision(accepted: true));
        Assert.False(session.RecordPeerDecision(accepted: false));
        Assert.Equal(PairingState.Confirming, session.State);
    }

    [Fact]
    public void NoDecisionIsTakenBeforeThePeerJoined()
    {
        // There is no code yet, so there is nothing a human could have compared -- including
        // after the key joined but before the nonces were exchanged.
        var session = Create(new FakeTimeProvider());
        session.JoinAsListener(PeerKey);

        Assert.False(session.Decide(accepted: true));
        Assert.False(session.RecordPeerDecision(accepted: true));
        Assert.False(session.BothAccepted);
    }

    [Fact]
    public void ExpiresWhenNoPeerJoinsInTime()
    {
        var clock = new FakeTimeProvider();
        var session = Create(clock);

        clock.Advance(PairingSession.JoinTimeout - TimeSpan.FromSeconds(1));
        Assert.Equal(PairingState.WaitingForPeer, session.State);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PairingState.Expired, session.State);
        Assert.Null(session.JoinAsListener(PeerKey));
    }

    [Fact]
    public void TheCodeAppearingRestartsTheClockForConfirmation()
    {
        // Sixty seconds to find each other, then a fresh sixty to read and compare digits --
        // otherwise a slow join would leave the humans no time at all.
        var clock = new FakeTimeProvider();
        var session = Create(clock);

        clock.Advance(TimeSpan.FromSeconds(50));
        session.JoinAsListener(PeerKey);
        session.RevealAsListener(DialerNonce);

        clock.Advance(PairingSession.ConfirmTimeout - TimeSpan.FromSeconds(1));
        Assert.Equal(PairingState.Confirming, session.State);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PairingState.Expired, session.State);
    }

    [Fact]
    public void ADecisionAfterExpiryChangesNothing()
    {
        var clock = new FakeTimeProvider();
        var session = Joined(clock);
        session.RecordPeerDecision(accepted: true);

        clock.Advance(PairingSession.ConfirmTimeout);

        Assert.False(session.Decide(accepted: true));
        Assert.False(session.BothAccepted);
        Assert.Equal(PairingState.Expired, session.State);
    }

    [Fact]
    public void BothAcceptingHoldsOffExpiryWhileThePinIsWritten()
    {
        // Pinning follows immediately, but it is I/O. Both humans already said yes inside the
        // window; the deadline passing during the write must not turn that into "expired".
        var clock = new FakeTimeProvider();
        var session = Joined(clock);
        session.Decide(accepted: true);
        session.RecordPeerDecision(accepted: true);

        clock.Advance(PairingSession.ConfirmTimeout + PairingSession.PinGrace - TimeSpan.FromSeconds(1));

        Assert.True(session.BothAccepted);
        session.MarkPinned();
        Assert.Equal(PairingState.Paired, session.State);
    }

    [Fact]
    public void BothAcceptingButNeverPinningStillClosesTheWindow()
    {
        // An open session is an open TLS gate. If nothing pins -- the request that would have
        // gone away, a write that hangs -- the window must still close.
        var clock = new FakeTimeProvider();
        var session = Joined(clock);
        session.Decide(accepted: true);
        session.RecordPeerDecision(accepted: true);

        clock.Advance(PairingSession.ConfirmTimeout + PairingSession.PinGrace);

        Assert.Equal(PairingState.Expired, session.State);
        Assert.False(session.IsOpen);
        Assert.False(session.BothAccepted);
    }

    [Fact]
    public void APinThatFinishesLateIsStillRecorded()
    {
        // The write outlived the grace, but it did land: the state says what is on disk.
        var clock = new FakeTimeProvider();
        var session = Joined(clock);
        session.Decide(accepted: true);
        session.RecordPeerDecision(accepted: true);
        clock.Advance(PairingSession.ConfirmTimeout + PairingSession.PinGrace);

        session.MarkPinned();

        Assert.Equal(PairingState.Paired, session.State);
    }

    [Fact]
    public void CancellingIsRefusedOnceBothAccepted()
    {
        // Ctrl+C landing while the pin is written must not report "cancelled, nothing saved"
        // over a pin that is about to be on disk -- or already is, on the other device.
        var session = Joined(new FakeTimeProvider());
        session.Decide(accepted: true);
        session.RecordPeerDecision(accepted: true);

        Assert.False(session.Cancel());
        Assert.Equal(PairingState.Confirming, session.State);

        session.MarkPinned();
        Assert.Equal(PairingState.Paired, session.State);
    }

    [Fact]
    public void AWithdrawnYesFromThePeerNoLongerCounts()
    {
        // The dialer said yes, then stopped waiting for the answer. This side's later yes must not
        // reach both-accepted with nobody left to hear it.
        var session = Joined(new FakeTimeProvider());
        session.RecordPeerDecision(accepted: true);

        Assert.True(session.WithdrawPeerDecision());
        session.Decide(accepted: true);
        Assert.False(session.BothAccepted);

        // It says yes again when it comes back, and that counts.
        Assert.True(session.RecordPeerDecision(accepted: true));
        Assert.True(session.BothAccepted);
    }

    [Fact]
    public void AYesCannotBeWithdrawnOnceBothAccepted()
    {
        var session = Joined(new FakeTimeProvider());
        session.Decide(accepted: true);
        session.RecordPeerDecision(accepted: true);

        Assert.False(session.WithdrawPeerDecision());
        Assert.True(session.BothAccepted);
    }

    [Fact]
    public void CancellingEndsAnOpenSession()
    {
        var session = Joined(new FakeTimeProvider());

        Assert.True(session.Cancel());

        Assert.Equal(PairingState.Cancelled, session.State);
        Assert.False(session.Decide(accepted: true));
    }

    [Fact]
    public void FailingEndsAnOpenSessionWithTheReason()
    {
        var session = Create(new FakeTimeProvider());

        Assert.True(session.Fail("The peer answered as someone else."));

        Assert.Equal(PairingState.Failed, session.State);
        Assert.Equal("The peer answered as someone else.", session.Snapshot().Reason);
    }

    [Fact]
    public void AnEndedSessionStaysEnded()
    {
        var session = Joined(new FakeTimeProvider());
        session.Decide(accepted: false);

        Assert.False(session.Cancel());
        Assert.False(session.Fail("late"));
        Assert.Equal(PairingState.Rejected, session.State);
    }

    [Fact]
    public void ThePeerKeyIsHandedOutAsACopy()
    {
        // The key that gets pinned must be the key that joined, not whatever a caller later wrote
        // into an array it was handed.
        var session = Joined(new FakeTimeProvider());

        var copy = session.PeerPublicKey!;
        copy[0] = 0xFF;

        Assert.True(session.IsPeerKey(PeerKey));
    }

    [Fact]
    public void SnapshotCarriesWhatTheCliShows()
    {
        var clock = new FakeTimeProvider();
        var session = Create(clock);

        var snapshot = session.Snapshot();

        Assert.Equal(HigherId, snapshot.PeerDeviceId);
        Assert.Equal("Work Laptop", snapshot.PeerName);
        Assert.Equal(PairingRole.Listener, snapshot.Role);
        Assert.Equal(clock.GetUtcNow() + PairingSession.JoinTimeout, snapshot.Deadline);
    }
}
