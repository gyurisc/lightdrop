using LightDrop.Core.Configuration;

namespace LightDrop.Core.Devices;

/// <summary>
/// Get-or-create identity resolution over the config and state ports.
/// </summary>
/// <remarks>
/// The logic lives in Core and the file I/O lives behind <see cref="IStateStore"/> and
/// <see cref="IConfigStore"/>, which is what keeps this class testable without touching
/// a real filesystem.
/// </remarks>
public sealed class DeviceIdentityProvider(IStateStore stateStore, IConfigStore configStore) : IDeviceIdentityProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DeviceIdentity? _cached;

    public async ValueTask<DeviceIdentity> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check: a concurrent caller may have resolved while we waited on the gate.
            if (_cached is not null)
            {
                return _cached;
            }

            var state = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);

            var deviceId = state.DeviceId;
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                deviceId = Guid.NewGuid().ToString("n");
                await stateStore.SaveAsync(state with { DeviceId = deviceId }, cancellationToken).ConfigureAwait(false);
            }

            var config = await configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var deviceName = string.IsNullOrWhiteSpace(config.DeviceName)
                ? Environment.MachineName
                : config.DeviceName.Trim();

            _cached = new DeviceIdentity(deviceId, deviceName);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}
