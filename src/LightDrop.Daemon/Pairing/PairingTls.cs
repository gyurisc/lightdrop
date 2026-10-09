using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using LightDrop.Core.Devices;
using LightDrop.Core.Pairing;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace LightDrop.Daemon.Pairing;

/// <summary>
/// Both ends of a pairing connection: a listener that pins its caller, and a caller that pins the
/// listener.
/// </summary>
/// <remarks>
/// <strong>Default certificate validation is replaced, not disabled.</strong> Both sides present
/// self-signed certificates, so no chain will ever validate and the platform's verdict is useless
/// here. What replaces it must actually compare the pinned key — a callback that returns
/// <c>true</c> builds a handshake that trusts anyone, and it looks identical to one that works.
/// <para>
/// The TLS version is left to the platform rather than forced to 1.3. Security here rests on the
/// pinned key, not the protocol version: an attacker who cannot produce the pinned private key
/// fails the handshake at any version. Forcing 1.3 risked breaking one of the two platforms this
/// project targets, which would have been a worse trade.
/// </para>
/// <para>
/// <strong>Two modes.</strong> <see cref="ConfigureListener(HttpsConnectionAdapterOptions, DeviceKeyPair, byte[])"/>
/// and <see cref="CreateHandler(DeviceKeyPair, byte[])"/> require the peer's key up front: they
/// describe a connection <em>after</em> pairing. During the pairing window neither side has that
/// key — it is never broadcast in mDNS, so the TLS handshake is the only way it can arrive — so
/// <see cref="CreateServerOptions"/> and <see cref="CreateCapturingHandler"/> defer the decision:
/// the server side asks a predicate the caller supplies, and the dialing side accepts whatever key
/// answers first, captures it, and refuses any different key for the rest of that handler's life.
/// The pairing session judges the captured key after the humans compare six digits.
/// </para>
/// </remarks>
internal static class PairingTls
{
    /// <summary>
    /// How long a dial to a peer may take, TCP connect and TLS handshake together. A device on
    /// the same network answers in milliseconds; this is for one that never will.
    /// </summary>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Server-side TLS options for one inbound connection, presenting this device's certificate and
    /// requiring the caller's, accepted only if <paramref name="accepts"/> says so.
    /// </summary>
    /// <remarks>
    /// Built per connection (Kestrel's <c>TlsHandshakeCallbackOptions</c>) rather than once per
    /// listener, so the predicate can close over the trusted peers as they are <em>now</em>: an
    /// unpair takes effect for the next connection with no restart and no cache to invalidate.
    /// <para>
    /// The predicate is the whole trust decision for the peer listener. It never sees a null:
    /// a caller that presents no certificate is refused here first.
    /// </para>
    /// </remarks>
    public static SslServerAuthenticationOptions CreateServerOptions(
        DeviceKeyPair local,
        Func<X509Certificate2, bool> accepts)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(accepts);

