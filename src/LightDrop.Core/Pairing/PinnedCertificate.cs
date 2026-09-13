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
            // out of anything this device constructed. A key algorithm .NET cannot model, or an
            // encoded key/parameters blob that does not round-trip, means the export throws here
            // instead of in ordinary use. A TLS callback is the wrong place to let that escape --
            // it does not fail the handshake, it crashes it, hiding the real cause. Not a match.
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
    /// inside a TLS callback, where the real cause would be invisible.
    /// <para>
    /// The decode buffer is heap-allocated rather than <c>stackalloc</c>'d at the input's length:
    /// the decoded length is always smaller than the encoded string, but nothing here bounds the
    /// encoded string itself. A corrupt <c>state.json</c> with an unexpectedly long value must fail
    /// the handshake, not risk a <see cref="StackOverflowException"/> — which is unrecoverable and
    /// would crash the daemon outright, the opposite of failing closed.
    /// </para>
    /// </remarks>
    public static bool Matches(X509Certificate2 presented, string pinnedPublicKeyInfoBase64)
    {
        if (string.IsNullOrWhiteSpace(pinnedPublicKeyInfoBase64))
        {
            return false;
        }

        var pinned = new byte[pinnedPublicKeyInfoBase64.Length];
        if (!Convert.TryFromBase64String(pinnedPublicKeyInfoBase64, pinned, out var written))
        {
            return false;
        }

        return Matches(presented, pinned.AsSpan(0, written));
    }
}
