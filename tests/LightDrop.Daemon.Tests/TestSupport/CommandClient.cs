using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using LightDrop.Core.Contracts;
using LightDrop.Core.Devices;
using LightDrop.Daemon.Commands;
using LightDrop.Daemon.Pairing;

namespace LightDrop.Daemon.Tests.TestSupport;

/// <summary>
/// The test playing a paired (or unpaired) device that calls <c>POST /commands/{name}</c> on a
/// peer listener directly, with whatever metadata and body the test chooses.
/// </summary>
/// <remarks>
/// For what <c>/api/send</c> would never produce: a file name containing separators, a lying
/// length, a body that stops half way. Like <see cref="PeerClient"/>, it accepts whatever server
/// key answers -- the receiver's checks are under test here, not the sender's.
/// </remarks>
internal static class CommandClient
{
    public static async Task<HttpResponseMessage> SendFileAsync(
        DeviceKeyPair presenting,
        Uri listener,
        string fileName,
        byte[] body)
    {
        using var content = new ByteArrayContent(body);
        return await SendAsync(presenting, listener, "SendFile", Metadata(fileName, body.Length), content);
    }

    public static async Task<HttpResponseMessage> SendAsync(
        DeviceKeyPair presenting,
        Uri listener,
        string command,
        string? metadataHeader,
        HttpContent? content,
        CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient(PairingTls.CreateCapturingHandler(presenting, new PeerKeyCapture()))
        {
            BaseAddress = listener,
            Timeout = TimeSpan.FromSeconds(60),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"commands/{command}") { Content = content };
        if (metadataHeader is not null)
        {
            request.Headers.Add(CommandEndpoints.MetadataHeader, metadataHeader);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>The header value for a <c>SendFile</c>: base64url of the JSON metadata.</summary>
    public static string Metadata(string fileName, long length) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new SendFileMetadata { FileName = fileName, Length = length },
            LightDropJsonContext.Default.SendFileMetadata)));
}
