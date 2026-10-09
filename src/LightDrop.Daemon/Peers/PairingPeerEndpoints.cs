using LightDrop.Core.Contracts;
using LightDrop.Daemon.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LightDrop.Daemon.Peers;

internal static class PairingPeerEndpoints
{
    /// <summary>
    /// Maps the two pairing routes on the peer listener -- the only routes it has in this slice.
    /// </summary>
    /// <remarks>
    /// Neither is behind <see cref="PinnedPeerFilter"/>: their callers are by definition not pinned
    /// yet. Each does its own check instead -- hello against the session's target and state,
    /// confirm against the exact key that joined at hello. A non-success answer carries no body:
    /// the caller is a stranger, and the reason is for the local user's log, not for it.
    /// </remarks>
    public static IEndpointRouteBuilder MapPairingPeerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/pair/hello", async Task<Results<Ok<PairHelloResponse>, Conflict>> (
                [FromBody] PairHelloRequest request,
                HttpContext context,
                [FromServices] PairingCoordinator coordinator) =>
            await coordinator
                .HelloAsync(request, context.Connection.ClientCertificate, context.RequestAborted)
                .ConfigureAwait(false) is { } response
                ? TypedResults.Ok(response)
                : TypedResults.Conflict());

        // A bare 403 rather than TypedResults.Forbid(): that one hands off to the authentication
        // stack, which this app deliberately does not have -- the TLS layer is its authentication.
        endpoints.MapPost("/pair/confirm", async Task<Results<Ok<PairingDecision>, StatusCodeHttpResult, Conflict>> (
                [FromBody] PairingDecision request,
                HttpContext context,
                [FromServices] PairingCoordinator coordinator) =>
        {
            var result = await coordinator
                .ConfirmFromPeerAsync(request, context.Connection.ClientCertificate, context.RequestAborted)
                .ConfigureAwait(false);

            return result.Outcome switch
            {
                PeerConfirmOutcome.Answered => TypedResults.Ok(new PairingDecision { Accepted = result.Accepted }),
                PeerConfirmOutcome.Forbidden => TypedResults.StatusCode(StatusCodes.Status403Forbidden),
                _ => TypedResults.Conflict(),
            };
        });

        return endpoints;
    }
}
