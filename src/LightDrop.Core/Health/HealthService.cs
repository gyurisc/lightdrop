using LightDrop.Core.Commands;
using LightDrop.Core.Contracts;
using LightDrop.Core.Devices;

namespace LightDrop.Core.Health;

/// <summary>
/// Builds this device's health snapshot.
/// </summary>
public sealed class HealthService(DeviceIdentityProvider identityProvider, CommandCapabilities capabilities)
{
    public async ValueTask<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var identity = await identityProvider.GetAsync(cancellationToken).ConfigureAwait(false);

        return new HealthResponse
        {
            Version = LightDropVersion.Current,
            ProtocolVersion = LightDropVersion.Protocol,
            DeviceId = identity.Id,
            DeviceName = identity.Name,
            Platform = DevicePlatform.Current,

            // Projected from the registered handlers (see CommandCapabilities), never listed here.
            Capabilities = capabilities.Names,
        };
    }
}
