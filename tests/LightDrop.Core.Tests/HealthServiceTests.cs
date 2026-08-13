using LightDrop.Core.Configuration;
using LightDrop.Core.Devices;
using LightDrop.Core.Health;
using LightDrop.Core.Tests.Fakes;

namespace LightDrop.Core.Tests;

public sealed class HealthServiceTests
{
    private static HealthService CreateService(
        LightDropConfig? config = null,
        LightDropState? state = null) =>
        new(new DeviceIdentityProvider(new InMemoryStateStore(state), new InMemoryConfigStore(config)));

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
    public async Task AdvertisesNoCapabilitiesWhileNoCommandsAreImplemented()
    {
        // The daemon accepts no commands yet and says so honestly. When handlers exist this
        // must become a projection of the registered set, not a hand-maintained list.
        var service = CreateService();

        var health = await service.GetHealthAsync(CancellationToken.None);

        Assert.Empty(health.Capabilities);
    }
}
