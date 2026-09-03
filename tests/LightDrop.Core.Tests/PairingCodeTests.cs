using LightDrop.Core.Pairing;

namespace LightDrop.Core.Tests;

/// <summary>
/// The six digits both users compare before trusting each other.
/// </summary>
/// <remarks>
/// The code is never transmitted. Each side derives it from the two public keys, so a
/// man-in-the-middle — holding a different key toward each side — makes the two screens disagree.
/// That property only holds if the derivation is symmetric and sensitive to every key byte, which
/// is what these tests pin down.
/// </remarks>
public sealed class PairingCodeTests
{
    // Deterministic stand-ins for two SubjectPublicKeyInfo blobs. A P-256 SPKI is 91 bytes.
    private static readonly byte[] KeyA = CreateKey(1);
    private static readonly byte[] KeyB = CreateKey(101);

    [Fact]
    public void DerivesSixDigits()
    {
        var code = PairingCode.Derive(KeyA, KeyB);

        Assert.Equal(6, code.Length);
        Assert.All(code, character => Assert.True(char.IsAsciiDigit(character)));
    }

    [Fact]
    public void MatchesAKnownVector()
    {
        // Computed independently of this implementation. If the tag, the hash, the byte order or
        // the truncation ever change, two versions of LightDrop stop agreeing on the digits and
        // pairing silently becomes impossible between them.
        Assert.Equal("411722", PairingCode.Derive(KeyA, KeyB));
    }

    [Fact]
    public void IsIndependentOfArgumentOrder()
    {
        // Both machines run the same code with the arguments the other way round, and must land on
        // the same digits without exchanging them.
        Assert.Equal(PairingCode.Derive(KeyA, KeyB), PairingCode.Derive(KeyB, KeyA));
    }

    [Fact]
    public void ChangesWhenOneKeyByteChanges()
    {
        var tampered = (byte[])KeyB.Clone();
        tampered[^1] ^= 0x01;

        Assert.NotEqual(PairingCode.Derive(KeyA, KeyB), PairingCode.Derive(KeyA, tampered));
    }

    [Fact]
    public void PadsShortValuesToSixDigits()
    {
        // Without padding this renders as five digits, and the two sides can disagree on a code
        // they would otherwise both have derived correctly.
        Assert.Equal("051118", PairingCode.Derive([0, 0, 0, 31], [0xff, 0xff, 0xff, 0xff]));
    }

    [Fact]
    public void RejectsDerivingAgainstItself()
    {
        // Both sides holding the same key means the peer is this device, or is replaying this
        // device's key back at it. Either way there is nothing to compare.
        Assert.Throws<ArgumentException>(() => PairingCode.Derive(KeyA, (byte[])KeyA.Clone()));
    }

    [Fact]
    public void RejectsAnEmptyKey()
    {
        Assert.Throws<ArgumentException>(() => PairingCode.Derive(KeyA, []));
    }

    private static byte[] CreateKey(byte start)
    {
        var key = new byte[91];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(start + i);
        }

        return key;
    }
}
