using LightDrop.Core.Configuration;
using LightDrop.Core.Pairing;
using LightDrop.Core.Tests.Fakes;

namespace LightDrop.Core.Tests;

/// <summary>
/// Pin-or-reject: the rules that decide whether a peer is trusted.
/// </summary>
/// <remarks>
/// This is the one place in LightDrop where a discovered stranger becomes a trusted peer, so the
/// interesting tests are the refusals. A peer is trusted only when it presents the exact key
/// pinned at pairing — a matching device id proves nothing, because anyone on the link can put
/// any id in an mDNS record.
/// </remarks>
public sealed class PairingServiceTests
{
    private static readonly byte[] KeyA = [1, 2, 3, 4];
    private static readonly byte[] KeyB = [5, 6, 7, 8];

    private static readonly DateTimeOffset Now = new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PinsAPeer()
    {
        var store = new InMemoryStateStore();
        var service = Create(store);

        await service.PinAsync("peer-a", "Work Laptop", KeyA, CancellationToken.None);

        var peer = Assert.Single(store.Current.TrustedPeers);
        Assert.Equal("peer-a", peer.DeviceId);
        Assert.Equal("Work Laptop", peer.DeviceName);
        Assert.Equal(Convert.ToBase64String(KeyA), peer.PublicKey);
        Assert.Equal(Now, peer.PairedAt);
    }

    [Fact]
    public async Task TrustsAPinnedPeerPresentingItsKey()
    {
        var service = Create(new InMemoryStateStore());
        await service.PinAsync("peer-a", "Work Laptop", KeyA, CancellationToken.None);

        Assert.True(await service.IsTrustedAsync("peer-a", KeyA, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsAPinnedPeerPresentingADifferentKey()
    {
        // The whole point of pinning. A stolen or spoofed device id must not be enough.
        var service = Create(new InMemoryStateStore());
        await service.PinAsync("peer-a", "Work Laptop", KeyA, CancellationToken.None);

        Assert.False(await service.IsTrustedAsync("peer-a", KeyB, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsAKnownKeyUnderTheWrongDeviceId()
    {
        var service = Create(new InMemoryStateStore());
        await service.PinAsync("peer-a", "Work Laptop", KeyA, CancellationToken.None);

        Assert.False(await service.IsTrustedAsync("peer-b", KeyA, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsAnUnknownPeer()
    {
        var service = Create(new InMemoryStateStore());

        Assert.False(await service.IsTrustedAsync("stranger", KeyA, CancellationToken.None));
    }

    [Fact]
    public async Task RefusesToRepinAnAlreadyPairedPeer()
    {
        // Silently replacing a pinned key is a downgrade path: an attacker who gets one pairing
        // through would overwrite the real device's key. Replacement must be an explicit unpair.
        var store = new InMemoryStateStore();
        var service = Create(store);
        await service.PinAsync("peer-a", "Work Laptop", KeyA, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PinAsync("peer-a", "Impostor", KeyB, CancellationToken.None).AsTask());

        var peer = Assert.Single(store.Current.TrustedPeers);
        Assert.Equal(Convert.ToBase64String(KeyA), peer.PublicKey);
    }

    [Fact]
    public async Task UnpinRemovesThePeer()
    {
        var service = Create(new InMemoryStateStore());
        await service.PinAsync("peer-a", "Work Laptop", KeyA, CancellationToken.None);

        Assert.True(await service.UnpinAsync("peer-a", CancellationToken.None));
        Assert.False(await service.IsTrustedAsync("peer-a", KeyA, CancellationToken.None));
    }

    [Fact]
    public async Task UnpinningAnUnknownPeerChangesNothing()
    {
        // `lightdrop unpair` on a peer that was never paired is a no-op, not a failure.
        var store = new InMemoryStateStore();
        var service = Create(store);

        Assert.False(await service.UnpinAsync("stranger", CancellationToken.None));
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task KeepsOtherPeersWhenUnpinningOne()
    {
        var store = new InMemoryStateStore();
        var service = Create(store);
        await service.PinAsync("peer-a", "Work Laptop", KeyA, CancellationToken.None);
        await service.PinAsync("peer-b", "Mac Mini", KeyB, CancellationToken.None);

        await service.UnpinAsync("peer-a", CancellationToken.None);

        var peer = Assert.Single(store.Current.TrustedPeers);
        Assert.Equal("peer-b", peer.DeviceId);
    }

    [Fact]
    public async Task ListsTrustedPeers()
    {
        var service = Create(new InMemoryStateStore());
        await service.PinAsync("peer-a", "Work Laptop", KeyA, CancellationToken.None);

        var peer = Assert.Single(await service.ListAsync(CancellationToken.None));
        Assert.Equal("Work Laptop", peer.DeviceName);
    }

    [Fact]
    public async Task RefusesToPinAnEmptyKey()
    {
        // A peer with no key could never be verified again, so the entry would trust a device id
        // alone — exactly what pinning exists to prevent.
        var service = Create(new InMemoryStateStore());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.PinAsync("peer-a", "Work Laptop", ReadOnlyMemory<byte>.Empty, CancellationToken.None).AsTask());
    }

    private static PairingService Create(InMemoryStateStore store)
        => new(store, new FakeTimeProvider(Now));
}
