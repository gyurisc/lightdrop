using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using LightDrop.Core.Devices;

namespace LightDrop.Daemon.Tests.TestSupport;

/// <summary>
/// A paired "receiving device" the test scripts byte for byte, for exercising a real daemon's
/// sender.
/// </summary>
/// <remarks>
/// A raw <see cref="SslStream"/> rather than Kestrel, because the point is to answer in ways no
/// HTTP server would: a header name holding an escape sequence, a JSON body made to be printed to
/// a terminal. It presents a real device key, so a sender that pinned that key completes the
/// handshake -- this stands in for a paired peer that is hostile or compromised, which pairing
/// says nothing against. Loopback only. Accepts any client certificate; the sender's checks are
/// under test here, not the receiver's.
/// </remarks>
internal sealed class ScriptedReceiver : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private ScriptedReceiver(TcpListener listener, DeviceKeyPair key, Func<Exchange, Task> answer)
    {
        _listener = listener;
        _loop = Task.Run(() => AcceptAsync(key, answer));
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <param name="key">The key presented, so a sender that pinned it completes the handshake.</param>
    /// <param name="answer">Called once per request, with the request head already read.</param>
    public static ScriptedReceiver Start(DeviceKeyPair key, Func<Exchange, Task> answer)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new ScriptedReceiver(listener, key, answer);
    }

    /// <summary>A complete HTTP/1.1 response with a JSON body, closing the connection after it.</summary>
    public static string Json(int status, string json) =>
        $"HTTP/1.1 {status} Scripted\r\nContent-Type: application/json\r\n" +
        $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\nConnection: close\r\n\r\n{json}";

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();

        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopping is how the loop ends.
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync(DeviceKeyPair key, Func<Exchange, Task> answer)
    {
        while (!_stop.IsCancellationRequested)
        {
            using var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
            await using var tls = new SslStream(socket.GetStream());

            try
            {
                await tls.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = key.Certificate,
                        ClientCertificateRequired = true,
                        RemoteCertificateValidationCallback = (_, _, _, _) => true,
                    },
                    _stop.Token);

                await answer(new Exchange(tls, await ReadHeadAsync(tls, _stop.Token)));
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
            {
                // The sender hung up or refused us; the test asserts on what it saw, not on this.
            }
        }
    }

    /// <summary>The request line and headers, byte by byte so nothing of the body is consumed.</summary>
    private static async Task<string> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var head = new List<byte>();
        var one = new byte[1];

        while (head.Count < 4 || !(head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0)
            {
                throw new IOException("The sender closed the connection inside the request head.");
            }

            head.Add(one[0]);
        }

        return Encoding.Latin1.GetString([.. head]);
    }

    /// <summary>One request, answered however the test likes.</summary>
    internal sealed class Exchange(SslStream stream, string head)
    {
        public string Head => head;

        /// <summary>The request's declared <c>Content-Length</c>.</summary>
        public long ContentLength =>
            long.Parse(
                head.Split("\r\n")
                    .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    ["Content-Length:".Length..]
                    .Trim(),
                CultureInfo.InvariantCulture);

        /// <summary>Writes bytes exactly as given: Latin-1, so <c>\u001b</c> is the single byte ESC.</summary>
        public async Task WriteAsync(string raw)
        {
            await stream.WriteAsync(Encoding.Latin1.GetBytes(raw));
            await stream.FlushAsync();
        }

        /// <summary>Reads exactly <see cref="ContentLength"/> bytes of body.</summary>
        public async Task<byte[]> ReadBodyAsync()
        {
            var body = new byte[ContentLength];
            await stream.ReadExactlyAsync(body);
            return body;
        }
    }
}
