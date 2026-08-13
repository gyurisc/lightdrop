using Microsoft.Extensions.Logging;

namespace LightDrop.Daemon.Tests.TestSupport;

/// <summary>
/// Records log entries so a test can assert that something was reported without asserting on
/// message text, which would be brittle.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<LogLevel> _levels = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<LogLevel> Levels
    {
        get
        {
            lock (_gate)
            {
                return [.. _levels];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _levels.Add(logLevel);
        }
    }
}