        return new SslServerAuthenticationOptions
        {
            ServerCertificate = local.Certificate,

            // The callback-path equivalent of ClientCertificateMode.RequireCertificate. Without
            // it the client is never asked for a certificate, and a caller with none would be
            // judged on nothing.
            ClientCertificateRequired = true,

            // Replaces the platform's chain verdict outright, as in ConfigureListener: a
            // self-signed peer certificate never chains, so that verdict means nothing here.
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is X509Certificate2 presented && accepts(presented),
        };
    }

    /// <summary>
    /// Creates a handler for the pairing window's <c>hello</c>: it presents this device's
    /// certificate, accepts the first server key it meets, and records it in
    /// <paramref name="capture"/>.
    /// </summary>
    /// <remarks>
    /// "First key wins" rather than "any key": should this handler ever open a second connection,
    /// it refuses a key different from the one it already captured, so a response this handler
    /// returns provably came from the captured key. Everything after <c>hello</c> must then go
    /// through <see cref="CreateHandler(DeviceKeyPair, byte[])"/> pinned to that key — this handler
    /// is for discovering a key, never for trusting one.
    /// </remarks>
    public static SocketsHttpHandler CreateCapturingHandler(DeviceKeyPair local, PeerKeyCapture capture)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(capture);

        return CreatePeerHandler(new SslClientAuthenticationOptions
        {
            ClientCertificates = [local.Certificate],
            TargetHost = "lightdrop",
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is X509Certificate2 presented && capture.CaptureOrMatch(presented),
        });
    }

    /// <summary>
    /// Configures a listener to present this device's certificate and to require, and pin, the
    /// caller's.
    /// </summary>
    public static void ConfigureListener(
        HttpsConnectionAdapterOptions https,
        DeviceKeyPair local,
        byte[] expectedPeerPublicKeyInfo)
    {
        ArgumentNullException.ThrowIfNull(https);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(expectedPeerPublicKeyInfo);

        // Cloned, not captured by reference: this array is closed over for the life of the
        // listener, and this is the actual trust decision -- letting the caller keep a live
        // reference would let them alter what every future handshake is compared against, the
        // same reasoning DeviceKeyPair.PublicKeyInfo already documents for its own clone-on-read.
        var pinned = (byte[])expectedPeerPublicKeyInfo.Clone();

        https.ServerCertificate = local.Certificate;

        // Required, not optional: a caller with no certificate has nothing to pin, so there is
        // nothing to decide about and the handshake should end here.
        https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;

        // A non-null ClientCertificateValidation replaces Kestrel's default chain verdict
        // entirely -- it is not additive with it, and needs no separate waiver call. That is what
        // we want here: the chain can never build for a self-signed peer certificate, so the only
        // verdict that matters is this one.
        https.ClientCertificateValidation = (certificate, _, _) =>
            PinnedCertificate.Matches(certificate, pinned);
    }

    /// <summary>
    /// Configures a listener to present this device's certificate and to require, and pin, the
    /// caller's, given the base64 pin stored in <c>state.json</c>.
    /// </summary>
    /// <remarks>
    /// Routes to <see cref="PinnedCertificate.Matches(X509Certificate2, string)"/> rather than
    /// decoding here: a corrupt stored pin must fail the handshake, not throw from inside the TLS
    /// callback that the byte-array overload's caller would otherwise have to build by hand.
    /// </remarks>
    public static void ConfigureListener(
        HttpsConnectionAdapterOptions https,
        DeviceKeyPair local,
        string expectedPeerPublicKeyInfoBase64)
    {
        ArgumentNullException.ThrowIfNull(https);
        ArgumentNullException.ThrowIfNull(local);

        https.ServerCertificate = local.Certificate;
        https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
        https.ClientCertificateValidation = (certificate, _, _) =>
            PinnedCertificate.Matches(certificate, expectedPeerPublicKeyInfoBase64);
    }

    /// <summary>
    /// Creates a handler that presents this device's certificate and accepts only the pinned peer.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(DeviceKeyPair local, byte[] expectedPeerPublicKeyInfo)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(expectedPeerPublicKeyInfo);

        // Cloned for the same reason as ConfigureListener above: this closure is the trust
        // decision for every request this handler ever makes, and must not be alterable through a
        // reference the caller kept.
        var pinned = (byte[])expectedPeerPublicKeyInfo.Clone();

        return CreatePeerHandler(new SslClientAuthenticationOptions
        {
            ClientCertificates = [local.Certificate],

            // The name in the certificate is decoration -- nothing validates it -- but the
            // handshake still wants a target name to send.
            TargetHost = "lightdrop",

            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is X509Certificate2 presented
                && PinnedCertificate.Matches(presented, pinned),
        });
    }

    /// <summary>
    /// Creates a handler that presents this device's certificate and accepts only the pinned peer,
    /// given the base64 pin stored in <c>state.json</c>.
    /// </summary>
    /// <remarks>
    /// See the byte-array overload's sibling <see cref="ConfigureListener(HttpsConnectionAdapterOptions, DeviceKeyPair, string)"/>:
    /// routing through <see cref="PinnedCertificate.Matches(X509Certificate2, string)"/> keeps a
    /// corrupt stored pin from throwing inside <see cref="SslClientAuthenticationOptions.RemoteCertificateValidationCallback"/>.
    /// </remarks>
    public static SocketsHttpHandler CreateHandler(DeviceKeyPair local, string expectedPeerPublicKeyInfoBase64)
    {
        ArgumentNullException.ThrowIfNull(local);

        return CreatePeerHandler(new SslClientAuthenticationOptions
        {
            ClientCertificates = [local.Certificate],
            TargetHost = "lightdrop",
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is X509Certificate2 presented
                && PinnedCertificate.Matches(presented, expectedPeerPublicKeyInfoBase64),
        });
    }

    /// <summary>
    /// The one place a handler for dialing a peer is built, so every dial gets the same two
    /// settings that are not about trust but would undo "local network only" if missed.
    /// </summary>
    /// <remarks>
    /// <strong>Never through a proxy.</strong> A <see cref="SocketsHttpHandler"/> obeys the system
    /// proxy, a PAC file and <c>HTTPS_PROXY</c> by default, and Windows' "bypass for local
    /// addresses" covers only dotless host names, not <c>192.168.x.y</c>. A peer dialed through one
    /// becomes a <c>CONNECT</c> to a third party, which usually cannot reach the home network --
    /// the send fails, blaming the peer -- and when it can, relays a transfer that must stay on
    /// the LAN. The pin would still hold end to end; the peer's address and the transfer's size
    /// and timing would not stay local.
    /// <para>
    /// <strong>A bounded dial, an unbounded transfer.</strong> <see cref="ConnectTimeout"/> covers
    /// the TCP connect and the TLS handshake only. A device that slept or left the network stays
    /// in the discovery registry for minutes, and a firewall dropping the port answers nothing;
    /// without it a send would wait out the OS connect timeout, 75 seconds on macOS -- or for ever
    /// against something that accepts the connection and never speaks TLS. The transfer after it
    /// keeps no timeout but the caller's cancellation, as a multi-gigabyte file needs.
    /// </para>
    /// </remarks>
    private static SocketsHttpHandler CreatePeerHandler(SslClientAuthenticationOptions ssl) => new()
    {
        SslOptions = ssl,
        UseProxy = false,
        ConnectTimeout = ConnectTimeout,
    };
}
