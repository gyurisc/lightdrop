using Microsoft.Extensions.Logging;

namespace LightDrop.Daemon.Infrastructure;

/// <summary>
/// Source-generated log messages for the storage adapters.
/// </summary>
internal static partial class StorageLog
{
    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Warning,
        Message = "Config file {ConfigPath} could not be read and was ignored; using defaults.")]
    public static partial void ConfigUnreadable(ILogger logger, string configPath, Exception exception);
}
