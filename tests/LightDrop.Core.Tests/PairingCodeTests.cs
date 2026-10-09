using LightDrop.Core.Pairing;

namespace LightDrop.Core.Tests;

/// <summary>
/// The six digits both users compare before trusting each other, and the commitment behind them.
/// </summary>
/// <remarks>
/// The code is never transmitted. Each side derives it from the two public keys and the two
/// session nonces, so a man-in-the-middle -- holding a different key toward each side -- makes the
/// two screens disagree. That only holds if the derivation is sensitive to every input and the
/// listener cannot change its nonce after committing, which is what these tests pin down.
/// </remarks>
public sealed class PairingCodeTests
{
    // Deterministic stand-ins for two SubjectPublicKeyInfo blobs. A P-256 SPKI is 91 bytes.
    private static readonly byte[] DialerKey = CreateKey(1);
    private static readonly byte[] ListenerKey = CreateKey(101);

    private static readonly byte[] DialerNonce = Filled(0x11);
    private static readonly byte[] ListenerNonce = Filled(0x22);

    [Fact]
    public void DerivesSixDigits()
    {
        var code = PairingCode.Derive(DialerKey, ListenerKey, DialerNonce, ListenerNonce);

        Assert.Equal(6, code.Length);
        Assert.All(code, character => Assert.True(char.IsAsciiDigit(character)));
    }

    [Fact]
    public void MatchesAKnownVector()
    {
        // Computed independently of this implementation (Python's hashlib). If the tag, the hash,
        // the field framing, the order or the truncation ever change, two versions of LightDrop
        // stop agreeing on the digits and pairing silently becomes impossible between them.
        Assert.Equal("335326", PairingCode.Derive(DialerKey, ListenerKey, DialerNonce, ListenerNonce));
    }

    [Fact]
    public void CommitmentMatchesAKnownVector()
    {
        Assert.Equal(
            "80399c72cf2bad1de55b74e700d8704cb5f3ba1b264afe9659c85332a34f4b05",
            Convert.ToHexStringLower(PairingCode.Commit(ListenerKey, DialerKey, ListenerNonce)));
    }

    [Fact]
    public void TheSameTwoKeysGiveADifferentCodeInEverySession()
    {
        // The regression this design exists for. Derived from the two keys alone, the digits were
        // fixed before the session began, and an attacker who had seen both keys could search
        // offline for a key pair of its own that made both screens agree. Fresh nonces on both
        // sides mean there is nothing to search for in advance.
        var codes = Enumerable.Range(0, 20)
            .Select(_ => PairingCode.Derive(DialerKey, ListenerKey, PairingCode.CreateNonce(), PairingCode.CreateNonce()))
            .ToHashSet();

        Assert.True(codes.Count > 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ChangesWhenAnyInputByteChanges(int input)
    {
        byte[][] inputs = [(byte[])DialerKey.Clone(), (byte[])ListenerKey.Clone(), (byte[])DialerNonce.Clone(), (byte[])ListenerNonce.Clone()];
        inputs[input][^1] ^= 0x01;

        Assert.NotEqual(
            PairingCode.Derive(DialerKey, ListenerKey, DialerNonce, ListenerNonce),
            PairingCode.Derive(inputs[0], inputs[1], inputs[2], inputs[3]));
    }

    [Fact]
    public void PadsShortValuesToSixDigits()
    {
        // Without padding this renders as five digits, and the two sides can disagree on a code
        // they would otherwise both have derived correctly.
        var listenerNonce = new byte[PairingCode.NonceLength];
        listenerNonce[^1] = 1;

        Assert.Equal("040962", PairingCode.Derive(DialerKey, ListenerKey, DialerNonce, listenerNonce));
    }

    [Fact]
    public void ACommitmentOpensWithItsOwnNonce()
    {
        var commitment = PairingCode.Commit(ListenerKey, DialerKey, ListenerNonce);

        Assert.True(PairingCode.Opens(commitment, ListenerKey, DialerKey, ListenerNonce));
    }

    [Fact]
    public void ACommitmentDoesNotOpenWithAnyOtherNonce()
    {
        // The property the whole exchange rests on: once committed, the listener cannot swap in a
        // nonce chosen after seeing the dialer's.
        var commitment = PairingCode.Commit(ListenerKey, DialerKey, ListenerNonce);

        Assert.False(PairingCode.Opens(commitment, ListenerKey, DialerKey, PairingCode.CreateNonce()));
    }

    [Fact]
    public void ACommitmentIsBoundToBothKeys()
    {
        // Otherwise a device in the middle could relay one side's commitment to the other
        // unchanged while holding different keys toward each.
        var commitment = PairingCode.Commit(ListenerKey, DialerKey, ListenerNonce);
        var otherKey = CreateKey(201);

        Assert.False(PairingCode.Opens(commitment, otherKey, DialerKey, ListenerNonce));
        Assert.False(PairingCode.Opens(commitment, ListenerKey, otherKey, ListenerNonce));
    }

    [Fact]
    public void MalformedInputNeverOpensACommitment()
    {
        // Everything Opens is handed came off the wire, so it answers false rather than throwing.
        var commitment = PairingCode.Commit(ListenerKey, DialerKey, ListenerNonce);

        Assert.False(PairingCode.Opens(commitment[..^1], ListenerKey, DialerKey, ListenerNonce));
        Assert.False(PairingCode.Opens(commitment, ListenerKey, DialerKey, ListenerNonce[..^1]));
        Assert.False(PairingCode.Opens(commitment, [], DialerKey, ListenerNonce));
    }

    [Fact]
    public void RejectsDerivingAgainstItself()
    {
        // Both sides holding the same key means the peer is this device, or is replaying this
        // device's key back at it. Either way there is nothing to compare.
        Assert.Throws<ArgumentException>(
            () => PairingCode.Derive(DialerKey, (byte[])DialerKey.Clone(), DialerNonce, ListenerNonce));
    }

    [Fact]
    public void RejectsAnEmptyKey()
    {
        Assert.Throws<ArgumentException>(() => PairingCode.Derive(DialerKey, [], DialerNonce, ListenerNonce));
    }

    [Fact]
    public void RejectsANonceOfTheWrongLength()
    {
        Assert.Throws<ArgumentException>(() => PairingCode.Derive(DialerKey, ListenerKey, DialerNonce[..^1], ListenerNonce));
        Assert.Throws<ArgumentException>(() => PairingCode.Commit(ListenerKey, DialerKey, []));
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

    private static byte[] Filled(byte value) => Enumerable.Repeat(value, PairingCode.NonceLength).ToArray();
}
