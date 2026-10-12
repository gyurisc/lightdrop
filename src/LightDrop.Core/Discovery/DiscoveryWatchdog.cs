namespace LightDrop.Core.Discovery;

/// <summary>
/// Decides when discovery has gone deaf and its sockets should be rebuilt.
/// </summary>
/// <remarks>
/// The signal is the daemon's own announcement: multicast loops back, so a working transport hears
/// itself within milliseconds of every announcement. Silence means its sockets are dead — opened
/// before macOS granted the Local Network permission (seen 2026-10-12 on a launchd-started daemon,
/// deaf for 12 minutes until restarted), or bound to an address the machine no longer has after a
/// network change the mDNS library does not watch for on macOS. Peers are no signal: a daemon alone
/// on the network hears none, and that is not a fault.
/// <para>
/// Pure logic over a <see cref="TimeProvider"/>, so the timing is tested without a network. The
/// transport calls <see cref="Check"/> once per refresh, before announcing.
/// </para>
/// </remarks>
public sealed class DiscoveryWatchdog
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _deafAfter;
    private readonly Lock _gate = new();
    private DateTimeOffset _lastHeard;
    private bool _deaf;

    /// <param name="timeProvider">The clock.</param>
    /// <param name="deafAfter">
    /// How long without hearing itself before discovery counts as deaf. Must exceed the announcement
    /// interval, or a healthy transport is rebuilt between two announcements.
    /// </param>
    public DiscoveryWatchdog(TimeProvider timeProvider, TimeSpan deafAfter)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deafAfter, TimeSpan.Zero);

        _timeProvider = timeProvider;
        _deafAfter = deafAfter;

        // A fresh transport gets a full window before it can be judged.
        _lastHeard = timeProvider.GetUtcNow();
    }

    /// <summary>Records that the transport heard its own announcement.</summary>
    /// <returns><c>true</c> if it had been deaf: the first sign a rebuild worked.</returns>
    public bool HeardSelf()
    {
        lock (_gate)
        {
            _lastHeard = _timeProvider.GetUtcNow();
            var recovered = _deaf;
            _deaf = false;
            return recovered;
        }
    }

    /// <summary>Whether the transport should be rebuilt now.</summary>
    /// <remarks>
    /// Any verdict other than <see cref="DiscoveryHealth.Healthy"/> restarts the window, so a rebuilt
    /// transport gets as long as a fresh one to prove itself, and a machine with no network at all is
    /// rebuilt once per window rather than on every check.
    /// </remarks>
    public DiscoveryHealth Check()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (now - _lastHeard < _deafAfter)
            {
                return DiscoveryHealth.Healthy;
            }

            _lastHeard = now;
            var first = !_deaf;
            _deaf = true;
            return first ? DiscoveryHealth.WentDeaf : DiscoveryHealth.StillDeaf;
        }
    }
}

/// <summary>The watchdog's verdict.</summary>
public enum DiscoveryHealth
{
    /// <summary>Heard itself recently; leave it alone.</summary>
    Healthy,

    /// <summary>Just went quiet: rebuild, and say so once.</summary>
    WentDeaf,

    /// <summary>Still quiet after a rebuild — no network, most likely. Rebuild again, quietly.</summary>
    StillDeaf,
}
