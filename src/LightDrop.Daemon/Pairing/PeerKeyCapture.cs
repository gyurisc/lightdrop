using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LightDrop.Core.Pairing;

namespace LightDrop.Daemon.Pairing;

/// <summary>
/// The server key a pairing <c>hello</c> met, captured during the handshake.
/// </summary>
/// <remarks>
/// The first key presented is kept; any later handshake through the same handler must present the
/// same key or fail. See <see cref="PairingTls.CreateCapturingHandler"/>.
/// </remarks>
internal sealed class PeerKeyCapture
{
    private readonly Lock _gate = new();
    private byte[]? _key;

    /// <summary>The captured SubjectPublicKeyInfo, or null if no handshake got that far. A copy.</summary>
    public byte[]? Key
    {
        get
        {
            lock (_gate)
            {
                return (byte[]?)_key?.Clone();
            }
        }
    }

    /// <summary>
    /// Captures the presented key if none has been yet; otherwise whether it is the same key.
    /// </summary>
    public bool CaptureOrMatch(X509Certificate2 presented)
    {
        if (!PinnedCertificate.TryExportPublicKeyInfo(presented, out var presentedKey))
        {
            return false;
        }

        lock (_gate)
        {
            if (_key is null)
            {
                _key = presentedKey;
                return true;
            }

            return CryptographicOperations.FixedTimeEquals(_key, presentedKey);
        }
    }
}
