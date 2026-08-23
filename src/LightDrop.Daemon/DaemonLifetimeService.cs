using LightDrop.Core;
using LightDrop.Core.Configuration;
using LightDrop.Core.Devices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LightDrop.Daemon;

/// <summary>
/// Logs a single, informative line at startup and shutdown.
/// </summary>
/// <remarks>
/// Resolving the identity and key here rather than lazily on the first request means a broken
/// state file, an unreadable device key, or an unwritable data directory fails at startup, where
/// it is obvious, instead of surfacing as a confusing 500 much later.
/// </remarks>
internal sealed class DaemonLifetimeService(
    DeviceIdentityProvider identityProvider,
    DeviceKeyProvider keyProvider,
    DaemonEndpointOptions endpoint,
    ILogger<DaemonLifetimeService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var identity = await identityProvider.GetAsync(cancellationToken).ConfigureAwait(false);

        // Created on first run and reused thereafter. Resolved even though nothing presents it
        // yet: it is what pairing will pin, so a device that cannot produce one is broken now
        // rather than at the moment someone tries to pair.
        _ = await keyProvider.GetAsync(cancellationToken).ConfigureAwait(false);

        DaemonLog.Started(
            logger,
            LightDropVersion.Current,
            LightDropVersion.Protocol,
            endpoint.BaseAddress.ToString(),
            identity.Name,
            identity.Id,
            DevicePlatform.Current);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DaemonLog.Stopping(logger);
        return Task.CompletedTask;
    }
}
