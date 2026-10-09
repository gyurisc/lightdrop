using LightDrop.Core.Commands;
using LightDrop.Core.Configuration;
using LightDrop.Core.Devices;
using LightDrop.Core.Health;
using LightDrop.Core.Tests.Fakes;

namespace LightDrop.Core.Tests;

public sealed class HealthServiceTests
{
    private static HealthService CreateService(
        LightDropConfig? config = null,
        LightDropState? state = null,
        params string[] commands) =>
        new(
            new DeviceIdentityProvider(new InMemoryStateStore(state), new InMemoryConfigStore(config)),
            new CommandCapabilities(commands));

    [Fact]
    public async Task ReportsIdentityFromTheProvider()
    {
        var service = CreateService(
            config: new LightDropConfig { DeviceName = "Work Laptop" },
            state: new LightDropState { DeviceId = "device-1" });

        var health = await service.GetHealthAsync(CancellationToken.None);

        Assert.Equal("device-1", health.DeviceId);
        Assert.Equal("Work Laptop", health.DeviceName);
    }

    [Fact]
    public async Task ReportsVersionAndProtocolVersionSeparately()
    {
        // Peers negotiate on the protocol version, not the application version, so the two
        // must stay independently reported.
        var service = CreateService();

        var health = await service.GetHealthAsync(CancellationToken.None);

        Assert.Equal(LightDropVersion.Current, health.Version);
        Assert.Equal(LightDropVersion.Protocol, health.ProtocolVersion);
    }

    [Fact]
    public async Task ReportsAStablePlatformToken()
    {
        var service = CreateService();

        var health = await service.GetHealthAsync(CancellationToken.None);

        Assert.Contains(
            health.Platform,
            new[] { DevicePlatform.Windows, DevicePlatform.MacOS, DevicePlatform.Linux, DevicePlatform.Unknown });
    }

    [Fact]
    public async Task AdvertisesNoCapabilitiesWhenNoCommandsAreRegistered()
    {
        var service = CreateService();

        var health = await service.GetHealthAsync(CancellationToken.None);

        Assert.Empty(health.Capabilities);
    }

    [Fact]
    public async Task AdvertisesExactlyTheRegisteredCommands()
    {
        // A projection of what is registered, never a hand-kept list: a list maintained separately
        // drifts from what the daemon actually accepts the first time someone forgets it.
        var service = CreateService(commands: ["SendFile", "Ping", "SendFile"]);

        var health = await service.GetHealthAsync(CancellationToken.None);

        Assert.Equal(["Ping", "SendFile"], health.Capabilities);
    }
}
