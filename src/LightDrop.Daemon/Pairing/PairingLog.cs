using LightDrop.Core.Pairing;
using Microsoft.Extensions.Logging;

namespace LightDrop.Daemon.Pairing;

/// <summary>
/// Source-generated log messages for pairing and the peer listener.
/// </summary>
/// <remarks>
/// Every way a session ends is logged on both machines, so a user comparing two daemons' output
/// after a failed pairing can see which side said no, or which side timed out. The code itself is
/// logged too: it is derived from two public keys, so it is no secret, and seeing the digits each
/// side computed is the quickest way to spot a man-in-the-middle after the fact.
/// </remarks>
internal static partial class PairingLog
{
    [LoggerMessage(
        EventId = 300,
        Level = LogLevel.Information,
        Message = "Pairing window opened for {PeerName} [{PeerDeviceId}]; this device is the {Role}.")]
    public static partial void Started(ILogger logger, string peerName, string peerDeviceId, PairingRole role);

    [LoggerMessage(
        EventId = 301,
        Level = LogLevel.Information,
        Message = "{PeerName} [{PeerDeviceId}] joined the pairing session.")]
    public static partial void Joined(ILogger logger, string peerName, string peerDeviceId);

    [LoggerMessage(
        EventId = 302,
        Level = LogLevel.Information,
        Message = "Pairing code for {PeerName} is {Code}; waiting for both sides to confirm.")]
    public static partial void CodeShown(ILogger logger, string peerName, string code);

    [LoggerMessage(
        EventId = 303,
        Level = LogLevel.Information,
        Message = "Pairing with {PeerName}: the code was accepted on this device.")]
    public static partial void AcceptedHere(ILogger logger, string peerName);

    [LoggerMessage(
        EventId = 304,
        Level = LogLevel.Warning,
        Message = "Pairing with {PeerName} rejected: {Reason} Nothing was saved.")]
    public static partial void Rejected(ILogger logger, string peerName, string? reason);

    [LoggerMessage(
        EventId = 305,
        Level = LogLevel.Information,
        Message = "Pairing with {PeerName} expired: {Reason} Nothing was saved.")]
    public static partial void Expired(ILogger logger, string peerName, string? reason);

    [LoggerMessage(
        EventId = 306,
        Level = LogLevel.Information,
        Message = "Paired with {PeerName} [{PeerDeviceId}]; its key is pinned.")]
    public static partial void Pinned(ILogger logger, string peerName, string peerDeviceId);

    [LoggerMessage(
        EventId = 307,
        Level = LogLevel.Information,
        Message = "Pairing with {PeerName} cancelled. Nothing was saved.")]
    public static partial void Cancelled(ILogger logger, string peerName);

    [LoggerMessage(
        EventId = 308,
        Level = LogLevel.Warning,
        Message = "Pairing with {PeerName} failed: {Reason}")]
    public static partial void Failed(ILogger logger, string peerName, string? reason);

    [LoggerMessage(
        EventId = 309,
        Level = LogLevel.Debug,
        Message = "Could not reach {PeerName} at {Address} yet; retrying.")]
    public static partial void DialRetry(ILogger logger, string peerName, string address, Exception? exception);

    [LoggerMessage(
        EventId = 310,
        Level = LogLevel.Information,
        Message = "Peer listener accepting mutual-TLS connections on {Endpoint}.")]
    public static partial void ListenerStarted(ILogger logger, string endpoint);

    [LoggerMessage(
        EventId = 311,
        Level = LogLevel.Error,
        Message = "Peer listener could not start on {Endpoint}. Paired devices cannot reach this one until it does.")]
    public static partial void ListenerFailed(ILogger logger, string endpoint, Exception exception);

    [LoggerMessage(
        EventId = 312,
        Level = LogLevel.Information,
        Message = "Unpaired {PeerName} [{PeerDeviceId}]. It keeps its own pairing until it unpairs too.")]
    public static partial void Unpinned(ILogger logger, string peerName, string peerDeviceId);
}
