using LightDrop.Core.Devices;
using LightDrop.Core.Discovery;

namespace LightDrop.Daemon.Discovery;

/// <summary>
/// A transport that neither advertises nor listens.
/// </summary>
/// <remarks>
/// Used by tests, so the suite never opens a multicast socket, and available as an explicit
/// opt-out for anyone who wants the daemon without discovery.
/// </remarks>
public sealed class NoOpPeerDiscoveryTransport : IPeerDiscoveryTransport
{
#pragma warning disable CS0067 // Never raised: that is the entire point of this implementation.
    public event Action<PeerAnnouncement>? PeerAnnounced;

    public event Action<string>? PeerGone;
#pragma warning restore CS0067

    public Task StartAsync(DeviceIdentity identity, int servicePort, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
