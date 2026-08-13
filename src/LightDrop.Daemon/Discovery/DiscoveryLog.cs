using Microsoft.Extensions.Logging;

namespace LightDrop.Daemon.Discovery;

internal static partial class DiscoveryLog
{
    [LoggerMessage(
        EventId = 200,
        Level = LogLevel.Information,
        Message = "Advertising {ServiceName} on port {Port} and browsing for peers.")]
    public static partial void Started(ILogger logger, string serviceName, int port);

    [LoggerMessage(EventId = 201, Level = LogLevel.Information, Message = "Peer discovery stopped.")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(
        EventId = 202,
        Level = LogLevel.Debug,
        Message = "Ignored a malformed or unreadable peer announcement.")]
    public static partial void AnnouncementIgnored(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 203, Level = LogLevel.Debug, Message = "A peer query failed on one interface.")]
    public static partial void QueryFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 204,
        Level = LogLevel.Warning,
        Message = "Could not announce goodbye; peers will age this device out instead.")]
    public static partial void GoodbyeFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 205, Level = LogLevel.Warning, Message = "Multicast service failed to stop cleanly.")]
    public static partial void StopFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 206,
        Level = LogLevel.Warning,
        Message = "Peer discovery could not start; the daemon continues without it. On macOS check System Settings > Privacy & Security > Local Network; on Windows check the Defender Firewall prompt.")]
    public static partial void StartFailed(ILogger logger, Exception exception);
}
