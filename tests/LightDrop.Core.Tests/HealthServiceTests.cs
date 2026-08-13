using LightDrop.Core.Configuration;
using LightDrop.Core.Devices;
using LightDrop.Core.Health;
using LightDrop.Core.Protocol;
using LightDrop.Core.Tests.Fakes;

namespace LightDrop.Core.Tests;

public sealed class HealthServiceTests
{
    private static HealthService CreateService(
        LightDropConfig? config = null,
        LightDropState? state = null,
        params ICommandHandler[] handlers) =>
        new(
            new DeviceIdentityProvider(new InMemoryStateStore(state), new InMemoryConfigStore(config)),
            new CommandRegistry(handlers));

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
        var service = CreateService();

        var health = await service.GetHealthAsync(CancellationToken.None);

        Assert.Empty(health.Capabilities);
    }

    [Fact]
    public async Task AdvertisesRegisteredCommandsAsCapabilities()
    {
        // This is the contract that lets the protocol grow additively: a peer discovers what
        // this device supports without a protocol version bump.
        var service = CreateService(
            handlers: [new StubCommandHandler("file.send"), new StubCommandHandler("clipboard.text")]);

        var health = await service.GetHealthAsync(CancellationToken.None);

        Assert.Equal(["clipboard.text", "file.send"], health.Capabilities);
    }
}
