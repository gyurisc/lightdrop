using System.Security.Cryptography.X509Certificates;

namespace LightDrop.Core.Devices;

/// <summary>
/// This device's long-lived key, and the certificate that presents it.
/// </summary>
/// <remarks>
/// <strong>The key is the identity; the certificate is disposable.</strong> Pairing pins the
/// SubjectPublicKeyInfo, never the certificate, so the certificate can be reissued on every
/// start without invalidating anything — which is why there is no renewal or expiry story to
/// maintain.
/// </remarks>
public sealed class DeviceKeyPair(X509Certificate2 certificate, byte[] publicKeyInfo)
{
    private readonly byte[] _publicKeyInfo = publicKeyInfo;

    /// <summary>
    /// The self-signed certificate presented during a TLS handshake.
    /// </summary>
    /// <remarks>
    /// Lives for the lifetime of the process. Not disposed here: it is resolved once and handed
    /// to whatever opens a connection, so ownership stays with the provider that cached it.
    /// </remarks>
    public X509Certificate2 Certificate { get; } = certificate;

    /// <summary>
    /// The DER-encoded SubjectPublicKeyInfo — what pairing pins, and what the human-verifiable
    /// code is derived from.
    /// </summary>
    /// <remarks>
    /// Copied on every read. This is the value trust decisions are compared against, and handing
    /// out the live array would let any caller alter what every later comparison sees.
    /// </remarks>
    public byte[] PublicKeyInfo => (byte[])_publicKeyInfo.Clone();
}
