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
/// <strong>This is not the pairing handshake.</strong> Both methods here require the peer's public
/// key up front, which is exactly what neither side has during the pairing window itself — the key
/// is never broadcast in mDNS, so the TLS handshake is the only way it could ever reach the other
/// side, and by then it is too late to have been supplied as a parameter. Pairing proper needs a
/// second mode that deferred-validates: accept whatever certificate is presented, capture its
/// SubjectPublicKeyInfo, and let the pairing session judge it after the humans compare six digits.
/// That mode does not exist yet — do not assume this class already covers it.
/// </para>
/// </remarks>
internal static class PairingTls
{
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

        return new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = [local.Certificate],

                // The name in the certificate is decoration -- nothing validates it -- but the
                // handshake still wants a target name to send.
                TargetHost = "lightdrop",

                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is X509Certificate2 presented
                    && PinnedCertificate.Matches(presented, pinned),
            },
        };
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

        return new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = [local.Certificate],
                TargetHost = "lightdrop",
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is X509Certificate2 presented
                    && PinnedCertificate.Matches(presented, expectedPeerPublicKeyInfoBase64),
            },
        };
    }
}
