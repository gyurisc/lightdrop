using LightDrop.Core.Contracts;
using LightDrop.Core.Discovery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace LightDrop.Daemon.Endpoints;

internal static class PeerEndpoints
{
    /// <summary>
    /// Maps <c>GET /api/peers</c>: the nearby devices heard recently.
    /// </summary>
    /// <remarks>
    /// Reachable only over loopback, because that is the only address Kestrel binds. It exists
    /// for the local <c>lightdrop peers</c> command, not for peers — nothing on the network can
    /// call it. Read-only and side-effect free.
    /// </remarks>
    public static IEndpointRouteBuilder MapPeerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/peers", Ok<IReadOnlyList<DiscoveredPeer>> (PeerRegistry registry) =>
            TypedResults.Ok(registry.GetPeers()))
            .WithName("Peers");

        return endpoints;
    }
}
