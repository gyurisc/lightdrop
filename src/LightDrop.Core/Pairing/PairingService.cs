using System.Security.Cryptography;
using LightDrop.Core.Configuration;

namespace LightDrop.Core.Pairing;

/// <summary>
/// The pin-or-reject rules: whether a peer is trusted, and how one becomes trusted.
/// </summary>
/// <remarks>
/// <strong>This is the boundary discovery is forbidden to cross.</strong> Nothing under
/// <c>Discovery</c> may reach <c>trustedPeers</c>; a discovered peer is a stranger until pairing
/// puts it here deliberately. That is why this lives in its own namespace with its own access to
/// <see cref="IStateStore"/>, rather than being folded into the registry.
/// <para>
/// A concrete class with no interface, matching <see cref="Devices.DeviceIdentityProvider"/>:
/// one implementation, no test double, get-or-create logic in Core with file I/O behind a port.
/// </para>
/// </remarks>
public sealed class PairingService(IStateStore stateStore, TimeProvider timeProvider)
{
    /// <summary>
    /// Whether this peer is paired <em>and</em> is presenting the key that was pinned.
    /// </summary>
    /// <remarks>
    /// Both halves matter. A matching device id proves nothing on its own — anyone on the link can
    /// put any id in an mDNS record — so the key is what the answer actually rests on. The
    /// comparison is constant-time: this runs against an attacker-supplied key, and a timing
    /// signal would leak how much of a pinned key a guess had right.
    /// </remarks>
    public async ValueTask<bool> IsTrustedAsync(
        string deviceId,
        ReadOnlyMemory<byte> publicKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (publicKey.IsEmpty)
        {
            return false;
        }

        var peer = await FindAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (peer is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromBase64String(peer.PublicKey),
            publicKey.Span);
    }

    /// <summary>
    /// Records a completed pairing.
    /// </summary>
    /// <remarks>
    /// Called only after both users have confirmed the same six digits. Everything before that
    /// point is an unauthenticated conversation with a stranger.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The peer is already paired. Silently replacing a pinned key is a downgrade path — an
    /// attacker who gets one pairing through would overwrite the real device's key — so
    /// replacement has to be an explicit <c>unpair</c> by the user.
    /// </exception>
    public async ValueTask PinAsync(
        string deviceId,
        string deviceName,
        ReadOnlyMemory<byte> publicKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);

        if (publicKey.IsEmpty)
        {
            // A peer with no key could never be verified again, so the entry would trust a device
            // id alone — exactly what pinning exists to prevent.
            throw new ArgumentException("A trusted peer must be pinned to a public key.", nameof(publicKey));
        }

        var state = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (state.TrustedPeers.Any(peer => Matches(peer, deviceId)))
        {
            throw new InvalidOperationException(
                $"'{deviceName}' is already paired with this device. Run `lightdrop unpair` first " +
                "if you meant to replace its key — pairing again would otherwise overwrite the key " +
                "silently.");
        }

        var pinned = new TrustedPeer
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            PublicKey = Convert.ToBase64String(publicKey.Span),
            PairedAt = timeProvider.GetUtcNow(),
        };

        await stateStore
            .SaveAsync(state with { TrustedPeers = [.. state.TrustedPeers, pinned] }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Forgets a peer. Returns whether anything was removed.
    /// </summary>
    /// <remarks>
    /// Local only: the peer keeps its own pin until it unpairs too. Making this symmetric would
    /// mean one side could revoke the other's trust over the network, which is a worse property
    /// than an asymmetry the docs can state plainly.
    /// </remarks>
    public async ValueTask<bool> UnpinAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var state = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        var remaining = state.TrustedPeers.Where(peer => !Matches(peer, deviceId)).ToArray();
        if (remaining.Length == state.TrustedPeers.Count)
        {
            // Unpairing a peer that was never paired is a no-op, not a failure — and writing here
            // would rewrite state.json for nothing.
            return false;
        }

        await stateStore
            .SaveAsync(state with { TrustedPeers = remaining }, cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// The peers this device has paired with, for <c>lightdrop peers --trusted</c>.
    /// </summary>
    public async ValueTask<IReadOnlyList<TrustedPeer>> ListAsync(CancellationToken cancellationToken = default)
    {
        var state = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return state.TrustedPeers;
    }

    private async ValueTask<TrustedPeer?> FindAsync(string deviceId, CancellationToken cancellationToken)
    {
        var state = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return state.TrustedPeers.FirstOrDefault(peer => Matches(peer, deviceId));
    }

    private static bool Matches(TrustedPeer peer, string deviceId)
        => string.Equals(peer.DeviceId, deviceId, StringComparison.Ordinal);
}
