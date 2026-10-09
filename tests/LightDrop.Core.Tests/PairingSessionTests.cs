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

    private static PairingSession Create(FakeTimeProvider clock, string local = LowerId, string peer = HigherId) =>
        new(local, LocalKey, peer, "Work Laptop", clock);

    private static PairingSession Joined(FakeTimeProvider clock)
    {
        var session = Create(clock);
        Assert.True(session.TryJoin(PeerKey));
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
    public void JoiningShowsTheDerivedCode()
    {
        var session = Joined(new FakeTimeProvider());

        Assert.Equal(PairingState.Confirming, session.State);
        Assert.Equal(PairingCode.Derive(LocalKey, PeerKey), session.Snapshot().Code);
    }

    [Fact]
    public void OnlyOnePeerCanJoin()
    {
        // The window accepts any certificate, so the first key to join is the only one this
        // session will ever compare digits over. A second joiner must not replace it.
        var session = Joined(new FakeTimeProvider());

        Assert.False(session.TryJoin(OtherKey));
        Assert.True(session.IsPeerKey(PeerKey));
        Assert.False(session.IsPeerKey(OtherKey));
    }

    [Fact]
    public void AKeyEqualToThisDevicesOwnCannotJoin()
    {
        var session = Create(new FakeTimeProvider());

        Assert.False(session.TryJoin(LocalKey));
        Assert.Equal(PairingState.WaitingForPeer, session.State);
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
        // There is no code yet, so there is nothing a human could have compared.
        var session = Create(new FakeTimeProvider());

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
        Assert.False(session.TryJoin(PeerKey));
    }

    [Fact]
    public void JoiningRestartsTheClockForConfirmation()
    {
        // Sixty seconds to find each other, then a fresh sixty to read and compare digits --
        // otherwise a slow join would leave the humans no time at all.
        var clock = new FakeTimeProvider();
        var session = Create(clock);

        clock.Advance(TimeSpan.FromSeconds(50));
        session.TryJoin(PeerKey);

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
    public void BothAcceptingStopsTheClock()
    {
        // Pinning follows immediately, but it is I/O. Both humans already said yes inside the
        // window; the deadline passing during the write must not turn that into "expired".
        var clock = new FakeTimeProvider();
        var session = Joined(clock);
        session.Decide(accepted: true);
        session.RecordPeerDecision(accepted: true);

        clock.Advance(PairingSession.ConfirmTimeout + TimeSpan.FromSeconds(5));

        Assert.True(session.BothAccepted);
        session.MarkPinned();
        Assert.Equal(PairingState.Paired, session.State);
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
