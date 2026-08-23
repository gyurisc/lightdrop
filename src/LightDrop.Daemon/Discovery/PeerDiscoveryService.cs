using LightDrop.Core.Configuration;
using LightDrop.Core.Devices;
using LightDrop.Core.Discovery;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LightDrop.Daemon.Discovery;

/// <summary>
/// Connects the discovery transport to the in-memory peer registry.
/// </summary>
/// <remarks>
/// The registry is the only destination. There is deliberately no path from here to
/// <see cref="IStateStore"/>: a discovered peer is a stranger, and nothing it says may reach
/// <c>state.json</c> or <c>trustedPeers</c>. Pairing must cross that boundary explicitly rather
/// than inheriting a route that already exists.
/// </remarks>
internal sealed class PeerDiscoveryService(
    IPeerDiscoveryTransport transport,
    PeerRegistry registry,
    DiscoveryStatus status,
    DeviceIdentityProvider identityProvider,
    DaemonEndpointOptions endpoint,
    ILogger<PeerDiscoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        transport.PeerAnnounced += OnPeerAnnounced;
        transport.PeerGone += OnPeerGone;

        var identity = await identityProvider.GetAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await transport.StartAsync(identity, endpoint.Port, cancellationToken).ConfigureAwait(false);
            status.MarkRunning();
        }
        catch (Exception ex)
        {
            // Discovery is a convenience, not a prerequisite. A blocked firewall or a denied
            // macOS Local Network permission must not stop the daemon serving /health.
            status.MarkStopped();
            DiscoveryLog.StartFailed(logger, ex);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        transport.PeerAnnounced -= OnPeerAnnounced;
        transport.PeerGone -= OnPeerGone;
        status.MarkStopped();

        await transport.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnPeerAnnounced(PeerAnnouncement announcement)
    {
        // Only the first sighting is logged. Peers re-announce periodically, so logging every
        // observation would bury everything else in the daemon's output.
        if (registry.Observe(announcement))
        {
            DiscoveryLog.PeerAppeared(
                logger, announcement.DeviceName, announcement.Platform, announcement.DeviceId);
        }
    }

    private void OnPeerGone(string deviceId)
    {
        if (registry.Forget(deviceId))
        {
            DiscoveryLog.PeerDisappeared(logger, deviceId);
        }
    }
}
