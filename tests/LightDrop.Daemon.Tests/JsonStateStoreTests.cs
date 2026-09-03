using LightDrop.Core.Configuration;
using LightDrop.Daemon.Infrastructure;
using LightDrop.Daemon.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace LightDrop.Daemon.Tests;

public sealed class JsonStateStoreTests
{
    private static JsonStateStore CreateStore(string dataDirectory) =>
        new(Options.Create(new StorageOptions { DataDirectory = dataDirectory }));

    private static LightDropState StateWithPeer(string deviceId, string peerId) => new()
    {
        DeviceId = deviceId,
        TrustedPeers =
        [
            new TrustedPeer
            {
                DeviceId = peerId,
                DeviceName = "MacBook Air",
                PublicKey = "cGVlci0xLXB1YmxpYy1rZXk=",
                PairedAt = DateTimeOffset.UnixEpoch,
            },
        ],
    };

    [Fact]
    public async Task ReturnsEmptyStateBeforeFirstRun()
    {
        using var directory = new TempDataDirectory();
        var store = CreateStore(directory.FullPath);

        var state = await store.LoadAsync(CancellationToken.None);

        Assert.Null(state.DeviceId);
        Assert.Empty(state.TrustedPeers);
    }

    [Fact]
    public async Task RoundTripsIdentityAndPairedPeers()
    {
        using var directory = new TempDataDirectory();
        var store = CreateStore(directory.FullPath);

        await store.SaveAsync(StateWithPeer("device-1", "peer-1"), CancellationToken.None);
        var loaded = await CreateStore(directory.FullPath).LoadAsync(CancellationToken.None);

        Assert.Equal("device-1", loaded.DeviceId);

        // Compared element-wise on purpose: record equality falls back to reference equality for
        // IReadOnlyList, so Assert.Equal on the two LightDropState values would pass vacuously.
        var peer = Assert.Single(loaded.TrustedPeers);
        Assert.Equal("peer-1", peer.DeviceId);
        Assert.Equal("MacBook Air", peer.DeviceName);

        // The pinned key must survive the round trip: JSON source generation fails at runtime
        // rather than at build, so a property the context does not emit would silently disarm
        // every trust check on the next start.
        Assert.Equal("cGVlci0xLXB1YmxpYy1rZXk=", peer.PublicKey);
        Assert.Equal(DateTimeOffset.UnixEpoch, peer.PairedAt);
    }

    [Fact]
    public async Task CreatesTheDataDirectoryOnFirstSave()
    {
        // First run on a clean machine: nothing has created the directory yet.
        using var directory = new TempDataDirectory();
        var nested = Path.Combine(directory.FullPath, "not-created-yet");
        var store = CreateStore(nested);

        await store.SaveAsync(new LightDropState { DeviceId = "device-1" }, CancellationToken.None);

        Assert.True(Directory.Exists(nested));
    }

    [Fact]
    public async Task ThrowsOnCorruptStateRatherThanSilentlyResettingIdentity()
    {
        // Starting fresh would mint a new device id and invalidate every pairing on every other
        // machine. Failing loudly is the whole point.
        using var directory = new TempDataDirectory();
        directory.WriteState("{ this is not json");
        var store = CreateStore(directory.FullPath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.LoadAsync(CancellationToken.None));

        Assert.Contains(directory.StateFilePath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavesNoTemporaryFilesBehindAfterSaving()
    {
        // Proxy for the write-then-rename being completed rather than abandoned. Deliberately
        // does not name the temp file, which is an implementation detail.
        using var directory = new TempDataDirectory();
        var store = CreateStore(directory.FullPath);

        await store.SaveAsync(new LightDropState { DeviceId = "device-1" }, CancellationToken.None);
        await store.SaveAsync(new LightDropState { DeviceId = "device-2" }, CancellationToken.None);

        var file = Assert.Single(Directory.GetFiles(directory.FullPath));
        Assert.Equal(directory.StateFilePath, file);
    }

    [Fact]
    public async Task SerialisesOverlappingSavesAndLeavesTheFileParseable()
    {
        // Scoped honestly: all calls go through one store instance, so the semaphore serialises
        // them before any file I/O happens. This covers the in-process gate and the final file
        // being readable — it does NOT prove crash-mid-write atomicity, which would need a
        // fault-injection seam that does not exist, nor cross-process safety, which nothing in
        // LightDrop currently provides.
        using var directory = new TempDataDirectory();
        var store = CreateStore(directory.FullPath);

        await Task.WhenAll(
            Enumerable.Range(0, 20).Select(i =>
                store.SaveAsync(new LightDropState { DeviceId = $"device-{i}" }, CancellationToken.None).AsTask()));

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.NotNull(loaded.DeviceId);
        Assert.StartsWith("device-", loaded.DeviceId, StringComparison.Ordinal);
    }
}
