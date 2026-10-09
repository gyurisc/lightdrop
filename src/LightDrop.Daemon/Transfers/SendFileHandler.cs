using System.Text.Json;
using LightDrop.Core.Contracts;
using LightDrop.Core.Transfers;
using LightDrop.Daemon.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace LightDrop.Daemon.Transfers;

/// <summary>
/// <c>SendFile</c>: a paired peer gives this device one file, which lands in the download folder.
/// </summary>
/// <remarks>
/// The caller is a pinned peer by the time this runs, but everything it says is still checked as
/// if it were not: pairing says who it is, not that it is careful. The name is sanitized, the
/// length must be stated twice and agree, and the body is held to exactly that length.
/// </remarks>
internal sealed class SendFileHandler(DownloadFolder downloads, ILogger<SendFileHandler> logger) : ICommandHandler
{
    public const string CommandName = "SendFile";

    public string Name => CommandName;

    public async Task<IResult> HandleAsync(CommandRequest request)
    {
        var http = request.Http;

        if (!TryParse(request.Metadata, out var metadata))
        {
            return Refuse("SendFile needs metadata {fileName, length}.");
        }

        // Required, not inferred from a chunked body as it arrives: the declared length is what
        // the body limit is raised to, so it has to be known, and agreed, before the first byte.
        if (http.Request.ContentLength is not { } contentLength)
        {
            return TypedResults.StatusCode(StatusCodes.Status411LengthRequired);
        }

        if (metadata.Length < 0 || contentLength != metadata.Length)
        {
            return Refuse("Content-Length must equal the declared length.");
        }

        if (ReceivedFileName.Sanitize(metadata.FileName) is not { } fileName)
        {
            return Refuse("The file name is empty once made safe to save.");
        }

        // The peer listener holds every body to 4 KB, and Kestrel's own default is 30 MB. Raised
        // here, for this request only, to exactly the declared length -- not removed -- before
        // anything has read the body (after that the feature is read-only). Every other route on
        // the listener keeps the small limit.
        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is null || limit.IsReadOnly)
        {
            throw new InvalidOperationException("The request body size limit can no longer be raised for this request.");
        }

        limit.MaxRequestBodySize = metadata.Length;

        try
        {
            var (savedAs, folder) = await downloads
                .SaveAsync(fileName, http.Request.Body, metadata.Length, http.RequestAborted)
                .ConfigureAwait(false);

            TransferLog.Received(logger, savedAs, metadata.Length, request.Peer.DeviceName, request.Peer.DeviceId, folder);

            // The name only. The receiver's path -- its user name, its folder layout -- is none of
            // the sender's business.
            return TypedResults.Ok(new SendFileResult { SavedAs = savedAs });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // The sender gave up, the body was short, or the disk said no. Usually nobody is left
            // to read the answer; when somebody is, it learns nothing about this machine's paths.
            TransferLog.ReceiveFailed(logger, fileName, request.Peer.DeviceName, request.Peer.DeviceId, ex);

            return TypedResults.Json(
                new ApiError { Error = "The file was not saved." },
                LightDropJsonContext.Default.ApiError,
                statusCode: ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError);
        }
    }

    private static bool TryParse(byte[]? metadata, out SendFileMetadata parsed)
    {
        parsed = null!;
        if (metadata is null)
        {
            return false;
        }

        try
        {
            parsed = JsonSerializer.Deserialize(metadata, LightDropJsonContext.Default.SendFileMetadata)!;
            return parsed is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IResult Refuse(string error) => TypedResults.BadRequest(new ApiError { Error = error });
}
