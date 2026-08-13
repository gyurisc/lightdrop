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
/// Resolving the identity here rather than lazily on the first request means a broken state file
/// or an unwritable data directory fails at startup, where it is obvious, instead of surfacing
/// as a confusing 500 much later.
/// </remarks>
internal sealed class DaemonLifetimeService(
    DeviceIdentityProvider identityProvider,
    DaemonEndpointOptions endpoint,
    ILogger<DaemonLifetimeService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var identity = await identityProvider.GetAsync(cancellationToken).ConfigureAwait(false);

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
