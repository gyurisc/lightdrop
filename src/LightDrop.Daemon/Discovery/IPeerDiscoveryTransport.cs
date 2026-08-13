using LightDrop.Core.Devices;
using LightDrop.Core.Discovery;

namespace LightDrop.Daemon.Discovery;

/// <summary>
/// Announces this device on the local link and reports the peers it hears.
/// </summary>
/// <remarks>
/// A genuine port, not an interface out of habit. It exists because multicast cannot be
/// exercised in tests: CI runners cannot route it, and on macOS the Local Network privacy
/// permission makes it fail <em>silently</em> rather than throwing. Without this seam every
/// existing daemon test would start opening real multicast sockets the moment discovery is wired
/// into the host.
/// <para>
/// Read-only by design. There is no send path: LightDrop advertises presence and listens. It
/// accepts no commands and transfers nothing.
/// </para>
/// </remarks>
public interface IPeerDiscoveryTransport : IAsyncDisposable
{
    /// <summary>Raised when a peer announces or re-announces itself.</summary>
    event Action<PeerAnnouncement>? PeerAnnounced;

    /// <summary>Raised with a device identifier when a peer says goodbye.</summary>
    event Action<string>? PeerGone;

    /// <summary>Begins advertising and browsing.</summary>
    /// <param name="identity">This device's identity, as advertised to peers.</param>
    /// <param name="servicePort">
    /// The port placed in the SRV record. Informational: it is not reachable across the network
    /// and is not an authorization boundary.
    /// </param>
    Task StartAsync(DeviceIdentity identity, int servicePort, CancellationToken cancellationToken = default);

    /// <summary>Stops advertising, sending a goodbye so peers drop this device promptly.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
