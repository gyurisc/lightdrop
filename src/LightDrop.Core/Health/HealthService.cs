using LightDrop.Core.Contracts;
using LightDrop.Core.Devices;

namespace LightDrop.Core.Health;

/// <summary>
/// Builds this device's health snapshot.
/// </summary>
public sealed class HealthService(DeviceIdentityProvider identityProvider)
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

            // Empty until the first command handler ships (file transfer, milestone 3). When
            // commands exist this must be projected from the registered handlers rather than
            // hand-maintained, or the advertised list will drift from what the daemon accepts.
            Capabilities = [],
        };
    }
}
