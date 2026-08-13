using LightDrop.Core.Contracts;
using LightDrop.Core.Devices;
using LightDrop.Core.Protocol;

namespace LightDrop.Core.Health;

/// <inheritdoc cref="IHealthService"/>
public sealed class HealthService(IDeviceIdentityProvider identityProvider, CommandRegistry commandRegistry)
    : IHealthService
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

            // Projected from the registered handlers, so this can never claim support the
            // daemon does not actually have.
            Capabilities = commandRegistry.Capabilities,
        };
    }
}
