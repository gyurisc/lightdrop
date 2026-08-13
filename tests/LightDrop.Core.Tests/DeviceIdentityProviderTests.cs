using LightDrop.Core.Configuration;
using LightDrop.Core.Devices;
using LightDrop.Core.Tests.Fakes;

namespace LightDrop.Core.Tests;

public sealed class DeviceIdentityProviderTests
{
    [Fact]
    public async Task GeneratesAndPersistsIdentityOnFirstRun()
    {
        var stateStore = new InMemoryStateStore();
        var provider = new DeviceIdentityProvider(stateStore, new InMemoryConfigStore());

        var identity = await provider.GetAsync(CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(identity.Id));
        Assert.Equal(1, stateStore.SaveCount);
        Assert.Equal(identity.Id, stateStore.Current.DeviceId);
    }

    [Fact]
    public async Task ReusesExistingIdentityAndDoesNotRewriteState()
    {
        // Peers pin trust to the device id, so a restart must never mint a new one.
        var stateStore = new InMemoryStateStore(new LightDropState { DeviceId = "existing-device-id" });
        var provider = new DeviceIdentityProvider(stateStore, new InMemoryConfigStore());

        var identity = await provider.GetAsync(CancellationToken.None);

        Assert.Equal("existing-device-id", identity.Id);
        Assert.Equal(0, stateStore.SaveCount);
    }

    [Fact]
    public async Task PreservesTrustedPeersWhenGeneratingIdentity()
    {
        // Writing the new id must not drop pairings that already exist in the same file.
        var peer = new TrustedPeer
        {
            DeviceId = "peer-1",
            DeviceName = "MacBook Air",
            PairedAt = DateTimeOffset.UnixEpoch,
        };
        var stateStore = new InMemoryStateStore(new LightDropState { TrustedPeers = [peer] });
        var provider = new DeviceIdentityProvider(stateStore, new InMemoryConfigStore());

        await provider.GetAsync(CancellationToken.None);

        Assert.Equal(peer, Assert.Single(stateStore.Current.TrustedPeers));
    }

    [Fact]
    public async Task UsesConfiguredDeviceName()
    {
        var provider = new DeviceIdentityProvider(
            new InMemoryStateStore(),
            new InMemoryConfigStore(new LightDropConfig { DeviceName = "  Work Laptop  " }));

        var identity = await provider.GetAsync(CancellationToken.None);

        Assert.Equal("Work Laptop", identity.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FallsBackToMachineNameWhenDeviceNameIsBlank(string? configuredName)
    {
        var provider = new DeviceIdentityProvider(
            new InMemoryStateStore(),
            new InMemoryConfigStore(new LightDropConfig { DeviceName = configuredName }));

        var identity = await provider.GetAsync(CancellationToken.None);

        Assert.Equal(Environment.MachineName, identity.Name);
    }

    [Fact]
    public async Task ResolvesOnceAndCachesThereafter()
    {
        var stateStore = new InMemoryStateStore();
        var provider = new DeviceIdentityProvider(stateStore, new InMemoryConfigStore());

        var first = await provider.GetAsync(CancellationToken.None);
        var second = await provider.GetAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, stateStore.LoadCount);
    }

    [Fact]
    public async Task ConcurrentCallersResolveTheSameIdentity()
    {
        // The health endpoint is reachable the moment Kestrel binds, so concurrent first-touch
        // is realistic. Two ids being generated here would be silently corrupting.
        var stateStore = new InMemoryStateStore();
        var provider = new DeviceIdentityProvider(stateStore, new InMemoryConfigStore());

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(async _ =>
                await provider.GetAsync(CancellationToken.None)));

        Assert.Single(results.Select(identity => identity.Id).Distinct());
        Assert.Equal(1, stateStore.SaveCount);
    }
}
