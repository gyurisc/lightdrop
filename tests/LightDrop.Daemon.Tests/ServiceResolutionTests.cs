using LightDrop.Core.Configuration;
using LightDrop.Core.Pairing;
using LightDrop.Daemon.Discovery;
using LightDrop.Daemon.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// Every registered service can actually be constructed.
/// </summary>
/// <remarks>
/// A dependency the container cannot satisfy stays invisible until something first asks for the
/// service. For pairing that would be the moment a user runs <c>lightdrop pair</c> — the worst
/// possible time to discover a wiring mistake.
/// </remarks>
public sealed class ServiceResolutionTests
{
    [Fact]
    public void ResolvesThePairingService()
    {
        using var directory = new TempDataDirectory();
        var endpoint = new DaemonEndpointOptions { Host = "127.0.0.1", Port = FreeTcpPort.Get() };

        using var app = LightDropDaemon.Create(
            endpoint, directory.FullPath, new NoOpPeerDiscoveryTransport());

        Assert.NotNull(app.Services.GetRequiredService<PairingService>());
    }
}
