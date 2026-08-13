namespace LightDrop.Core.Tests.Fakes;

/// <summary>
/// A clock the test drives.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taking Microsoft.Extensions.TimeProvider.Testing: the registry needs
/// only <see cref="GetUtcNow"/>, and that package's real value is its fake timer queue, which
/// nothing here uses. Same size and spirit as the in-memory store fakes.
/// </remarks>
internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public FakeTimeProvider()
        : this(DateTimeOffset.UnixEpoch)
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now = _now.Add(amount);
}
