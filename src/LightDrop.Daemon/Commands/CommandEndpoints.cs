using System.Buffers.Text;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using LightDrop.Daemon.Peers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LightDrop.Daemon.Commands;

internal static class CommandEndpoints
{
    /// <summary>
    /// The request header carrying a command's metadata: JSON, base64url-encoded.
    /// </summary>
    /// <remarks>
    /// In a header so the body can be the payload's raw bytes, streamed -- no base64-in-JSON, no
    /// whole-file buffering. Always base64url rather than "JSON when it happens to be safe": a
    /// file name may hold any Unicode, and header values are not a place to find out which
    /// characters a given HTTP stack mangles.
    /// </remarks>
    public const string MetadataHeader = "LightDrop-Metadata";

    /// <summary>
    /// The longest metadata header accepted, encoded. Bounds every string in it -- a file name
    /// included -- before any of it is parsed.
    /// </summary>
    public const int MaxMetadataHeaderLength = 8192;

    /// <summary>
    /// Maps <c>POST /commands/{name}</c> on the peer listener, behind the pinned-peer check.
    /// </summary>
    /// <remarks>
    /// <strong>The check is on the group, not on a handler.</strong> While a pairing window is
    /// open the listener's handshake admits any certificate, so without it every command would be
    /// open to any stranger for the length of every window. On the group, a handler added later
    /// inherits it rather than having to remember it.
    /// </remarks>
    public static IEndpointRouteBuilder MapCommandEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/commands")
            .RequirePinnedPeer()
            .MapPost("/{name}", DispatchAsync);

        return endpoints;
    }

    /// <summary>Hands a request to the handler registered under <paramref name="name"/>.</summary>
    /// <remarks>
    /// Internal rather than private only so a test can call it without the filter in front.
    /// </remarks>
    internal static async Task<IResult> DispatchAsync(
        string name,
        HttpContext context,
        [FromServices] IEnumerable<ICommandHandler> handlers)
    {
        // RequirePinnedPeer leaves the matched peer here and refuses any request it cannot match.
        // Checked again rather than cast on trust: if the filter is ever lost from the group --
        // a refactor, this dispatcher mapped somewhere else -- the request is refused here, before
        // a handler writes a stranger's file to disk, instead of crashing after it has.
        if (context.Items[PinnedPeerFilter.ItemsKey] is not TrustedPeer peer)
        {
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);
        }

        // Ordinal: a command name is an identifier on the wire, not something a human typed.
        var handler = handlers.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (handler is null)
        {
            return TypedResults.NotFound();
        }

        if (!TryReadMetadata(context.Request, out var metadata))
        {
            return TypedResults.BadRequest(new ApiError { Error = $"The {MetadataHeader} header is not valid base64url." });
        }

        return await handler.HandleAsync(new CommandRequest(peer, metadata, context)).ConfigureAwait(false);
    }

    /// <summary>
    /// Decodes the metadata header. An absent header is null, not an error: whether a command
    /// needs metadata is its handler's business.
    /// </summary>
    private static bool TryReadMetadata(HttpRequest request, out byte[]? metadata)
    {
        metadata = null;

        if (!request.Headers.TryGetValue(MetadataHeader, out var values) || values.Count == 0)
        {
            return true;
        }

        // Exactly one: two values are either a mistake or an attempt to have one part of the
        // stack read a different one than another.
        if (values.Count > 1 || values[0] is not { } encoded || encoded.Length > MaxMetadataHeaderLength)
        {
            return false;
        }

        if (!Base64Url.IsValid(encoded, out var decodedLength))
        {
            return false;
        }

        metadata = new byte[decodedLength];
        return Base64Url.TryDecodeFromChars(encoded, metadata, out _);
    }
}
