using LightDrop.Core.Discovery;
using LightDrop.Daemon.Discovery;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// The re-query keeps a live peer listed.
/// </summary>
/// <remarks>
/// The registry forgets a peer it has not heard from in <see cref="PeerRegistry.StaleAfter"/>, and
/// the periodic refresh is the only thing that makes a live peer speak again. If the two drift apart,
/// peers flicker out of <c>lightdrop peers</c> — which is how every peer used to vanish three
/// minutes after start.
/// </remarks>
public sealed class DiscoveryTimingTests
{
    [Fact]
    public void ALivePeerIsRefreshedAtLeastThreeTimesBeforeItGoesStale()
    {
        Assert.True(MdnsPeerDiscoveryTransport.RefreshInterval * 3 <= PeerRegistry.StaleAfter);
    }

    [Fact]
    public void AHealthyTransportIsNeverCalledDeafBetweenTwoAnnouncements()
    {
        // It hears itself once per refresh; the watchdog must allow at least two missed in a row.
        Assert.True(MdnsPeerDiscoveryTransport.DeafAfter >= MdnsPeerDiscoveryTransport.RefreshInterval * 2);
    }
}
