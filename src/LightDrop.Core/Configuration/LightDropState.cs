namespace LightDrop.Core.Configuration;

/// <summary>
/// Application-owned state, persisted to <c>state.json</c>.
/// </summary>
/// <remarks>
/// LightDrop writes this file; users are not expected to edit it. Keeping it separate from
/// <see cref="LightDropConfig"/> means pairing can append a peer without rewriting — and
/// potentially destroying — hand-edited user settings.
/// </remarks>
public sealed record LightDropState
{
    /// <summary>
    /// This device's stable identifier, generated on first run. Null only before first run.
    /// </summary>
    public string? DeviceId { get; init; }

    /// <summary>
    /// This device's private key, PKCS#8 DER, base64-encoded. Null only before first run.
    /// </summary>
    /// <remarks>
    /// The one genuinely secret value LightDrop stores, and the reason <c>state.json</c> is
    /// created <c>0600</c> on Unix. Only the key is kept — the certificate presenting it is
    /// reissued on every start, because pairing pins the public key rather than the certificate.
    /// </remarks>
    public string? DeviceKey { get; init; }

    /// <summary>
    /// Peers this device has completed pairing with.
    /// </summary>
    public IReadOnlyList<TrustedPeer> TrustedPeers { get; init; } = [];
}

/// <summary>
/// A peer this device has paired with.
/// </summary>
/// <remarks>
/// The device id says who a peer claims to be; <see cref="PublicKey"/> is what proves it. The two
/// are stored separately on purpose, so a future key rotation does not destroy the human-facing
/// identity, and so a spoofed id in an mDNS record buys an attacker nothing.
/// </remarks>
public sealed record TrustedPeer
{
    public required string DeviceId { get; init; }

    public required string DeviceName { get; init; }

    /// <summary>
    /// The peer's DER SubjectPublicKeyInfo, base64-encoded, pinned at pairing.
    /// </summary>
    /// <remarks>
    /// Not a secret — a public key crosses the network in the clear during every TLS handshake.
    /// The full key is kept rather than a fingerprint because the pairing code is derived from
    /// the key bytes: storing only a hash would make an existing pairing impossible to re-verify
    /// without unpairing first.
    /// </remarks>
    public required string PublicKey { get; init; }

    public required DateTimeOffset PairedAt { get; init; }
}
