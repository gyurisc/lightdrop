using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace LightDrop.Core.Pairing;

/// <summary>
/// The short human-verifiable code two users compare before trusting each other, and the
/// commitment that makes six digits enough.
/// </summary>
/// <remarks>
/// <strong>The code is never transmitted.</strong> Each side derives it independently from both
/// public keys and both sides' nonces, so a man-in-the-middle -- presenting a different key toward
/// each side -- has to make two independent derivations land on the same six digits.
/// <para>
/// <strong>Why there are nonces and a commitment.</strong> Six digits is about 20 bits. Derived
/// from the two static keys alone, an attacker who has seen both honest keys can generate key
/// pairs until one toward each side gives the same digits -- a birthday search over 10^6 values
/// that takes well under a second. This is Bluetooth Secure Simple Pairing's numeric comparison,
/// adopted as-is rather than adapted: the listener commits to a random nonce before it sees the
/// dialer's, the dialer reveals its nonce, then the listener opens its commitment. Neither side's
/// randomness can then be chosen after seeing the other's, so whoever sits in the middle gets one
/// try at a one-in-a-million match, in front of two humans, rather than an offline search.
/// </para>
/// <para>
/// The digits are therefore different every session, including when the same two devices pair
/// again. TLS supplies the channel; the pinned key carries trust forward.
/// </para>
/// </remarks>
public static class PairingCode
{
    /// <summary>Bytes of fresh randomness each side contributes per session.</summary>
    public const int NonceLength = 32;

    /// <summary>Bytes in a commitment: one SHA-256 digest.</summary>
    public const int CommitmentLength = SHA256.HashSizeInBytes;

    /// <summary>
    /// Domain separation, one tag per use. Hashing the inputs bare would let a digest computed for
    /// some other purpose -- or a commitment -- be replayed as a code, or the other way round.
    /// </summary>
    private static ReadOnlySpan<byte> CommitTag => "lightdrop-sas-commit-v2"u8;

    private static ReadOnlySpan<byte> CodeTag => "lightdrop-sas-v2"u8;

    private const uint Modulus = 1_000_000;

    /// <summary>A fresh nonce from the operating system's CSPRNG.</summary>
    public static byte[] CreateNonce() => RandomNumberGenerator.GetBytes(NonceLength);

    /// <summary>
    /// The listener's commitment to its nonce, sent before it has seen the dialer's.
    /// </summary>
    /// <remarks>
    /// Binds both keys as well as the nonce, as SSP's <c>f4</c> does: a commitment that named only
    /// the nonce could be relayed unchanged from one side of a man-in-the-middle to the other.
    /// </remarks>
    public static byte[] Commit(
        ReadOnlySpan<byte> listenerPublicKey,
        ReadOnlySpan<byte> dialerPublicKey,
        ReadOnlySpan<byte> listenerNonce)
    {
        RequireKeys(listenerPublicKey, dialerPublicKey);
        RequireNonce(listenerNonce);

        var digest = new byte[CommitmentLength];
        Hash(CommitTag, listenerPublicKey, dialerPublicKey, listenerNonce, [], digest);
        return digest;
    }

    /// <summary>Whether <paramref name="listenerNonce"/> is what <paramref name="commitment"/> committed to.</summary>
    /// <remarks>Constant-time. False rather than throwing for malformed input: all of it came off the wire.</remarks>
    public static bool Opens(
        ReadOnlySpan<byte> commitment,
        ReadOnlySpan<byte> listenerPublicKey,
        ReadOnlySpan<byte> dialerPublicKey,
        ReadOnlySpan<byte> listenerNonce)
    {
        if (commitment.Length != CommitmentLength
            || listenerNonce.Length != NonceLength
            || listenerPublicKey.IsEmpty
            || dialerPublicKey.IsEmpty
            || listenerPublicKey.SequenceEqual(dialerPublicKey))
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[CommitmentLength];
        Hash(CommitTag, listenerPublicKey, dialerPublicKey, listenerNonce, [], expected);
        return CryptographicOperations.FixedTimeEquals(expected, commitment);
    }

    /// <summary>
    /// Derives the six digits both users compare.
    /// </summary>
    /// <remarks>
    /// Ordered by role rather than sorted: both machines know which of them dials, so they hash
    /// the same inputs in the same order with no extra round trip.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// A key is empty, a nonce is the wrong length, or both keys are the same -- meaning the peer
    /// is this device, or is replaying this device's key back at it. Either way there is nothing
    /// to compare.
    /// </exception>
    public static string Derive(
        ReadOnlySpan<byte> dialerPublicKey,
        ReadOnlySpan<byte> listenerPublicKey,
        ReadOnlySpan<byte> dialerNonce,
        ReadOnlySpan<byte> listenerNonce)
    {
        RequireKeys(dialerPublicKey, listenerPublicKey);
        RequireNonce(dialerNonce);
        RequireNonce(listenerNonce);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        Hash(CodeTag, dialerPublicKey, listenerPublicKey, dialerNonce, listenerNonce, digest);

        // Truncation to six digits is what makes the code readable aloud. It leaves ~20 bits, the
        // same as Bluetooth numeric comparison -- enough only because of the commitment above.
        var value = BinaryPrimitives.ReadUInt32BigEndian(digest) % Modulus;

        return value.ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// SHA-256 over the tag and each field, every field preceded by its length.
    /// </summary>
    /// <remarks>
    /// Keys vary in length, so without the prefixes the boundary between two of them would be
    /// ambiguous: two different key pairs could concatenate to the same bytes.
    /// </remarks>
    private static void Hash(
        ReadOnlySpan<byte> tag,
        ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second,
        ReadOnlySpan<byte> third,
        ReadOnlySpan<byte> fourth,
        Span<byte> destination)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(tag);
        AppendField(hash, first);
        AppendField(hash, second);
        AppendField(hash, third);
        AppendField(hash, fourth);
        hash.GetHashAndReset(destination);
    }

    private static void AppendField(IncrementalHash hash, ReadOnlySpan<byte> field)
    {
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)field.Length);
        hash.AppendData(length);
        hash.AppendData(field);
    }

    private static void RequireKeys(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        if (first.IsEmpty || second.IsEmpty)
        {
            throw new ArgumentException("A pairing code needs a public key from both devices.");
        }

        if (first.SequenceEqual(second))
        {
            throw new ArgumentException("Both sides presented the same public key.");
        }
    }

    private static void RequireNonce(ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length != NonceLength)
        {
            throw new ArgumentException($"A pairing nonce is {NonceLength} bytes, not {nonce.Length}.");
        }
    }
}
