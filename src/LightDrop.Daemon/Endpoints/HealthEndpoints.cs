using LightDrop.Core.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using LightDrop.Core.Contracts;

namespace LightDrop.Daemon.Endpoints;

internal static class HealthEndpoints
{
    /// <summary>
    /// Maps <c>GET /health</c>: liveness plus the identity and capability information a peer
    /// needs before it can talk to this device.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", async Task<Ok<HealthResponse>> (
                IHealthService healthService,
                CancellationToken cancellationToken) =>
            TypedResults.Ok(await healthService.GetHealthAsync(cancellationToken).ConfigureAwait(false)))
            .WithName("Health");

        return endpoints;
    }
}
