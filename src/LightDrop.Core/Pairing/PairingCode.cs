using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace LightDrop.Core.Pairing;

/// <summary>
/// The short human-verifiable code two users compare before trusting each other.
/// </summary>
/// <remarks>
/// <strong>The code is never transmitted.</strong> Each side derives it independently from both
/// public keys, so a man-in-the-middle — presenting a different key toward each side — produces
/// different digits on the two screens. This is what Bluetooth numeric comparison and Signal
/// safety numbers do, and it is the entire reason pairing resists an active attacker on the LAN.
/// <para>
/// The digits are stable for a given pair of devices rather than per session: it is SSH-style key
/// verification, so re-pairing shows the same code. TLS supplies the channel; the pinned key
/// carries the trust forward.
/// </para>
/// </remarks>
public static class PairingCode
{
    /// <summary>
    /// Domain separation. Hashing the keys bare would let a digest computed for some other
    /// purpose be replayed as a pairing code.
    /// </summary>
    private static ReadOnlySpan<byte> Tag => "lightdrop-sas-v1"u8;

    private const uint Modulus = 1_000_000;

    /// <summary>
    /// Derives the six digits both users compare, from the two devices'
    /// SubjectPublicKeyInfo blobs.
    /// </summary>
    /// <remarks>
    /// Symmetric in its arguments: the keys are sorted by byte order before hashing, so both
    /// machines reach the same value with no extra round trip.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// A key is empty, or both keys are the same — meaning the peer is this device, or is
    /// replaying this device's key back at it. Either way there is nothing to compare.
    /// </exception>
    public static string Derive(ReadOnlySpan<byte> localPublicKey, ReadOnlySpan<byte> remotePublicKey)
    {
        if (localPublicKey.IsEmpty || remotePublicKey.IsEmpty)
        {
            throw new ArgumentException("A pairing code needs a public key from both devices.");
        }

        if (localPublicKey.SequenceEqual(remotePublicKey))
        {
            throw new ArgumentException("Both sides presented the same public key.");
        }

        var localSortsFirst = localPublicKey.SequenceCompareTo(remotePublicKey) < 0;
        var lower = localSortsFirst ? localPublicKey : remotePublicKey;
        var higher = localSortsFirst ? remotePublicKey : localPublicKey;

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];

        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            hash.AppendData(Tag);
            hash.AppendData(lower);
            hash.AppendData(higher);
            hash.GetHashAndReset(digest);
        }

        // Truncation to six digits is what makes the code readable aloud. It leaves ~20 bits, the
        // same order as Bluetooth numeric comparison: an attacker gets one guess in front of a
        // human who is looking at the other screen, not an offline search.
        var value = BinaryPrimitives.ReadUInt32BigEndian(digest) % Modulus;

        return value.ToString("D6", CultureInfo.InvariantCulture);
    }
}
