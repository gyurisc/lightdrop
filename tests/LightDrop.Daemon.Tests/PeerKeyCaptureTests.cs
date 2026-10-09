using LightDrop.Daemon.Pairing;
using LightDrop.Daemon.Tests.TestSupport;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// The dialer's record of which key answered hello.
/// </summary>
/// <remarks>
/// The capturing handler accepts an unknown key exactly once. If it accepted a second, different
/// key on a later connection, a hello response could come from someone other than the key the
/// code is derived from.
/// </remarks>
public sealed class PeerKeyCaptureTests(PairingKeys keys) : IClassFixture<PairingKeys>
{
    [Fact]
    public void CapturesTheFirstKeyItMeets()
    {
        var capture = new PeerKeyCapture();

        Assert.Null(capture.Key);
        Assert.True(capture.CaptureOrMatch(keys.Alice.Certificate));
        Assert.Equal(keys.Alice.PublicKeyInfo, capture.Key);
    }

    [Fact]
    public void AcceptsTheSameKeyAgain()
    {
        var capture = new PeerKeyCapture();
        capture.CaptureOrMatch(keys.Alice.Certificate);

        Assert.True(capture.CaptureOrMatch(keys.Alice.Certificate));
    }

    [Fact]
    public void RefusesADifferentKeyOnceOneIsCaptured()
    {
        var capture = new PeerKeyCapture();
        capture.CaptureOrMatch(keys.Alice.Certificate);

        Assert.False(capture.CaptureOrMatch(keys.Impostor.Certificate));
        Assert.Equal(keys.Alice.PublicKeyInfo, capture.Key);
    }
}
