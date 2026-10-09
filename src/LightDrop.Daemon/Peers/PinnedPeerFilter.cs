using LightDrop.Core.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LightDrop.Daemon.Peers;

/// <summary>
/// Admits a request to a route group only if its connection presented a pinned peer's key.
/// </summary>
/// <remarks>
/// <strong>Why a second check behind the TLS gate.</strong> While a pairing window is open the peer
/// listener's handshake admits any certificate -- it has to, the handshake being the only carrier
/// of the new peer's key. Without this, everything reachable on the listener would be reachable by
/// any stranger for the length of every window. Applied to a whole group so that a route added
/// later inherits it rather than having to remember it.
/// <para>
/// Applied to the <c>/commands</c> group (<see cref="Commands.CommandEndpoints"/>). Not to the
/// pairing routes: their callers cannot have a pin yet, and each checks the session's joined key
/// for itself instead.
/// </para>
/// <para>
/// <c>trustedPeers</c> is read per request, like the handshake gate, so an unpair applies at once.
/// The matched peer is left in <see cref="HttpContext.Items"/> for the handler.
/// </para>
/// </remarks>
internal static class PinnedPeerFilter
{
    /// <summary>The <see cref="HttpContext.Items"/> key holding the matched <c>TrustedPeer</c>.</summary>
    public static readonly object ItemsKey = new();

    public static TBuilder RequirePinnedPeer<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var certificate = http.Connection.ClientCertificate;

            if (certificate is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var pairing = http.RequestServices.GetRequiredService<PairingService>();
            var trusted = await pairing.ListAsync(http.RequestAborted).ConfigureAwait(false);

            var peer = trusted.FirstOrDefault(pinned => PinnedCertificate.Matches(certificate, pinned.PublicKey));
            if (peer is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            http.Items[ItemsKey] = peer;
            return await next(context).ConfigureAwait(false);
        });

        return builder;
    }
}
