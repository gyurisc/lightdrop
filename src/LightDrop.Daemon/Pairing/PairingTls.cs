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

        https.ServerCertificate = local.Certificate;

        // Required, not optional: a caller with no certificate has nothing to pin, so there is
        // nothing to decide about and the handshake should end here.
        https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;

        // Kestrel will not even call this unless the chain check is waived, and the chain can
        // never build for a self-signed peer certificate.
        https.AllowAnyClientCertificate();
        https.ClientCertificateValidation = (certificate, _, _) =>
            PinnedCertificate.Matches(certificate, expectedPeerPublicKeyInfo);
    }

    /// <summary>
    /// Creates a handler that presents this device's certificate and accepts only the pinned peer.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(DeviceKeyPair local, byte[] expectedPeerPublicKeyInfo)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(expectedPeerPublicKeyInfo);

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
                    && PinnedCertificate.Matches(presented, expectedPeerPublicKeyInfo),
            },
        };
    }
}
