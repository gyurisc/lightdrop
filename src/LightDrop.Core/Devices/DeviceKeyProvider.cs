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
        // certificate issued moments ago. On macOS this exact object is handed to the caller and
        // lives as long as the process does; on Windows MakeUsableByTls disposes it and returns a
        // replacement built from its exported bytes, so the object handed to the caller differs by
        // platform even though the key it wraps does not.
        var certificate = request.CreateSelfSigned(now.AddHours(-1), now.Add(CertificateLifetime));

        return new DeviceKeyPair(MakeUsableByTls(certificate), key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>
    /// Returns a certificate whose private key <see cref="System.Net.Security.SslStream"/> will use.
    /// </summary>
    /// <remarks>
    /// On Windows, <see cref="CertificateRequest.CreateSelfSigned"/> attaches the private key as an
    /// ephemeral CNG key, and Schannel refuses to use it for a handshake, surfacing as
    /// <c>0x8009030E</c> ("No credentials are available in the security package") rather than
    /// anything naming the real cause. A PKCS#12 round trip materialises the key in a form
    /// Schannel accepts.
    /// <para>
    /// Deliberately <em>not</em> <see cref="X509KeyStorageFlags.EphemeralKeySet"/>: that flag looks
    /// like the obvious fix but keeps the key in-memory-only, which is the same condition Schannel
    /// was just refusing -- it does not fix this failure, it reproduces it.
    /// </para>
    /// <para>
    /// Also deliberately without <see cref="X509KeyStorageFlags.PersistKeySet"/>: without it,
    /// Windows drops the temporary CNG key container when the certificate is disposed. This runs
    /// once per daemon start, so persisting the key set would leak one container per restart.
    /// </para>
    /// Guarded rather than applied everywhere, matching how the state store guards
    /// <c>UnixCreateMode</c>: on macOS the original certificate already works, and the round trip
    /// would be pure cost. Verified on Windows by CI, which runs <c>dotnet test</c> on
    /// <c>windows-latest</c>; <c>PairingTlsTests</c> drives a real Kestrel HTTPS handshake using
    /// this exact certificate, so the Windows path is exercised on every push even though
    /// development happens on macOS and this was never run against a real handshake locally.
    /// </remarks>
    private static X509Certificate2 MakeUsableByTls(X509Certificate2 certificate)
    {
        if (!OperatingSystem.IsWindows())
        {
            return certificate;
        }

        using (certificate)
        {
            // No password: the bytes exist only for the length of this call and never touch disk.
            //
            // DefaultKeySet, not Exportable: nothing here ever exports this private key --
            // DeviceKeyPair exposes only the certificate and the *public* SPKI, and signing does
            // not require an exportable key. DefaultKeySet satisfies Schannel just as well -- the
            // problem this guards against was only ever EphemeralKeySet -- while leaving the
            // device's one genuinely secret, long-lived value non-exportable from its CNG
            // container. Free hardening with no cost to anything this type does.
            return X509CertificateLoader.LoadPkcs12(
                certificate.Export(X509ContentType.Pkcs12),
                password: null,
                X509KeyStorageFlags.DefaultKeySet);
        }
    }
}
