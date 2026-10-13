namespace LightDrop.Core.Transfers;

/// <summary>
/// The speed of a transfer and the time it has left, from samples of how much has gone.
/// </summary>
/// <remarks>
/// Measured over a sliding window rather than since the start, so the figure follows a Wi-Fi link
/// that slows down or recovers, and the seconds spent dialing the peer do not drag it down for the
/// rest of the transfer. Pure arithmetic over times the caller supplies, so it is tested without a
/// network or a real wait.
/// </remarks>
public sealed class TransferRate
{
    /// <summary>
    /// The shortest span an estimate is drawn from. Over less, the figure mostly measures how the
    /// network's buffers happened to fill, and the first number shown would be wildly off.
    /// </summary>
    private static readonly TimeSpan ShortestSpan = TimeSpan.FromSeconds(1);

    /// <summary>Beyond this an estimate says nothing useful, and far beyond it would overflow.</summary>
    private static readonly TimeSpan LongestTimeLeft = TimeSpan.FromDays(30);

    private readonly TimeSpan _window;
    private readonly List<(TimeSpan At, long Bytes)> _samples = [];

    /// <param name="window">How far back the speed is measured.</param>
    public TransferRate(TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _window = window;
    }

    /// <summary>Bytes per second over the window, or <c>null</c> until there is enough to say.</summary>
    public double? BytesPerSecond
    {
        get
        {
            if (_samples.Count < 2)
            {
                return null;
            }

            var (firstAt, firstBytes) = _samples[0];
            var (lastAt, lastBytes) = _samples[^1];
            var span = lastAt - firstAt;

            return span < ShortestSpan ? null : (lastBytes - firstBytes) / span.TotalSeconds;
        }
    }

    /// <summary>Records that <paramref name="bytes"/> had gone by <paramref name="at"/>.</summary>
    /// <remarks>
    /// A sample that does not move time forward, or claims fewer bytes than the last, is ignored:
    /// neither can happen in a real transfer, and either would poison the figure.
    /// </remarks>
    public void Observe(TimeSpan at, long bytes)
    {
        if (_samples.Count > 0 && (at <= _samples[^1].At || bytes < _samples[^1].Bytes))
        {
            return;
        }

        _samples.Add((at, bytes));

        // Keep the newest sample at least a window old, so the span covers the whole window once
        // the transfer has run that long; drop everything before it.
        while (_samples.Count > 2 && at - _samples[1].At >= _window)
        {
            _samples.RemoveAt(0);
        }
    }

    /// <summary>
    /// How long the rest of <paramref name="length"/> will take at the current speed: zero once it
    /// has all gone, <c>null</c> while the speed is unknown or nothing is moving.
    /// </summary>
    public TimeSpan? TimeLeft(long length)
    {
        if (_samples.Count > 0 && _samples[^1].Bytes >= length)
        {
            return TimeSpan.Zero;
        }

        if (BytesPerSecond is not { } rate || rate <= 0)
        {
            return null;
        }

        var seconds = (length - _samples[^1].Bytes) / rate;
        return seconds > LongestTimeLeft.TotalSeconds ? null : TimeSpan.FromSeconds(seconds);
    }
}
