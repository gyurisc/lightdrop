using LightDrop.Core.Pairing;

namespace LightDrop.Core.Contracts;

// ---- Loopback API: what the CLI sends the local daemon. ----

/// <summary>Body of <c>POST /api/pairing</c>: who to pair with, as the user typed it.</summary>
public sealed record StartPairingRequest
{
    /// <summary>A discovered device's name or device-id prefix.</summary>
    public required string Peer { get; init; }
}

/// <summary>
/// One pairing session as the CLI sees it: <c>GET /api/pairing</c>, and the answer to every other
/// pairing call.
/// </summary>
public sealed record PairingStatus
{
    public required PairingState State { get; init; }

    public required PairingRole Role { get; init; }

    public required string PeerDeviceId { get; init; }

    /// <summary>The peer's name as discovery sanitized it on ingestion.</summary>
    public required string PeerName { get; init; }

    /// <summary>The six digits to compare. Present once the peer has joined.</summary>
    public string? Code { get; init; }

    /// <summary>Why the session ended, in words for a human, when it ended badly.</summary>
    public string? Reason { get; init; }

    /// <summary>When the current step times out.</summary>
    public required DateTimeOffset Deadline { get; init; }
}

/// <summary>
/// A yes or no: the local user's answer on loopback, and each side's answer in
/// <c>POST /pair/confirm</c> on the LAN.
/// </summary>
public sealed record PairingDecision
{
    public required bool Accepted { get; init; }
}

/// <summary>A paired peer as <c>GET /api/trusted</c> lists it.</summary>
/// <remarks>
/// Deliberately not <see cref="Configuration.TrustedPeer"/>: the pinned key stays inside the
/// daemon. Nothing the CLI does needs it, and a key that never leaves cannot be mishandled.
/// </remarks>
public sealed record TrustedPeerSummary
{
    public required string DeviceId { get; init; }

    public required string DeviceName { get; init; }

    public required DateTimeOffset PairedAt { get; init; }
}

/// <summary>Response of <c>GET /api/trusted</c>.</summary>
public sealed record TrustedPeerListResponse
{
    public required IReadOnlyList<TrustedPeerSummary> Peers { get; init; }
}

/// <summary>An error the CLI prints as-is.</summary>
public sealed record ApiError
{
    public required string Error { get; init; }
}

// ---- Peer protocol: what two daemons send each other on the peer listener. ----

/// <summary>Body of <c>POST /pair/hello</c>, sent by the dialing side.</summary>
/// <remarks>
/// Carries no name and no key. The key travels in the TLS handshake, which is the only carrier
/// that proves possession of it; the name comes from discovery, which already sanitized it. A
/// field here would be one more attacker-controlled string to get wrong.
/// </remarks>
public sealed record PairHelloRequest
{
    public required string DeviceId { get; init; }

    public required int ProtocolVersion { get; init; }
}

/// <summary>
/// Response to <c>POST /pair/hello</c>: who actually answered, and its commitment to the nonce it
/// will reveal once the dialer has revealed its own.
/// </summary>
public sealed record PairHelloResponse
{
    public required string DeviceId { get; init; }

    /// <summary><see cref="Pairing.PairingCode.Commit"/>; 32 bytes, base64 on the wire.</summary>
    public required byte[] Commitment { get; init; }
}

/// <summary>
/// One side's nonce: the body of <c>POST /pair/reveal</c> from the dialer, and the listener's
/// answer to it.
/// </summary>
public sealed record PairingNonce
{
    /// <summary><see cref="Pairing.PairingCode.NonceLength"/> bytes, base64 on the wire.</summary>
    public required byte[] Nonce { get; init; }
}
