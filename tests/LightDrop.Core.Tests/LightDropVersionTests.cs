namespace LightDrop.Core.Tests;

public sealed class LightDropVersionTests
{
    [Fact]
    public void ResolvesTheVersionFromTheAssembly()
    {
        // Guards the Directory.Build.props -> assembly attribute -> runtime path. If this returns
        // the "0.0.0" fallback, version metadata stopped flowing into the build.
        Assert.False(string.IsNullOrWhiteSpace(LightDropVersion.Current));
        Assert.NotEqual("0.0.0", LightDropVersion.Current);
        Assert.True(Version.TryParse(LightDropVersion.Current, out _));
    }

    [Fact]
    public void StripsTheSourceRevisionSuffix()
    {
        // The SDK appends "+<commit sha>" to the informational version; peers should never see it.
        Assert.DoesNotContain('+', LightDropVersion.Current);
    }

    [Fact]
    public void ProtocolVersionStartsAtOne()
    {
        Assert.Equal(1, LightDropVersion.Protocol);
    }
}
