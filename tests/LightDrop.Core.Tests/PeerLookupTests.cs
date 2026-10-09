using LightDrop.Core.Pairing;

namespace LightDrop.Core.Tests;

/// <summary>
/// Turning what a user typed into exactly one device, or a clear refusal.
/// </summary>
/// <remarks>
/// This feeds <c>pair</c> and <c>unpair</c>, both trust operations, so the rule is: guess never.
/// Two devices that could both be meant is an error that names them, not a coin toss.
/// </remarks>
public sealed class PeerLookupTests
{
    private sealed record Peer(string Id, string Name);

    private static readonly Peer Laptop = new("ab12cd34ef", "Work Laptop");
    private static readonly Peer Mini = new("ff00aa11bb", "Mac Mini");
    private static readonly Peer OtherMini = new("ab99000000", "Mac Mini");

    private static PeerLookupResult<Peer> Find(string query, params Peer[] peers) =>
        PeerLookup.Find(query, peers, peer => peer.Id, peer => peer.Name);

    [Fact]
    public void MatchesANameIgnoringCase()
    {
        var result = Find("work laptop", Laptop, Mini);

        Assert.Equal(PeerLookupOutcome.Found, result.Outcome);
        Assert.Same(Laptop, result.Match);
    }

    [Fact]
    public void MatchesAnIdPrefix()
    {
        var result = Find("ff0", Laptop, Mini);

        Assert.Equal(PeerLookupOutcome.Found, result.Outcome);
        Assert.Same(Mini, result.Match);
    }

    [Fact]
    public void MatchesAFullId()
    {
        Assert.Same(Laptop, Find("ab12cd34ef", Laptop, Mini).Match);
    }

    [Fact]
    public void IgnoresSurroundingWhitespace()
    {
        Assert.Same(Mini, Find("  Mac Mini ", Laptop, Mini).Match);
    }

    [Fact]
    public void ReportsNoMatch()
    {
        var result = Find("Kitchen iPad", Laptop, Mini);

        Assert.Equal(PeerLookupOutcome.NotFound, result.Outcome);
        Assert.Null(result.Match);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void AnEmptyQueryMatchesNothing()
    {
        // An empty string is a prefix of every id; treating it as one would select whichever
        // device happened to be alone in the list.
        Assert.Equal(PeerLookupOutcome.NotFound, Find("", Laptop).Outcome);
        Assert.Equal(PeerLookupOutcome.NotFound, Find("   ", Laptop).Outcome);
    }

    [Fact]
    public void TwoDevicesWithTheSameNameAreAmbiguous()
    {
        // Device names are not unique and are chosen by whoever announces them.
        var result = Find("Mac Mini", Laptop, Mini, OtherMini);

        Assert.Equal(PeerLookupOutcome.Ambiguous, result.Outcome);
        Assert.Null(result.Match);
        Assert.Equal([Mini, OtherMini], result.Candidates);
    }

    [Fact]
    public void AShortIdPrefixSharedByTwoDevicesIsAmbiguous()
    {
        var result = Find("ab", Laptop, Mini, OtherMini);

        Assert.Equal(PeerLookupOutcome.Ambiguous, result.Outcome);
        Assert.Equal([Laptop, OtherMini], result.Candidates);
    }

    [Fact]
    public void ANameThatIsAlsoAnotherDevicesIdPrefixIsAmbiguous()
    {
        // A hostile announcer can pick its name to collide with a real device's id prefix. When
        // the two readings disagree, refuse rather than prefer one.
        var hostile = new Peer("0000000000", "ab12");

        var result = Find("ab12", Laptop, hostile);

        Assert.Equal(PeerLookupOutcome.Ambiguous, result.Outcome);
    }

    [Fact]
    public void AnExactIdBeatsEverythingElse()
    {
        // The full id is the one unambiguous handle a user has; it must always work, even when a
        // hostile device names itself after it.
        var hostile = new Peer("0000000000", "ab12cd34ef");

        var result = Find("ab12cd34ef", Laptop, hostile);

        Assert.Equal(PeerLookupOutcome.Found, result.Outcome);
        Assert.Same(Laptop, result.Match);
    }
}
