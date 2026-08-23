using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LightDrop.Core.Configuration;
using LightDrop.Core.Devices;
using LightDrop.Core.Tests.Fakes;

namespace LightDrop.Core.Tests;

/// <summary>
/// The device key pair: generated once, persisted, and stable across restarts.
/// </summary>
/// <remarks>
/// Stability is the whole point. Pairing pins the public key, so a key that changed on restart
/// would silently invalidate every pairing this device had completed — the same failure the state
/// store already throws to avoid for <c>deviceId</c>.
/// </remarks>
public sealed class DeviceKeyProviderTests
{
    [Fact]
    public async Task CreatesAndPersistsAKeyOnFirstUse()
    {
        var store = new InMemoryStateStore();

        var keyPair = await new DeviceKeyProvider(store).GetAsync(CancellationToken.None);

        Assert.NotNull(keyPair.Certificate);
        Assert.False(string.IsNullOrWhiteSpace(store.Current.DeviceKey));
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task ReusesThePersistedKeyAcrossRestarts()
    {
        var store = new InMemoryStateStore();

        var first = await new DeviceKeyProvider(store).GetAsync(CancellationToken.None);
        var second = await new DeviceKeyProvider(store).GetAsync(CancellationToken.None);

        // The certificate is reissued on every run; the key underneath it must not change.
        Assert.Equal(first.PublicKeyInfo, second.PublicKeyInfo);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task GeneratesADistinctKeyPerDevice()
    {
        var one = await new DeviceKeyProvider(new InMemoryStateStore()).GetAsync(CancellationToken.None);
        var two = await new DeviceKeyProvider(new InMemoryStateStore()).GetAsync(CancellationToken.None);

        Assert.NotEqual(one.PublicKeyInfo, two.PublicKeyInfo);
    }

    [Fact]
    public async Task IssuesACertificateHoldingThatKey()
    {
        // Or TLS would present one identity while pairing pinned another.
        var keyPair = await new DeviceKeyProvider(new InMemoryStateStore()).GetAsync(CancellationToken.None);

        Assert.Equal(keyPair.Certificate.PublicKey.ExportSubjectPublicKeyInfo(), keyPair.PublicKeyInfo);
        Assert.True(keyPair.Certificate.HasPrivateKey);
    }

    [Fact]
    public async Task UsesP256()
    {
        var keyPair = await new DeviceKeyProvider(new InMemoryStateStore()).GetAsync(CancellationToken.None);

        using var ecdsa = keyPair.Certificate.GetECDsaPrivateKey();
        Assert.NotNull(ecdsa);
        Assert.Equal(256, ecdsa.KeySize);
    }

    [Fact]
    public async Task ThrowsWhenThePersistedKeyIsUnreadable()
    {
        // Same reasoning as a corrupt state file: silently minting a replacement would invalidate
        // every existing pairing without telling anyone.
        var store = new InMemoryStateStore(new LightDropState { DeviceKey = "not-a-key" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await new DeviceKeyProvider(store).GetAsync(CancellationToken.None));

        Assert.Contains("paired", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task ResolvesOnceUnderConcurrentCallers()
    {
        var store = new InMemoryStateStore();
        var provider = new DeviceKeyProvider(store);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => provider.GetAsync(CancellationToken.None).AsTask()));

        Assert.Equal(1, store.SaveCount);
        Assert.All(results, keyPair => Assert.Same(results[0], keyPair));
    }
}
