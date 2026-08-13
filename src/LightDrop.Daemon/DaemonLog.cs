using Microsoft.Extensions.Logging;

namespace LightDrop.Daemon;

/// <summary>
/// Source-generated log messages for the daemon lifetime.
/// </summary>
/// <remarks>
/// <c>[LoggerMessage]</c> rather than <c>logger.LogInformation(...)</c>: the generated code is
/// allocation-free on the hot path, emits named fields for the JSON console formatter, and stays
/// trim- and AOT-safe.
/// </remarks>
internal static partial class DaemonLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "LightDrop {Version} (protocol {ProtocolVersion}) listening on {Endpoint} as {DeviceName} [{DeviceId}] on {Platform}.")]
    public static partial void Started(
        ILogger logger,
        string version,
        int protocolVersion,
        string endpoint,
        string deviceName,
        string deviceId,
        string platform);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "LightDrop daemon shutting down.")]
    public static partial void Stopping(ILogger logger);
}
