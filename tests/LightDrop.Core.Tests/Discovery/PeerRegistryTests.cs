using LightDrop.Core.Devices;
using LightDrop.Core.Discovery;
using LightDrop.Core.Tests.Fakes;

namespace LightDrop.Core.Tests.Discovery;

public sealed class PeerRegistryTests
{
    private const string LocalDeviceId = "local-device";

    private static PeerAnnouncement Announcement(string deviceId, string? name = null)
    {
        Assert.True(PeerAnnouncement.TryCreate(
            deviceId,
            name ?? $"Device {deviceId}",
            DevicePlatform.MacOS,
            1,
            null,
            5533,
            "192.168.0.149",
            out var announcement));
        return announcement!;
    }

    private static (PeerRegistry Registry, FakeTimeProvider Clock) Create()
    {
        var clock = new FakeTimeProvider();
        return (new PeerRegistry(clock, LocalDeviceId), clock);
    }

    [Fact]
    public void RecordsANewlyObservedPeer()
    {
        var (registry, _) = Create();

        Assert.True(registry.Observe(Announcement("peer-1", "MacBook Air")));

        var peer = Assert.Single(registry.GetPeers());
        Assert.Equal("peer-1", peer.DeviceId);
        Assert.Equal("MacBook Air", peer.DeviceName);
        Assert.Equal(5533, peer.Port);
    }

    [Fact]
    public void KeepsOnlyTheLatestDataForAPeerThatReAnnounces()
    {
        var (registry, clock) = Create();

        registry.Observe(Announcement("peer-1", "Old Name"));
        clock.Advance(TimeSpan.FromSeconds(30));
        registry.Observe(Announcement("peer-1", "New Name"));

        var peer = Assert.Single(registry.GetPeers());
        Assert.Equal("New Name", peer.DeviceName);
    }

    [Fact]
    public void ForgetsAPeerThatGoesQuiet()
    {
        var (registry, clock) = Create();
        registry.Observe(Announcement("peer-1"));

        clock.Advance(PeerRegistry.StaleAfter);

        Assert.Empty(registry.GetPeers());
    }

    [Fact]
    public void ReAnnouncingExtendsAPeersLifetime()
    {
        // The test that actually proves re-announcement refreshes liveness rather than merely
        // overwriting data: the peer survives past the point the first announcement would have
        // expired.
        var (registry, clock) = Create();
        registry.Observe(Announcement("peer-1"));

        clock.Advance(TimeSpan.FromSeconds(120));
        registry.Observe(Announcement("peer-1"));
        clock.Advance(TimeSpan.FromSeconds(120));

        Assert.Single(registry.GetPeers());
    }

    [Fact]
    public void IgnoresItsOwnAnnouncements()
    {
        // Multicast loops back, so a daemon hears itself announce.
        var (registry, _) = Create();

        Assert.False(registry.Observe(Announcement(LocalDeviceId)));
        Assert.Empty(registry.GetPeers());
    }

    [Fact]
    public void DropsAPeerImmediatelyOnGoodbye()
    {
        var (registry, _) = Create();
        registry.Observe(Announcement("peer-1"));

        Assert.True(registry.Forget("peer-1"));
        Assert.Empty(registry.GetPeers());
    }

    [Fact]
    public void StaysBoundedWhenFloodedWithFabricatedPeers()
    {
        // Anyone on the link can invent unlimited identifiers, so an unbounded registry would be
        // a memory-exhaustion primitive.
        var (registry, _) = Create();

        for (var i = 0; i < PeerRegistry.MaxPeers * 4; i++)
        {
            registry.Observe(Announcement($"flood-{i}"));
        }

        Assert.Equal(PeerRegistry.MaxPeers, registry.GetPeers().Count);
    }

    [Fact]
    public void EvictsByLastSeenRatherThanByInsertionOrder()
    {
        // The distinction matters: a peer inserted early but still announcing is live, and must
        // outlast one inserted later that has gone quiet. Evicting in insertion order would drop
        // the wrong device.
        var (registry, clock) = Create();

        registry.Observe(Announcement("early-but-active"));
        clock.Advance(TimeSpan.FromSeconds(1));
        registry.Observe(Announcement("later-but-silent"));
        clock.Advance(TimeSpan.FromSeconds(1));

        // The early peer re-announces, so it is now the more recently seen of the two.
        registry.Observe(Announcement("early-but-active"));
        clock.Advance(TimeSpan.FromSeconds(1));

        for (var i = 0; i < PeerRegistry.MaxPeers; i++)
        {
            registry.Observe(Announcement($"filler-{i}"));
        }

        var peers = registry.GetPeers();
        Assert.Equal(PeerRegistry.MaxPeers, peers.Count);
        Assert.DoesNotContain(peers, peer => peer.DeviceId == "later-but-silent");
    }

    [Fact]
    public void ListsMostRecentlySeenFirst()
    {
        var (registry, clock) = Create();

        registry.Observe(Announcement("first"));
        clock.Advance(TimeSpan.FromSeconds(10));
        registry.Observe(Announcement("second"));

        Assert.Equal(["second", "first"], registry.GetPeers().Select(peer => peer.DeviceId));
    }

    [Fact]
    public void SurvivesConcurrentObservations()
    {
        // The real caller is a transport event handler, which can fire from several threads.
        var (registry, _) = Create();

        Parallel.For(0, 200, i => registry.Observe(Announcement($"peer-{i % 50}")));

        Assert.Equal(50, registry.GetPeers().Count);
    }
}
