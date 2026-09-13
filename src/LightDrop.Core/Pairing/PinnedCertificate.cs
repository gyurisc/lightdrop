using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LightDrop.Core.Pairing;

/// <summary>
/// Whether a certificate presented during a TLS handshake carries the key that was pinned.
/// </summary>
/// <remarks>
/// <strong>This is the entire trust decision.</strong> Both devices present self-signed
/// certificates that no authority vouches for, so nothing in the certificate itself — its subject,
/// its issuer, its validity dates — carries any weight. Anyone on the network can mint one saying
/// whatever they like. The only question is whether the public key inside matches the one recorded
/// when two people compared six digits.
/// <para>
/// That is also why the certificate can be reissued on every daemon start: pairing pins the key,
/// never the certificate wrapped around it.
/// </para>
/// </remarks>
public static class PinnedCertificate
{
    /// <summary>
    /// Whether <paramref name="presented"/> carries the pinned key.
    /// </summary>
    /// <remarks>
    /// Constant-time, because this runs against a certificate an attacker chose and a timing
    /// signal would leak how much of a pinned key a guess had right. An empty pin fails closed:
    /// a trusted peer with no key is a bug elsewhere, and treating it as "no constraint" would
    /// turn that bug into an open door.
    /// </remarks>
    public static bool Matches(X509Certificate2 presented, ReadOnlySpan<byte> pinnedPublicKeyInfo)
    {
        ArgumentNullException.ThrowIfNull(presented);

        if (pinnedPublicKeyInfo.IsEmpty)
        {
            return false;
        }

        byte[] presentedSpki;
        try
        {
            presentedSpki = presented.PublicKey.ExportSubjectPublicKeyInfo();
        }
        catch (CryptographicException)
        {
            // presented is attacker-controlled: it came off the wire during a TLS handshake, not
            // out of anything this device constructed. A TLS callback is the wrong place to let
            // an export failure escape -- it does not fail the handshake, it crashes it, hiding
            // the real cause. Not a match.
            //
            // Deliberately untested, not dead: no certificate that actually parses has been found
            // to throw here -- RSA, Ed25519, and explicit-curve EC were all constructed and all
            // exported cleanly. A SubjectPublicKeyInfo broken enough to fail export appears to also
            // fail certificate parsing itself, before any of this code runs, so the throwing input
            // this guards against cannot arrive through TLS. It stays as defence-in-depth against
            // that boundary being wrong, not as a branch this test suite can reach.
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(presentedSpki, pinnedPublicKeyInfo);
    }

    /// <summary>
    /// Whether <paramref name="presented"/> carries the pinned key, given the base64 form stored
    /// in <c>state.json</c>.
    /// </summary>
    /// <remarks>
    /// A malformed pin returns false rather than throwing. A corrupt state file should fail the
    /// handshake — which the user can act on — not tear down the connection with an exception from
    /// inside a TLS callback, where the real cause would be invisible. See
    /// <see cref="TryDecodePin"/> for why the decode itself never throws.
    /// </remarks>
    public static bool Matches(X509Certificate2 presented, string pinnedPublicKeyInfoBase64)
    {
        return TryDecodePin(pinnedPublicKeyInfoBase64, out var pinned) && Matches(presented, pinned);
    }

    /// <summary>
    /// Decodes a pin stored as base64 in <c>state.json</c>, failing closed rather than throwing.
    /// </summary>
    /// <remarks>
    /// Shared by every place in Core that compares against a stored pin -- this type's own
    /// <see cref="Matches(X509Certificate2, string)"/> and <see
    /// cref="PairingService.IsTrustedAsync"/> -- so a corrupt pin fails the operation it gates
    /// instead of throwing from inside a caller. That matters most here: one caller runs inside a
    /// TLS validation callback, where an escaping exception crashes the handshake rather than
    /// failing it, hiding the real cause.
    /// <para>
    /// The decode buffer is heap-allocated at the input's length rather than <c>stackalloc</c>'d,
    /// for the same reason as the caller above: nothing bounds how long a corrupt stored value
    /// could be, and a stack overflow is an unrecoverable crash, the opposite of failing closed.
    /// </para>
    /// </remarks>
    internal static bool TryDecodePin(string? base64, out byte[] decoded)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            decoded = [];
            return false;
        }

        var buffer = new byte[base64.Length];
        if (!Convert.TryFromBase64String(base64, buffer, out var written))
        {
            decoded = [];
            return false;
        }

        decoded = buffer.AsSpan(0, written).ToArray();
        return true;
    }
}
