using LightDrop.Core.Contracts;
using LightDrop.Core.Discovery;

namespace LightDrop.Core.Tests.Discovery;

/// <summary>
/// How an empty peer list should be read.
/// </summary>
/// <remarks>
/// This exists because "no peers" meant three different things and said only one of them. A
/// daemon that started two seconds ago has not finished looking; one that started five minutes
/// ago and still hears nothing is being blocked; one whose transport never started is not
/// looking at all. Advising someone to check their firewall in the first case sends them after a
/// problem they do not have.
/// </remarks>
public sealed class PeerSearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    // Defaults describe a healthy daemon that started moments ago. Tests that care about the
    // start time say so explicitly; a null one is a state that should never occur on the wire.
    private static PeerListResponse Response(
        bool running = true, DateTimeOffset? startedAt = null, IReadOnlyList<DiscoveredPeer>? peers = null) =>
        new()
        {
            DiscoveryRunning = running,
            DiscoveryStartedAt = startedAt ?? Now - TimeSpan.FromSeconds(5),
            Peers = peers ?? [],
        };

    private static DiscoveredPeer Peer() => new()
    {
        DeviceId = "peer-1",
        DeviceName = "Work Laptop",
        Platform = "windows",
        ProtocolVersion = 1,
        Capabilities = [],
        Port = 5533,
        Address = "192.168.0.149",
        LastSeen = Now,
    };

    [Fact]
    public void ReportsPeersWheneverAnyAreKnown() =>
        Assert.Equal(PeerSearchState.PeersFound, PeerSearch.Classify(Response(peers: [Peer()]), Now));

    [Fact]
    public void ReportsStoppedWhenDiscoveryIsNotRunning() =>
        Assert.Equal(PeerSearchState.Stopped, PeerSearch.Classify(Response(running: false), Now));

    [Fact]
    public void ReportsStoppedEvenIfPeersLingerFromBeforeItStopped() =>
        Assert.Equal(
            PeerSearchState.Stopped, PeerSearch.Classify(Response(running: false, peers: [Peer()]), Now));

    [Fact]
    public void ReportsSearchingWhileDiscoveryIsStillYoung() =>
        Assert.Equal(
            PeerSearchState.Searching,
            PeerSearch.Classify(Response(startedAt: Now - TimeSpan.FromSeconds(5)), Now));

    [Fact]
    public void ReportsSearchingRightUpToTheSettlingPoint() =>
        Assert.Equal(
            PeerSearchState.Searching,
            PeerSearch.Classify(Response(startedAt: Now - PeerSearch.SettlesAfter + TimeSpan.FromSeconds(1)), Now));

    [Fact]
    public void ReportsSilentOnceDiscoveryHasHadLongEnough() =>
        Assert.Equal(
            PeerSearchState.Silent,
            PeerSearch.Classify(Response(startedAt: Now - PeerSearch.SettlesAfter), Now));

    [Fact]
    public void TreatsAMissingStartTimeAsStopped()
    {
        // Running with no start time should not happen, but guessing "searching" would show a
        // reassuring message forever if it ever did.
        var contradictory = new PeerListResponse
        {
            DiscoveryRunning = true,
            DiscoveryStartedAt = null,
            Peers = [],
        };

        Assert.Equal(PeerSearchState.Stopped, PeerSearch.Classify(contradictory, Now));
    }
}
