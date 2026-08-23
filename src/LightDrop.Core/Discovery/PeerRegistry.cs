using LightDrop.Core.Contracts;

namespace LightDrop.Core.Discovery;

/// <summary>
/// The nearby devices heard recently. In memory only, for the lifetime of the process.
/// </summary>
/// <remarks>
/// Deliberately has no dependency on <see cref="Configuration.IStateStore"/> or anything else
/// that persists. Discovered peers are strangers: nothing here may reach <c>state.json</c> or
/// <c>trustedPeers</c>. Pairing must cross that boundary explicitly and under review, rather than
/// inheriting a path that already exists.
/// <para>
/// A concrete class with no interface, matching <see cref="Health.HealthService"/>: it is
/// in-memory logic with no boundary to abstract, and tests exercise it directly.
/// </para>
/// </remarks>
public sealed class PeerRegistry(TimeProvider timeProvider, string localDeviceId)
{
    /// <summary>
    /// How long a peer survives without being heard from again.
    /// </summary>
    /// <remarks>
    /// Three times the announcement interval, so a single dropped packet does not make a peer
    /// flicker. Deliberately independent of the DNS record TTL: the standard 75-minute PTR TTL
    /// would leave a sleeping laptop listed as present for over an hour, and a device that sleeps
    /// or drops off Wi-Fi never sends a goodbye.
    /// </remarks>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Hard ceiling on tracked peers.
    /// </summary>
    /// <remarks>
    /// Expiry handles the steady state; this is the backstop against a flood of fabricated
    /// announcements arriving faster than they age out. Anyone on the link can invent unlimited
    /// identifiers, so an unbounded registry is a memory-exhaustion primitive.
    /// </remarks>
    public const int MaxPeers = 256;

    private readonly Dictionary<string, Entry> _peers = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// Records an announcement, or refreshes an existing peer.
    /// </summary>
    /// <returns><c>false</c> if the announcement was ignored.</returns>
    public bool Observe(PeerAnnouncement announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);

        // Multicast loops back, so a daemon hears itself. Two daemons on one machine also hear
        // each other, which is a supported way to test discovery.
        if (string.Equals(announcement.DeviceId, localDeviceId, StringComparison.Ordinal))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (!_peers.ContainsKey(announcement.DeviceId))
            {
                RemoveExpired(now);

                // Accepted limitation: the cap holds, but nothing rate-limits distinct fabricated
                // identifiers. An attacker sustaining more than MaxPeers unique announcements
                // faster than a real device re-announces can push that device out. Mitigating it
                // needs per-source throttling, which needs the sender address the transport does
                // not currently surface. Presence is not trust, so the cost is a missing row in a
                // list, not a security decision.
                if (_peers.Count >= MaxPeers && !EvictLeastRecentlySeen())
                {
                    return false;
                }
            }

            // Freshest wins, always. A newer announcement overwrites cached data outright rather
            // than waiting for a TTL — that is what makes an unsolicited re-announcement take
            // effect promptly.
            _peers[announcement.DeviceId] = new Entry(announcement, now);
            return true;
        }
    }

    /// <summary>
    /// Drops a peer immediately, for a goodbye announcement.
    /// </summary>
    public bool Forget(string deviceId)
    {
        lock (_gate)
        {
            return _peers.Remove(deviceId);
        }
    }

    /// <summary>
    /// The peers heard from recently, most recently seen first.
    /// </summary>
    /// <remarks>
    /// Expiry is applied here rather than by a background timer. Nothing needs peers evicted
    /// between reads, and a timer would be one more thing to schedule, stop and test.
    /// </remarks>
    public IReadOnlyList<DiscoveredPeer> GetPeers()
    {
        var now = timeProvider.GetUtcNow();

        lock (_gate)
        {
            RemoveExpired(now);

            return [.. _peers.Values
                .OrderByDescending(entry => entry.LastSeen)
                .Select(entry => entry.ToDiscoveredPeer())];
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        // Materialised before removing, since the dictionary cannot be mutated while enumerated.
        var expired = _peers
            .Where(pair => now - pair.Value.LastSeen >= StaleAfter)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var deviceId in expired)
        {
            _peers.Remove(deviceId);
        }
    }

    private bool EvictLeastRecentlySeen()
    {
        if (_peers.Count == 0)
        {
            return false;
        }

        var oldest = _peers.MinBy(pair => pair.Value.LastSeen).Key;
        return _peers.Remove(oldest);
    }

    private readonly record struct Entry(PeerAnnouncement Announcement, DateTimeOffset LastSeen)
    {
        public DiscoveredPeer ToDiscoveredPeer() => new()
        {
            DeviceId = Announcement.DeviceId,
            DeviceName = Announcement.DeviceName,
            Platform = Announcement.Platform,
            ProtocolVersion = Announcement.ProtocolVersion,
            Capabilities = Announcement.Capabilities,
            Port = Announcement.Port,
            LastSeen = LastSeen,
                    Address = Announcement.Address,
        };
    }
}
