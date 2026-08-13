using LightDrop.Core.Devices;
using LightDrop.Core.Discovery;
using LightDrop.Daemon.Discovery;

namespace LightDrop.Daemon.Tests.TestSupport;

/// <summary>
/// A discovery transport the test drives directly, with no socket involved.
/// </summary>
/// <remarks>
/// Every daemon test must supply one of these (or a
/// <see cref="NoOpPeerDiscoveryTransport"/>). Letting the real transport start would open
/// multicast sockets: CI runners cannot route multicast, and on macOS it fails silently without
/// the Local Network permission, so the failure would be a hang rather than a clean error.
/// </remarks>
internal sealed class FakePeerDiscoveryTransport : IPeerDiscoveryTransport
{
    public event Action<PeerAnnouncement>? PeerAnnounced;

    public event Action<string>? PeerGone;

    public DeviceIdentity? StartedWith { get; private set; }

    public int StartedOnPort { get; private set; }

    public bool Stopped { get; private set; }

    /// <summary>
    /// Makes <see cref="StartAsync"/> fail, standing in for a blocked firewall or a denied macOS
    /// Local Network permission.
    /// </summary>
    public bool FailToStart { get; init; }

    public Task StartAsync(DeviceIdentity identity, int servicePort, CancellationToken cancellationToken = default)
    {
        StartedWith = identity;
        StartedOnPort = servicePort;

        if (FailToStart)
        {
            throw new InvalidOperationException("Simulated discovery start failure.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Stopped = true;
        return Task.CompletedTask;
    }

    /// <summary>Simulates hearing a peer announce itself.</summary>
    public void Announce(
        string deviceId,
        string deviceName = "MacBook Air",
        string platform = DevicePlatform.MacOS,
        int protocolVersion = 1,
        int port = 5533)
    {
        if (!PeerAnnouncement.TryCreate(deviceId, deviceName, platform, protocolVersion, null, port, out var announcement))
        {
            throw new InvalidOperationException($"Test announcement for '{deviceId}' was rejected.");
        }

        PeerAnnounced?.Invoke(announcement!);
    }

    /// <summary>Simulates a peer saying goodbye.</summary>
    public void Goodbye(string deviceId) => PeerGone?.Invoke(deviceId);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
