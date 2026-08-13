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
    /// Empty while the daemon implements no commands.
    /// </summary>
    public required IReadOnlyList<string> Capabilities { get; init; }
}
