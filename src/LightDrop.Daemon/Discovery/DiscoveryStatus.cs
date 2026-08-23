namespace LightDrop.Daemon.Discovery;

/// <summary>
/// Whether discovery is browsing, and since when.
/// </summary>
/// <remarks>
/// A concrete class with one implementation and no test double, per "interfaces are for ports,
/// not habit". It exists because an empty peer list cannot be interpreted without it: the
/// registry can say what was heard, but only the transport's start outcome says whether anything
/// could have been heard at all.
/// <para>
/// Written once by <see cref="PeerDiscoveryService"/> during startup and read by the peers
/// endpoint on request. Reads and writes are on different threads, so the fields are volatile
/// rather than lock-guarded — they are two independent values that are never compared against
/// each other, and a reader that sees a stale timestamp for a moment reports a slightly early
/// start time rather than anything incorrect.
/// </para>
/// </remarks>
internal sealed class DiscoveryStatus(TimeProvider timeProvider)
{
    private volatile bool _running;

    private long _startedAtTicks;

    public bool Running => _running;

    public DateTimeOffset? StartedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _startedAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Records that the transport started browsing.</summary>
    public void MarkRunning()
    {
        Interlocked.Exchange(ref _startedAtTicks, timeProvider.GetUtcNow().UtcTicks);
        _running = true;
    }

    /// <summary>
    /// Records that the transport could not start — a blocked firewall, or a denied macOS Local
    /// Network permission.
    /// </summary>
    public void MarkStopped()
    {
        _running = false;
        Interlocked.Exchange(ref _startedAtTicks, 0);
    }
}
