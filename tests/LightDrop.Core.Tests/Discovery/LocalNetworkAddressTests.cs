using LightDrop.Core.Discovery;

namespace LightDrop.Core.Tests.Discovery;

/// <summary>
/// Which addresses a peer is allowed to claim.
/// </summary>
/// <remarks>
/// This is the first thing discovery has ever captured that something will later be dialled at,
/// so it is the first place a hostile announcement can steer this machine rather than merely
/// misinform it. The rule is a range check, not a route check: refusing anything outside the
/// private and link-local ranges means a peer cannot point LightDrop at a host on the internet,
/// which is the redirection that matters.
/// </remarks>
public sealed class LocalNetworkAddressTests
{
    [Theory]
    [InlineData("192.168.0.149")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("169.254.10.20")]
    public void AcceptsAddressesOnAPrivateOrLinkLocalNetwork(string address)
    {
        Assert.True(LocalNetworkAddress.TryNormalize(address, out var normalized));
        Assert.Equal(address, normalized);
    }

    [Fact]
    public void AcceptsLoopback() =>
        // Two daemons on one machine is a supported way to exercise discovery, and the loopback
        // interface is deliberately kept in the interface filter for exactly that.
        Assert.True(LocalNetworkAddress.TryNormalize("127.0.0.1", out _));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("203.0.113.7")]
    public void RejectsAddressesOutsideTheLocalNetwork(string address)
    {
        // The attack this exists to stop: a peer announcing a third party's address so that
        // pairing opens a TLS connection to a host of the announcer's choosing.
        Assert.False(LocalNetworkAddress.TryNormalize(address, out var normalized));
        Assert.Null(normalized);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.251")]
    [InlineData("255.255.255.255")]
    public void RejectsAddressesThatNameNoSingleHost(string address) =>
        Assert.False(LocalNetworkAddress.TryNormalize(address, out _));

    [Fact]
    public void RejectsIpv6WhileDiscoveryIsIpv4Only() =>
        Assert.False(LocalNetworkAddress.TryNormalize("fe80::1", out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("192.168.0.149:5533")]
    public void RejectsWhatIsNotAnAddressAtAll(string? address) =>
        Assert.False(LocalNetworkAddress.TryNormalize(address, out _));

    [Fact]
    public void NormalizesSurroundingWhitespace()
    {
        Assert.True(LocalNetworkAddress.TryNormalize("  192.168.0.149  ", out var normalized));
        Assert.Equal("192.168.0.149", normalized);
    }
}
