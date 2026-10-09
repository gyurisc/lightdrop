using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LightDrop.Core.Contracts;
using LightDrop.Core.Devices;
using LightDrop.Core.Discovery;
using LightDrop.Core.Pairing;
using LightDrop.Daemon.Commands;
using LightDrop.Daemon.Pairing;
using Microsoft.Extensions.Logging;

namespace LightDrop.Daemon.Transfers;

internal enum SendOutcome
{
    Sent,

    /// <summary>The request itself is unusable: no absolute path, no such file.</summary>
    Invalid,

    /// <summary>No paired device matches.</summary>
    NotPaired,

    /// <summary>More than one paired device matches.</summary>
    Ambiguous,

    /// <summary>Paired, but not on the network right now, so there is no address to dial.</summary>
    NotDiscovered,

    /// <summary>The connection failed, the key did not match, or the peer refused the file.</summary>
    Failed,
}

internal sealed record SendResult(SendOutcome Outcome, SendResponse? Response = null, string? Error = null);

/// <summary>
/// Sends one file to one paired device: the far side of <see cref="SendFileHandler"/>.
/// </summary>
/// <remarks>
/// <strong>Trust from the pin, address from discovery.</strong> The peer is chosen among
/// <em>paired</em> devices, and the connection must present the key pinned for it -- through
/// <see cref="PairingTls.CreateHandler(DeviceKeyPair, string)"/>, the existing mutation-tested
/// pinned client. Discovery only says where to dial, which is all an attacker-controlled
/// announcement can be trusted for: a stranger announcing the peer's id at its own address gets a
/// handshake it cannot finish.
/// <para>
/// <strong>Streamed, never buffered.</strong> The body is a <see cref="StreamContent"/> over the
/// open file with an explicit <c>Content-Length</c>, so <see cref="SocketsHttpHandler"/> writes it
/// straight through without chunking and without holding it in memory. No timeout but the
/// caller's cancellation: <see cref="HttpClient.Timeout"/>'s 100-second default would cut off any
/// file that takes longer, which on a LAN is a few gigabytes.
/// </para>
/// </remarks>
internal sealed class FileSender(
    PairingService pairing,
    PeerRegistry registry,
    DeviceKeyProvider keys,
    ILogger<FileSender> logger)
{
    /// <summary>The largest answer read back from the receiver; it is a file name or an error.</summary>
    private const int MaxResponseBytes = 4096;

    /// <summary>How much of the receiver's own words are shown to the user.</summary>
    private const int MaxPeerTextBytes = 512;

    public async Task<SendResult> SendAsync(string? path, string? peerQuery, CancellationToken cancellationToken)
    {
        if (!TryCheckPath(path, out var error))
        {
            return new SendResult(SendOutcome.Invalid, Error: error);
        }

        var trusted = await pairing.ListAsync(cancellationToken).ConfigureAwait(false);
        var lookup = PeerLookup.Find(peerQuery, trusted, peer => peer.DeviceId, peer => peer.DeviceName);

        switch (lookup.Outcome)
        {
            case PeerLookupOutcome.NotFound:
                return new SendResult(
                    SendOutcome.NotPaired,
                    Error: $"No paired device matches '{peerQuery}'. Run 'lightdrop peers --trusted' to list them, " +
                           "or 'lightdrop pair' to pair one.");

            case PeerLookupOutcome.Ambiguous:
                return new SendResult(
                    SendOutcome.Ambiguous,
                    Error: $"'{peerQuery}' matches more than one paired device: " +
                           $"{string.Join(", ", lookup.Candidates.Select(peer => $"{peer.DeviceName} ({ShortId(peer.DeviceId)})"))}. " +
                           "Use more of the device id.");
        }

        var target = lookup.Match!;

        // By the pinned id, never by name: a stranger can announce any name, including this one.
        var discovered = registry.GetPeers()
            .FirstOrDefault(peer => string.Equals(peer.DeviceId, target.DeviceId, StringComparison.Ordinal));

        if (discovered is null)
        {
            return new SendResult(
                SendOutcome.NotDiscovered,
                Error: $"{target.DeviceName} is paired but not on the network right now, so there is no address to send to. " +
                       $"Check that its daemon is running and that both devices are on the same network.");
        }

        return await SendToAsync(path, target.DeviceId, target.DeviceName, target.PublicKey, discovered, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SendResult> SendToAsync(
        string path,
        string peerId,
        string peerName,
        string pinnedKey,
        DiscoveredPeer discovered,
        CancellationToken cancellationToken)
    {
        var address = new Uri($"https://{discovered.Address}:{discovered.Port}/");
        var local = await keys.GetAsync(cancellationToken).ConfigureAwait(false);

        FileStream file;
        try
        {
            file = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SendResult(SendOutcome.Invalid, Error: $"Could not read {path}: {ex.Message}");
        }

        await using (file.ConfigureAwait(false))
        {
            var length = file.Length;
            var metadata = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(
                new SendFileMetadata { FileName = Path.GetFileName(path), Length = length },
                LightDropJsonContext.Default.SendFileMetadata));

            using var handler = PairingTls.CreateHandler(local, pinnedKey);
            using var client = new HttpClient(handler)
            {
                BaseAddress = address,
                Timeout = Timeout.InfiniteTimeSpan,
                MaxResponseContentBufferSize = MaxResponseBytes,
            };

            // Cut at the length declared below: a file another process is still appending to must
            // not send more than it said it would (see LengthLimitedStream).
            using var content = new StreamContent(new LengthLimitedStream(file, length));

            // Explicit, so the request carries Content-Length and is never chunked: the receiver
            // refuses a body whose length it cannot check up front.
            content.Headers.ContentLength = length;
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"commands/{SendFileHandler.CommandName}")
            {
                Content = content,
            };
            request.Headers.Add(CommandEndpoints.MetadataHeader, metadata);

            // A refusal decided from the headers alone -- a name that sanitizes to nothing -- then
            // arrives before the body is sent, instead of after gigabytes nobody will read.
            request.Headers.ExpectContinue = true;

            try
            {
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var refusal = await DescribeRefusalAsync(response, peerName, cancellationToken).ConfigureAwait(false);
                    TransferLog.SendFailed(logger, path, peerName, peerId, refusal, null);
                    return new SendResult(SendOutcome.Failed, Error: refusal);
                }

                var result = await response.Content
                    .ReadFromJsonAsync(LightDropJsonContext.Default.SendFileResult, cancellationToken)
                    .ConfigureAwait(false);

                // The receiver is paired, not harmless: what it says is printed to a terminal.
                var savedAs = UntrustedText.Sanitize(result?.SavedAs, MaxPeerTextBytes);
                TransferLog.Sent(logger, path, length, peerName, peerId, savedAs);

                return new SendResult(SendOutcome.Sent, new SendResponse
                {
                    SavedAs = savedAs,
                    Length = length,
                    PeerDeviceId = peerId,
                    PeerName = peerName,
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // TLS failures land here too, from either side: this end refusing a key that is not
                // the pinned one, or the far end refusing ours because it has unpaired this device.
                // The two cannot be told apart from here, so the message names both.
                //
                // The exception's own words are the receiver's words too: the HTTP stack quotes
                // the bytes of a malformed status line or header name in its message, escape
                // sequences included. Sanitized like everything else the receiver says, before
                // it reaches the log or the terminal.
                var detail = UntrustedText.Sanitize(Innermost(ex).Message, MaxPeerTextBytes);
                var reason =
                    $"Could not send to {peerName} at {discovered.Address}:{discovered.Port} ({detail}). " +
                    $"It may be offline or asleep, a firewall may be blocking that port, " +
                    $"or one of the two devices may have unpaired the other.";
                TransferLog.SendFailed(logger, path, peerName, peerId, reason, ex);
                return new SendResult(SendOutcome.Failed, Error: reason);
            }
        }
    }

    private static bool TryCheckPath([NotNullWhen(true)] string? path, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            // The daemon's working directory is not the user's; a relative path would resolve
            // somewhere they did not mean.
            error = "The file must be given as an absolute path.";
            return false;
        }

        if (Directory.Exists(path))
        {
            error = $"{path} is a folder. LightDrop sends one file at a time.";
            return false;
        }

        if (!File.Exists(path))
        {
            error = $"There is no file at {path}.";
            return false;
        }

        return true;
    }

    private static async Task<string> DescribeRefusalAsync(
        HttpResponseMessage response,
        string peerName,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return $"{peerName} does not accept files. It may be running an older version of LightDrop.";
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return $"{peerName} no longer trusts this device. Pair the two again.";
        }

        try
        {
            var error = await response.Content
                .ReadFromJsonAsync(LightDropJsonContext.Default.ApiError, cancellationToken)
                .ConfigureAwait(false);

            var said = UntrustedText.Sanitize(error?.Error, MaxPeerTextBytes);
            if (said.Length > 0)
            {
                return $"{peerName} refused the file: {said}";
            }
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or NotSupportedException)
        {
            // No usable explanation; the status code is all there is.
        }

        return $"{peerName} refused the file (HTTP {(int)response.StatusCode}).";
    }

    private static Exception Innermost(Exception exception)
    {
        while (exception.InnerException is not null)
        {
            exception = exception.InnerException;
        }

        return exception;
    }

    private static string ShortId(string deviceId) => deviceId.Length > 8 ? deviceId[..8] : deviceId;
}
