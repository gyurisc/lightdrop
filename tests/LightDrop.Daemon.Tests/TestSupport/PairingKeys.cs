using LightDrop.Core.Devices;
using LightDrop.Daemon.Infrastructure;
using Microsoft.Extensions.Options;

namespace LightDrop.Daemon.Tests.TestSupport;

/// <summary>
/// Three device key pairs, made once and shared by every test in a class.
/// </summary>
/// <remarks>
/// Generated through the real provider and a real state file, not hand-built certificates. The
/// certificate the provider issues is the one production presents, including the Windows PKCS#12
/// round trip — a test using a differently-built certificate could pass while the real one failed
/// the handshake.
/// </remarks>
public sealed class PairingKeys : IAsyncLifetime
{
    private readonly List<TempDataDirectory> _directories = [];

    public DeviceKeyPair Alice { get; private set; } = null!;

    public DeviceKeyPair Bob { get; private set; } = null!;

    /// <summary>A third, valid device that neither of the others has pinned.</summary>
    public DeviceKeyPair Impostor { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Alice = await CreateAsync();
        Bob = await CreateAsync();
        Impostor = await CreateAsync();
    }

    public Task DisposeAsync()
    {
        // The provider hands out the certificate it caches; nothing else in this fixture's
        // lifetime owns it, so disposing it here is the only place it happens.
        Alice.Certificate.Dispose();
        Bob.Certificate.Dispose();
        Impostor.Certificate.Dispose();

        foreach (var directory in _directories)
        {
            directory.Dispose();
        }

        return Task.CompletedTask;
    }

    private async Task<DeviceKeyPair> CreateAsync()
    {
        var directory = new TempDataDirectory();
        _directories.Add(directory);

        var store = new JsonStateStore(
            Options.Create(new StorageOptions { DataDirectory = directory.FullPath }));

        return await new DeviceKeyProvider(store).GetAsync(CancellationToken.None);
    }
}
