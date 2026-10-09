namespace LightDrop.Daemon.Tests.TestSupport;

/// <summary>
/// A clock the test moves by hand, for crossing a pairing deadline without waiting a minute.
/// </summary>
/// <remarks>
/// Read from request threads while the test advances it, hence the lock -- unlike the Core fake,
/// which only ever sees one thread.
/// </remarks>
internal sealed class ManualClock : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan amount)
    {
        lock (_gate)
        {
            _now += amount;
        }
    }
}
