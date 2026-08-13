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
    /// Peers this device has completed pairing with.
    /// </summary>
    public IReadOnlyList<TrustedPeer> TrustedPeers { get; init; } = [];
}

/// <summary>
/// A peer this device has paired with.
/// </summary>
/// <remarks>
/// Placeholder shape. This will gain key material once the pairing handshake is designed —
/// pairing is the one part of v1 that cannot be retrofitted, so it gets its own design pass
/// before implementation.
/// </remarks>
public sealed record TrustedPeer
{
    public required string DeviceId { get; init; }

    public required string DeviceName { get; init; }

    public required DateTimeOffset PairedAt { get; init; }
}
