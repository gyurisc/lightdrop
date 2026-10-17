using System.Text.Json.Serialization;

namespace LightDrop.Core.Contracts;

/// <summary>
/// The response body of <c>GET /health</c>.
/// </summary>
/// <remarks>
/// Lives in Core because it crosses a network boundary: the daemon produces it and the CLI
/// consumes it. A shared type is what stops the two from drifting.
/// <para>
/// Health stays a plain HTTP GET rather than a command, because it is the bootstrap probe —
/// it has to answer before any socket, pairing or capability negotiation exists, and it keeps
/// the daemon debuggable with nothing more than <c>curl</c>.
/// </para>
/// </remarks>
public sealed record HealthResponse
{
    /// <summary>The application version, e.g. <c>0.1.0</c>.</summary>
    public required string Version { get; init; }

    /// <summary>The wire protocol version. Peers negotiate compatibility on this, not on <see cref="Version"/>.</summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>The device's stable identifier.</summary>
    public required string DeviceId { get; init; }

    /// <summary>The device's human-readable name, which is how the CLI addresses peers.</summary>
    public required string DeviceName { get; init; }

    /// <summary>One of the tokens on <see cref="Devices.DevicePlatform"/>.</summary>
    public required string Platform { get; init; }

    /// <summary>
    /// The commands this device accepts. Lets the protocol grow additively: a peer learns what
    /// clipboard, image or notification support is available without a protocol version bump.
    /// Derived from the registered command handlers; see <see cref="Commands.CommandCapabilities"/>.
    /// </summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>
    /// Whether paired devices can reach this one. Filled in by the daemon, which owns the peer
    /// listener; <c>null</c> from a daemon older than this field, so a newer CLI still reads it.
    /// </summary>
    public PeerListenerHealth? PeerListener { get; init; }
}

/// <summary>The peer listener as <c>GET /health</c> reports it.</summary>
/// <remarks>
/// Exists because a listener that cannot bind its port is not fatal -- the daemon stays useful for
/// sending -- so without this the only sign was one line in the daemon's log, and a sender's
/// "may be offline" on the other device.
/// </remarks>
public sealed record PeerListenerHealth
{
    public required PeerListenerState State { get; init; }

    /// <summary>The port it listens on, or would.</summary>
    public required int Port { get; init; }

    /// <summary>Why it could not start. Only with <see cref="PeerListenerState.Failed"/>.</summary>
    public string? Error { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<PeerListenerState>))]
public enum PeerListenerState
{
    /// <summary>
    /// Not started, because nothing has needed it: no peer was pinned when the daemon started and
    /// no pairing window has opened since. A fresh install has no LAN socket at all.
    /// </summary>
    Off,

    /// <summary>Accepting connections from paired devices.</summary>
    Listening,

    /// <summary>
    /// Needed but not running: the port could not be bound -- another process holds it, or on
    /// Windows it falls in a Hyper-V excluded range. Paired devices cannot send to this one. A
    /// restart, or the next pairing window, tries again.
    /// </summary>
    Failed,
}
