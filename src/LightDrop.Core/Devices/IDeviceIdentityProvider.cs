namespace LightDrop.Core.Devices;

/// <summary>
/// Resolves this device's identity, creating and persisting it on first use.
/// </summary>
public interface IDeviceIdentityProvider
{
    ValueTask<DeviceIdentity> GetAsync(CancellationToken cancellationToken = default);
}
