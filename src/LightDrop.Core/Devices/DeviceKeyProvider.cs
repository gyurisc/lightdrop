using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LightDrop.Core.Configuration;

namespace LightDrop.Core.Devices;

/// <summary>
/// Resolves this device's key pair, creating and persisting it on first use.
/// </summary>
/// <remarks>
/// Mirrors <see cref="DeviceIdentityProvider"/>: get-or-create logic in Core, file I/O behind
/// <see cref="IStateStore"/>, so it is testable without a filesystem. The private key lands in
/// <c>state.json</c>, which is created <c>0600</c> on Unix for exactly this reason.
/// <para>
/// <strong>Only the private key is persisted.</strong> The certificate is reissued from it on
/// every start. That is safe because pairing pins the public key rather than the certificate,
/// and it removes certificate expiry as something to track: a fresh certificate each run cannot
/// go stale.
/// </para>
/// </remarks>
public sealed class DeviceKeyProvider(IStateStore stateStore)
{
    /// <summary>
    /// How long an issued certificate stays valid.
    /// </summary>
    /// <remarks>
    /// Generous because it bounds nothing that matters — a daemon reissues on restart, and trust
    /// rests on the pinned key. It exists only because X.509 requires a validity window.
    /// </remarks>
    private static readonly TimeSpan CertificateLifetime = TimeSpan.FromDays(825);

    private readonly SemaphoreSlim _gate = new(1, 1);

    // volatile for the same reason as DeviceIdentityProvider: read on the fast path outside the
    // semaphore, and the CLI ships an arm64 RID.
    private volatile DeviceKeyPair? _cached;

    public async ValueTask<DeviceKeyPair> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            var state = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            if (string.IsNullOrWhiteSpace(state.DeviceKey))
            {
                var exported = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
                await stateStore.SaveAsync(state with { DeviceKey = exported }, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                Import(key, state.DeviceKey);
            }

            _cached = Issue(key);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads the persisted key, or fails loudly.
    /// </summary>
    /// <remarks>
    /// Deliberately fatal, mirroring a corrupt state file. Minting a replacement would revoke
    /// every pairing this device holds without telling anyone — the failure is silent precisely
    /// where it must not be. Both failure modes are collapsed into one exception because the
    /// caller cannot act differently on a bad base64 string than on a bad key.
    /// </remarks>
    private static void Import(ECDsa key, string persisted)
    {
        try
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(persisted), out _);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidOperationException(
                "LightDrop's stored device key is unreadable. This device cannot prove its " +
                "identity to peers it has already paired with. Move state.json aside to start " +
                "fresh, understanding that this device must then be paired again.",
                ex);
        }
    }

    private static DeviceKeyPair Issue(ECDsa key)
    {
        // The subject carries no identity claim. Nothing validates a name here: pairing compares
        // the public key, so anything asserted in the certificate is decoration.
        var request = new CertificateRequest("CN=LightDrop", key, HashAlgorithmName.SHA256);

        var now = DateTimeOffset.UtcNow;

        // Backdated an hour so a peer whose clock runs slightly behind does not reject a
        // certificate issued moments ago. Not disposed: it is handed to the caller and lives as
        // long as the process does.
        var certificate = request.CreateSelfSigned(now.AddHours(-1), now.Add(CertificateLifetime));

        return new DeviceKeyPair(certificate, key.ExportSubjectPublicKeyInfo());
    }
}
