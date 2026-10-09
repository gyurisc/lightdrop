using LightDrop.Core.Contracts;
using LightDrop.Daemon.Transfers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LightDrop.Daemon.Endpoints;

internal static class SendEndpoints
{
    /// <summary>
    /// Maps <c>POST /api/send</c> <c>{path, peer}</c>: sends a file to a paired device and answers
    /// once the other device has all of it.
    /// </summary>
    /// <remarks>
    /// Loopback only and behind the origin check, like every <c>/api</c> route: a web page cannot
    /// post here. Any process of this user's can, and can send any file this user can read -- to
    /// a device this user already paired, and nowhere else. Accepted for a personal tool; the
    /// alternative is a secret the CLI would have to find, which is a file any such process could
    /// read too.
    /// <para>
    /// Held open for the whole transfer. When the caller goes away -- Ctrl+C in
    /// <c>lightdrop send</c> -- the request is aborted, which cancels the upload, which makes the
    /// receiver delete what it had so far.
    /// </para>
    /// </remarks>
    public static IEndpointRouteBuilder MapSendEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/send", async Task<Results<Ok<SendResponse>, JsonHttpResult<ApiError>>> (
                [FromBody] SendRequest request,
                [FromServices] FileSender sender,
                CancellationToken cancellationToken) =>
        {
            var result = await sender.SendAsync(request.Path, request.Peer, cancellationToken).ConfigureAwait(false);

            if (result.Outcome == SendOutcome.Sent)
            {
                return TypedResults.Ok(result.Response!);
            }

            var status = result.Outcome switch
            {
                SendOutcome.Invalid or SendOutcome.Ambiguous => StatusCodes.Status400BadRequest,
                SendOutcome.NotPaired => StatusCodes.Status404NotFound,
                SendOutcome.NotDiscovered => StatusCodes.Status409Conflict,

                // The local daemon is fine; the device it talked to (or tried to) is not.
                _ => StatusCodes.Status502BadGateway,
            };

            return TypedResults.Json(new ApiError { Error = result.Error! }, LightDropJsonContext.Default.ApiError, statusCode: status);
        });

        return endpoints;
    }
}
