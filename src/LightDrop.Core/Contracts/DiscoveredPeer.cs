namespace LightDrop.Core.Contracts;

/// <summary>
/// A nearby device LightDrop has heard announce itself. The response shape of
/// <c>GET /api/peers</c>.
/// </summary>
/// <remarks>
/// <strong>A discovered peer is a stranger, not a trusted device.</strong> Everything here is
/// unverified data broadcast by whoever sent it — anyone on the local network can claim any of
/// it. Presence is not trust. Nothing derived from this type may be persisted or used to
/// authorize anything; that arrives with pairing.
/// </remarks>
public sealed record DiscoveredPeer
{
    public required string DeviceId { get; init; }

    public required string DeviceName { get; init; }

    public required string Platform { get; init; }

    public required int ProtocolVersion { get; init; }

    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>
    /// The port from the peer's SRV record. Informational only — <strong>not</strong> an
    /// authorization boundary, and not usable for commands: LightDrop binds HTTP to loopback and
    /// accepts no inbound commands from peers.
    /// </summary>
    public required int Port { get; init; }

    /// <summary>When this peer was last heard from.</summary>
    public required DateTimeOffset LastSeen { get; init; }
}
