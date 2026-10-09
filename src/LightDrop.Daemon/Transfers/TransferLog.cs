using Microsoft.Extensions.Logging;

namespace LightDrop.Daemon.Transfers;

/// <summary>
/// Source-generated log messages for file transfer, both directions.
/// </summary>
/// <remarks>
/// One line per file on each machine, naming the file, its size and the other device, so the two
/// daemons' output can be read side by side. The received name is the sanitized one; the
/// receiver's folder is logged locally, where it is this user's own information, and never sent
/// back to the sender.
/// </remarks>
internal static partial class TransferLog
{
    [LoggerMessage(
        EventId = 400,
        Level = LogLevel.Information,
        Message = "Received {FileName} ({Bytes} bytes) from {PeerName} [{PeerDeviceId}] into {Folder}.")]
    public static partial void Received(ILogger logger, string fileName, long bytes, string peerName, string peerDeviceId, string folder);

    [LoggerMessage(
        EventId = 401,
        Level = LogLevel.Warning,
        Message = "Receiving {FileName} from {PeerName} [{PeerDeviceId}] failed; nothing was saved.")]
    public static partial void ReceiveFailed(ILogger logger, string fileName, string peerName, string peerDeviceId, Exception exception);

    [LoggerMessage(
        EventId = 402,
        Level = LogLevel.Information,
        Message = "Sent {Path} ({Bytes} bytes) to {PeerName} [{PeerDeviceId}]; saved there as {SavedAs}.")]
    public static partial void Sent(ILogger logger, string path, long bytes, string peerName, string peerDeviceId, string savedAs);

    [LoggerMessage(
        EventId = 403,
        Level = LogLevel.Warning,
        Message = "Sending {Path} to {PeerName} [{PeerDeviceId}] failed: {Reason}")]
    public static partial void SendFailed(ILogger logger, string path, string peerName, string peerDeviceId, string reason, Exception? exception);
}
