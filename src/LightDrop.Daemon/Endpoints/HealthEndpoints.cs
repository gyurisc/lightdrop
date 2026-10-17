using LightDrop.Core.Contracts;
using LightDrop.Core.Health;
using LightDrop.Daemon.Peers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace LightDrop.Daemon.Endpoints;

internal static class HealthEndpoints
{
    /// <summary>
    /// Maps <c>GET /health</c>: liveness plus the identity and capability information a peer
    /// needs before it can talk to this device, and whether paired devices can reach it.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", async Task<Ok<HealthResponse>> (
                HealthService healthService,
                PeerListener listener,
                CancellationToken cancellationToken) =>
            {
                var health = await healthService.GetHealthAsync(cancellationToken).ConfigureAwait(false);

                // Added here rather than in HealthService: the listener is the daemon's, and Core
                // cannot see it.
                return TypedResults.Ok(health with { PeerListener = listener.Health });
            })
            .WithName("Health");

        return endpoints;
    }
}
