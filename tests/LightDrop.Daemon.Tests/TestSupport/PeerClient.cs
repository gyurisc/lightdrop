using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using LightDrop.Core;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using LightDrop.Core.Devices;
using LightDrop.Core.Pairing;
using LightDrop.Daemon.Pairing;

namespace LightDrop.Daemon.Tests.TestSupport;

/// <summary>
/// The test playing a device on the LAN: calls a daemon's peer listener presenting a chosen key.
/// </summary>
/// <remarks>
/// It accepts whatever server key answers -- these tests are about what the <em>listener</em>
/// admits, so the client side is deliberately not the thing under test. A new handler per call,
/// so every call is a new TLS handshake and the gate is evaluated afresh.
/// </remarks>
internal static class PeerClient
{
    public static async Task<HttpResponseMessage> GetAsync(DeviceKeyPair presenting, Uri listener, string path)
    {
        using var client = Create(presenting, listener);
        return await client.GetAsync(path);
    }

    public static async Task<HttpResponseMessage> HelloAsync(
        DeviceKeyPair presenting,
        Uri listener,
        string claimedDeviceId,
        int protocolVersion = LightDropVersion.Protocol)
    {
        using var client = Create(presenting, listener);
        return await client.PostAsJsonAsync(
            "pair/hello",
            new PairHelloRequest { DeviceId = claimedDeviceId, ProtocolVersion = protocolVersion },
            LightDropJsonContext.Default.PairHelloRequest);
    }

    public static async Task<HttpResponseMessage> RevealAsync(DeviceKeyPair presenting, Uri listener, byte[] nonce)
    {
        using var client = Create(presenting, listener);
        return await client.PostAsJsonAsync(
            "pair/reveal", new PairingNonce { Nonce = nonce }, LightDropJsonContext.Default.PairingNonce);
    }

    /// <summary>
    /// Plays an honest dialer up to the code: hello, then reveal. Returns everything needed to
    /// derive the code the listener is now showing.
    /// </summary>
    public static async Task<JoinedSession> JoinAsync(DeviceKeyPair presenting, Uri listener, string claimedDeviceId)
    {
        using var hello = await HelloAsync(presenting, listener, claimedDeviceId);
        Assert.Equal(HttpStatusCode.OK, hello.StatusCode);
        var answer = (await hello.Content.ReadFromJsonAsync(LightDropJsonContext.Default.PairHelloResponse))!;

        var dialerNonce = PairingCode.CreateNonce();
        using var reveal = await RevealAsync(presenting, listener, dialerNonce);
        Assert.Equal(HttpStatusCode.OK, reveal.StatusCode);
        var listenerNonce = (await reveal.Content.ReadFromJsonAsync(LightDropJsonContext.Default.PairingNonce))!.Nonce;

        return new JoinedSession(answer.Commitment, dialerNonce, listenerNonce);
    }

    public static async Task<HttpResponseMessage> ConfirmAsync(
        DeviceKeyPair presenting,
        Uri listener,
        bool accepted,
        CancellationToken cancellationToken = default)
    {
        using var client = Create(presenting, listener);
        return await client.PostAsJsonAsync(
            "pair/confirm",
            new PairingDecision { Accepted = accepted },
            LightDropJsonContext.Default.PairingDecision,
            cancellationToken);
    }

    /// <summary>Whether anything accepts a TCP connection on the peer port at all.</summary>
    public static async Task<bool> IsListeningAsync(DaemonEndpointOptions endpoint)
    {
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync("127.0.0.1", endpoint.PeerPort);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static HttpClient Create(DeviceKeyPair presenting, Uri listener) =>
        new(PairingTls.CreateCapturingHandler(presenting, new PeerKeyCapture()))
        {
            BaseAddress = listener,
            Timeout = TimeSpan.FromSeconds(30),
        };
}

/// <summary>What a test playing the dialer holds once the listener is showing its code.</summary>
internal sealed record JoinedSession(byte[] Commitment, byte[] DialerNonce, byte[] ListenerNonce);
