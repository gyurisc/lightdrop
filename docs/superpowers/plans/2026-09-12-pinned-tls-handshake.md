# Pinned TLS Handshake Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two LightDrop daemons complete a mutual-TLS handshake over loopback that succeeds only when each side presents the public key the other has pinned.

**Architecture:** The existing `DeviceKeyProvider` already issues a self-signed ECDSA P-256 certificate from a persisted key. This plan adds the comparison that makes that certificate mean something — a constant-time SPKI match in Core — and wires it into both ends of a TLS connection: a Kestrel HTTPS listener that requires and pins a client certificate, and an `HttpClient` that presents one and pins the server's. Nothing else from M2 step 2 is in scope.

**Tech Stack:** .NET 10, Kestrel HTTPS (`UseHttps` + `ClientCertificateValidation`), `SocketsHttpHandler.SslOptions`, xUnit. No new dependencies.

**Spec:** `docs/superpowers/specs/2026-08-16-m2-secure-pairing-design.md` — decision 4 ("Mutual TLS 1.3 with self-signed certificates, pinned at pairing") is what this implements. Decisions 1-3 and the pairing ceremony are explicitly out of scope.

## Global Constraints

- `dotnet build LightDrop.sln` must stay at **0 warnings**. Warnings are errors in this solution.
- All three projects set `IsAotCompatible`. Trim and AOT violations are build errors. Certificate and TLS code is reflection-adjacent — the trimmed publish check in Task 4 is not optional.
- `LightDrop.Core` stays platform independent: no ASP.NET Core, no filesystem, no OS-specific APIs. `System.Security.Cryptography.X509Certificates` is already used there (`DeviceKeyPair`) and is allowed.
- `LightDrop.Daemon` is a class library. Never give it `OutputType=Exe`.
- **The daemon's main Kestrel endpoint stays bound to loopback on plain HTTP.** This plan adds a *second, separate* HTTPS listener, also on loopback, used only by tests. Binding anything to the LAN is a later slice and is out of scope here.
- Every daemon test passes an explicit data directory (`TempDataDirectory`) and a `NoOpPeerDiscoveryTransport`, or it touches the real user profile and opens real multicast sockets.
- **Never invent cryptography.** This plan writes a byte comparison and configures platform TLS. If a step seems to call for anything more than that, stop.
- Use `[LoggerMessage]` source-generated logging if any logging is added.
- Comment style explains *why*, not *what*, and documents rejected alternatives.

## A decision this plan makes, and the reason

**TLS version is not pinned to 1.3.** The M2 spec says TLS 1.3, but .NET's TLS on macOS goes through the platform stack and 1.3 availability there is less certain than on Windows. Forcing `SslProtocols.Tls13` risks a handshake that works on one of the two machines this project actually targets.

So: leave `EnabledSslProtocols` unset and let each platform negotiate its best, and add a test asserting the negotiated protocol is **at least TLS 1.2**. Task 3 records what each platform actually negotiated. If both negotiate 1.3, a later slice can tighten the floor with evidence; if macOS lands on 1.2, that is a decision to record rather than a failure.

This is safe because the security here does not rest on the TLS version. It rests on the pinned key — an attacker who cannot produce the pinned private key fails the handshake at any version ≥ 1.2.

---

### Task 1: Compare a presented certificate against a pinned key

**Files:**
- Create: `src/LightDrop.Core/Pairing/PinnedCertificate.cs`
- Test: `tests/LightDrop.Core.Tests/PinnedCertificateTests.cs`

**Interfaces:**
- Consumes: `DeviceKeyPair.PublicKeyInfo` (a `byte[]`, the DER SubjectPublicKeyInfo) from `LightDrop.Core.Devices`; `TrustedPeer.PublicKey` (base64 of the same) from `LightDrop.Core.Configuration`.
- Produces: `PinnedCertificate.Matches(X509Certificate2 presented, ReadOnlySpan<byte> pinnedPublicKeyInfo) -> bool` and `PinnedCertificate.Matches(X509Certificate2 presented, string pinnedPublicKeyInfoBase64) -> bool`.

- [ ] **Step 1: Write the failing tests**

Create `tests/LightDrop.Core.Tests/PinnedCertificateTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LightDrop.Core.Pairing;

namespace LightDrop.Core.Tests;

/// <summary>
/// Deciding whether the certificate on the wire belongs to the peer we paired with.
/// </summary>
/// <remarks>
/// This is the whole of LightDrop's trust decision. The certificate itself proves nothing — both
/// sides are self-signed and no authority vouches for either — so the only question that matters
/// is whether the key inside it is the key that was pinned when two humans compared six digits.
/// </remarks>
public sealed class PinnedCertificateTests
{
    [Fact]
    public void MatchesTheCertificateHoldingThePinnedKey()
    {
        using var certificate = CreateCertificate();

        Assert.True(PinnedCertificate.Matches(certificate, certificate.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void RejectsACertificateHoldingADifferentKey()
    {
        // The attack: anyone can mint a self-signed certificate saying anything at all. Only the
        // pinned key distinguishes the real peer from someone who did exactly that.
        using var pinned = CreateCertificate();
        using var impostor = CreateCertificate();

        Assert.False(PinnedCertificate.Matches(impostor, pinned.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void RejectsACertificateWhoseKeyDiffersByOneByte()
    {
        using var certificate = CreateCertificate();
        var tampered = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        tampered[^1] ^= 0x01;

        Assert.False(PinnedCertificate.Matches(certificate, tampered));
    }

    [Fact]
    public void RejectsAnEmptyPin()
    {
        // An empty pin must never be treated as "no constraint". A trusted peer with no key is a
        // bug elsewhere, and this must fail closed rather than accept anyone.
        using var certificate = CreateCertificate();

        Assert.False(PinnedCertificate.Matches(certificate, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void MatchesABase64Pin()
    {
        // The form actually stored in state.json.
        using var certificate = CreateCertificate();
        var pinned = Convert.ToBase64String(certificate.PublicKey.ExportSubjectPublicKeyInfo());

        Assert.True(PinnedCertificate.Matches(certificate, pinned));
    }

    [Fact]
    public void RejectsAMalformedBase64Pin()
    {
        // A corrupt state file must fail the handshake, not crash it.
        using var certificate = CreateCertificate();

        Assert.False(PinnedCertificate.Matches(certificate, "not base64 at all!!"));
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=LightDrop", key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddHours(-1), now.AddDays(1));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LightDrop.Core.Tests --filter "FullyQualifiedName~PinnedCertificateTests"`

Expected: a compile error that `PinnedCertificate` does not exist. Create the file with both methods as `throw new NotImplementedException();`, re-run, and confirm all 6 tests fail with `NotImplementedException` before writing the real implementation.

- [ ] **Step 3: Write the implementation**

Create `src/LightDrop.Core/Pairing/PinnedCertificate.cs`:

```csharp
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LightDrop.Core.Pairing;

/// <summary>
/// Whether a certificate presented during a TLS handshake carries the key that was pinned.
/// </summary>
/// <remarks>
/// <strong>This is the entire trust decision.</strong> Both devices present self-signed
/// certificates that no authority vouches for, so nothing in the certificate itself — its subject,
/// its issuer, its validity dates — carries any weight. Anyone on the network can mint one saying
/// whatever they like. The only question is whether the public key inside matches the one recorded
/// when two people compared six digits.
/// <para>
/// That is also why the certificate can be reissued on every daemon start: pairing pins the key,
/// never the certificate wrapped around it.
/// </para>
/// </remarks>
public static class PinnedCertificate
{
    /// <summary>
    /// Whether <paramref name="presented"/> carries the pinned key.
    /// </summary>
    /// <remarks>
    /// Constant-time, because this runs against a certificate an attacker chose and a timing
    /// signal would leak how much of a pinned key a guess had right. An empty pin fails closed:
    /// a trusted peer with no key is a bug elsewhere, and treating it as "no constraint" would
    /// turn that bug into an open door.
    /// </remarks>
    public static bool Matches(X509Certificate2 presented, ReadOnlySpan<byte> pinnedPublicKeyInfo)
    {
        ArgumentNullException.ThrowIfNull(presented);

        if (pinnedPublicKeyInfo.IsEmpty)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            presented.PublicKey.ExportSubjectPublicKeyInfo(),
            pinnedPublicKeyInfo);
    }

    /// <summary>
    /// Whether <paramref name="presented"/> carries the pinned key, given the base64 form stored
    /// in <c>state.json</c>.
    /// </summary>
    /// <remarks>
    /// A malformed pin returns false rather than throwing. A corrupt state file should fail the
    /// handshake — which the user can act on — not tear down the connection with an exception from
    /// inside a TLS callback, where the real cause would be invisible.
    /// </remarks>
    public static bool Matches(X509Certificate2 presented, string pinnedPublicKeyInfoBase64)
    {
        if (string.IsNullOrWhiteSpace(pinnedPublicKeyInfoBase64))
        {
            return false;
        }

        Span<byte> pinned = stackalloc byte[pinnedPublicKeyInfoBase64.Length];
        if (!Convert.TryFromBase64String(pinnedPublicKeyInfoBase64, pinned, out var written))
        {
            return false;
        }

        return Matches(presented, pinned[..written]);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/LightDrop.Core.Tests --filter "FullyQualifiedName~PinnedCertificateTests"`

Expected: PASS, 6 tests.

- [ ] **Step 5: Run the full suite and check the build**

Run: `dotnet build LightDrop.sln && dotnet test LightDrop.sln`

Expected: `Build succeeded.` with 0 warnings, all tests passing.

- [ ] **Step 6: Commit**

```bash
git add src/LightDrop.Core/Pairing/PinnedCertificate.cs tests/LightDrop.Core.Tests/PinnedCertificateTests.cs
git commit -m "feat: compare a presented certificate against a pinned key

The whole of LightDrop's trust decision, in one comparison. Both devices present
self-signed certificates that no authority vouches for, so nothing in the
certificate itself carries weight -- anyone on the network can mint one saying
whatever they like. The only question is whether the key inside matches the one
recorded when two people compared six digits.

Constant-time, because it runs against a certificate an attacker chose. An empty
or malformed pin fails closed rather than throwing: a corrupt state file should
fail the handshake, which the user can act on, not raise from inside a TLS
callback where the cause would be invisible."
```

---

### Task 2: Make the certificate usable by SslStream on Windows

**Files:**
- Modify: `src/LightDrop.Core/Devices/DeviceKeyProvider.cs` (the `Issue` method)
- Test: `tests/LightDrop.Core.Tests/DeviceKeyProviderTests.cs` (add one test)

**Interfaces:**
- Consumes: nothing new.
- Produces: no signature change. `DeviceKeyPair.Certificate` gains the property that its private key is usable by `SslStream` on Windows.

- [ ] **Step 1: Understand the problem before changing anything**

`CertificateRequest.CreateSelfSigned` returns a certificate whose private key is attached as an ephemeral CNG key on Windows. `SslStream` — and therefore Kestrel and `HttpClient` — can fail to use such a key, with an error that does not name the cause. The standard remedy is a PKCS#12 export/import round trip, which materialises the key in a form the TLS stack accepts.

This was flagged in commit `7eff527`'s message as something that "surfaces when the pairing listener lands". It has landed.

You are on macOS, where this is not reproducible. Do not try to reproduce it — implement the round trip behind an `OperatingSystem.IsWindows()` guard, mirroring how `JsonStateStore` guards `UnixCreateMode`, and verify on macOS that the guarded code changes nothing.

- [ ] **Step 2: Write the failing test**

Add to `tests/LightDrop.Core.Tests/DeviceKeyProviderTests.cs`:

```csharp
    [Fact]
    public async Task IssuesACertificateWhosePrivateKeySignsAndVerifies()
    {
        // The property TLS actually needs. A certificate can carry a private key handle that looks
        // present and still be unusable by SslStream -- on Windows an ephemeral CNG key from
        // CreateSelfSigned is exactly that. Signing with it here is the closest platform-neutral
        // proxy for "a TLS handshake can use this".
        var keyPair = await new DeviceKeyProvider(new InMemoryStateStore()).GetAsync(CancellationToken.None);

        using var privateKey = keyPair.Certificate.GetECDsaPrivateKey();
        Assert.NotNull(privateKey);

        var payload = "lightdrop"u8.ToArray();
        var signature = privateKey.SignData(payload, HashAlgorithmName.SHA256);

        using var publicKey = keyPair.Certificate.GetECDsaPublicKey();
        Assert.NotNull(publicKey);
        Assert.True(publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256));
    }
```

- [ ] **Step 3: Run it**

Run: `dotnet test tests/LightDrop.Core.Tests --filter "FullyQualifiedName~DeviceKeyProviderTests"`

Expected on macOS: **PASS**, because the defect is Windows-only. That is the honest outcome — record it in your report. This test is a regression guard for the property the round trip protects, not a red-green cycle you can drive on this machine.

- [ ] **Step 4: Add the Windows round trip**

In `src/LightDrop.Core/Devices/DeviceKeyProvider.cs`, replace the final two lines of `Issue` (`var certificate = request.CreateSelfSigned(...)` through `return new DeviceKeyPair(...)`) with:

```csharp
        var certificate = request.CreateSelfSigned(now.AddHours(-1), now.Add(CertificateLifetime));

        return new DeviceKeyPair(MakeUsableByTls(certificate), key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>
    /// Returns a certificate whose private key <see cref="System.Net.Security.SslStream"/> will use.
    /// </summary>
    /// <remarks>
    /// On Windows, <see cref="CertificateRequest.CreateSelfSigned"/> attaches the private key as an
    /// ephemeral CNG key, and the TLS stack can refuse it with an error that does not name the
    /// cause. A PKCS#12 round trip materialises the key in a form it accepts. Guarded rather than
    /// applied everywhere, matching how the state store guards <c>UnixCreateMode</c>: on macOS the
    /// original certificate already works, and the round trip would be pure cost.
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
            return X509CertificateLoader.LoadPkcs12(
                certificate.Export(X509ContentType.Pkcs12),
                password: null,
                X509KeyStorageFlags.EphemeralKeySet);
        }
    }
```

Add `using System.Security.Cryptography.X509Certificates;` if it is not already present (it is).

- [ ] **Step 5: Verify nothing changed on macOS**

Run: `dotnet test LightDrop.sln`

Expected: all tests pass, including the existing `ReusesThePersistedKeyAcrossRestarts` and `IssuesACertificateHoldingThatKey`. The guard means macOS takes the early return, so behaviour is unchanged there — that is what you are confirming.

If `X509KeyStorageFlags.EphemeralKeySet` causes trouble on Windows later, the fallback is `X509KeyStorageFlags.Exportable`; note that in your report but do not change it now, since you cannot test the Windows path.

- [ ] **Step 6: Commit**

```bash
git add src/LightDrop.Core/Devices/DeviceKeyProvider.cs tests/LightDrop.Core.Tests/DeviceKeyProviderTests.cs
git commit -m "fix: make the device certificate usable by SslStream on Windows

CreateSelfSigned attaches the private key as an ephemeral CNG key on Windows, and
the TLS stack can refuse it with an error that does not name the cause. A PKCS#12
round trip materialises it in a form SslStream accepts. Flagged in 7eff527 as
something that would surface when the pairing listener landed; it has landed.

Guarded by OperatingSystem.IsWindows rather than applied everywhere, matching how
the state store guards UnixCreateMode -- on macOS the original certificate already
works and the round trip would be pure cost.

The new test signs and verifies with the certificate's own key, which is the
closest platform-neutral proxy for \"a TLS handshake can use this\". It passes on
macOS before and after, because the defect is Windows-only; it is a regression
guard, not evidence the fix works. The Windows path is unverified until someone
runs the suite there."
```

---

### Task 3: A mutual-TLS handshake that only a pinned peer survives

**Files:**
- Create: `src/LightDrop.Daemon/Pairing/PairingTls.cs`
- Test: `tests/LightDrop.Daemon.Tests/PairingTlsTests.cs`

**Interfaces:**
- Consumes: `PinnedCertificate.Matches` (Task 1); `DeviceKeyPair` from `LightDrop.Core.Devices`.
- Produces:
  - `PairingTls.ConfigureListener(HttpsConnectionAdapterOptions https, DeviceKeyPair local, byte[] expectedPeerPublicKeyInfo) -> void`
  - `PairingTls.CreateHandler(DeviceKeyPair local, byte[] expectedPeerPublicKeyInfo) -> SocketsHttpHandler`

- [ ] **Step 1: Write the helper**

Create `src/LightDrop.Daemon/Pairing/PairingTls.cs`:

```csharp
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
```

- [ ] **Step 2: Write the test fixture and helper**

Three key pairs are needed, and generating them is the slow part, so they are made once per class
through a fixture. They come from the real `DeviceKeyProvider` over a real `JsonStateStore` rather
than from hand-built certificates — the point of Task 2 was that the *production* certificate must
work with `SslStream`, and a test using a differently-built one would not show that.

Create `tests/LightDrop.Daemon.Tests/TestSupport/PairingKeys.cs`:

```csharp
using LightDrop.Core.Devices;
using LightDrop.Daemon.Infrastructure;
using Microsoft.Extensions.Options;

namespace LightDrop.Daemon.Tests.TestSupport;

/// <summary>
/// Three device key pairs, made once and shared by every test in a class.
/// </summary>
/// <remarks>
/// Generated through the real provider and a real state file, not hand-built certificates. The
/// certificate the provider issues is the one production presents, including the Windows PKCS#12
/// round trip — a test using a differently-built certificate could pass while the real one failed
/// the handshake.
/// </remarks>
internal sealed class PairingKeys : IAsyncLifetime
{
    private readonly List<TempDataDirectory> _directories = [];

    public DeviceKeyPair Alice { get; private set; } = null!;

    public DeviceKeyPair Bob { get; private set; } = null!;

    /// <summary>A third, valid device that neither of the others has pinned.</summary>
    public DeviceKeyPair Impostor { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Alice = await CreateAsync();
        Bob = await CreateAsync();
        Impostor = await CreateAsync();
    }

    public Task DisposeAsync()
    {
        foreach (var directory in _directories)
        {
            directory.Dispose();
        }

        return Task.CompletedTask;
    }

    private async Task<DeviceKeyPair> CreateAsync()
    {
        var directory = new TempDataDirectory();
        _directories.Add(directory);

        var store = new JsonStateStore(
            Options.Create(new StorageOptions { DataDirectory = directory.FullPath }));

        return await new DeviceKeyProvider(store).GetAsync(CancellationToken.None);
    }
}
```

Create `tests/LightDrop.Daemon.Tests/TestSupport/PinnedListener.cs`:

```csharp
using System.Net;
using LightDrop.Core.Devices;
using LightDrop.Daemon.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LightDrop.Daemon.Tests.TestSupport;

/// <summary>
/// A real Kestrel HTTPS listener on loopback that pins the certificate its caller presents.
/// </summary>
/// <remarks>
/// Loopback, and only ever started by a test. This slice adds no LAN-reachable listener — that is
/// a later decision, made where the pairing window is.
/// </remarks>
internal sealed class PinnedListener : IAsyncDisposable
{
    private readonly WebApplication _app;

    private PinnedListener(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    public Uri Address { get; }

    public static async Task<PinnedListener> StartAsync(DeviceKeyPair local, byte[] expectedPeerPublicKeyInfo)
    {
        var port = FreeTcpPort.Get();
        var builder = WebApplication.CreateSlimBuilder();

        // Two of these tests deliberately fail a handshake, and Kestrel logs each failure. Without
        // this the suite's output is dominated by warnings from its passing tests.
        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, port, listen =>
                listen.UseHttps(https => PairingTls.ConfigureListener(https, local, expectedPeerPublicKeyInfo))));

        var app = builder.Build();

        app.MapGet("/pair/hello", string () => "lightdrop");

        app.MapGet("/pair/protocol", string (HttpContext context) =>
            context.Features.Get<ITlsHandshakeFeature>()?.Protocol.ToString() ?? "None");

        await app.StartAsync();

        return new PinnedListener(app, new Uri($"https://127.0.0.1:{port}/"));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
```

- [ ] **Step 2b: Write the failing tests**

Create `tests/LightDrop.Daemon.Tests/PairingTlsTests.cs`:

```csharp
using System.Net.Security;
using System.Security.Authentication;
using LightDrop.Daemon.Pairing;
using LightDrop.Daemon.Tests.TestSupport;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// A TLS connection that only a pinned peer survives.
/// </summary>
/// <remarks>
/// The failure this guards against is silent: a validation callback that returns <c>true</c>
/// builds a handshake trusting anyone, and an "it connects" test looks identical either way. So
/// the tests that matter here are the two rejections, not the acceptance — and Step 3 verifies
/// them by mutation rather than trusting that they pass for the right reason.
/// <para>
/// Two real key pairs, a real Kestrel HTTPS listener on loopback, a real HttpClient. Nothing is
/// mocked, because what is under test is whether the platform's TLS stack did what we asked.
/// </para>
/// </remarks>
public sealed class PairingTlsTests(PairingKeys keys) : IClassFixture<PairingKeys>
{
    [Fact]
    public async Task ConnectsWhenBothSidesPresentThePinnedKey()
    {
        await using var listener = await PinnedListener.StartAsync(keys.Alice, keys.Bob.PublicKeyInfo);

        using var handler = PairingTls.CreateHandler(keys.Bob, keys.Alice.PublicKeyInfo);
        using var client = new HttpClient(handler) { BaseAddress = listener.Address };

        Assert.Equal("lightdrop", await client.GetStringAsync("pair/hello", CancellationToken.None));
    }

    [Fact]
    public async Task RejectsAListenerPresentingTheWrongKey()
    {
        // The man-in-the-middle: something answers on the address the peer announced, holding a key
        // nobody pinned. It must not get a connection.
        await using var listener = await PinnedListener.StartAsync(keys.Alice, keys.Bob.PublicKeyInfo);

        using var handler = PairingTls.CreateHandler(keys.Bob, keys.Impostor.PublicKeyInfo);
        using var client = new HttpClient(handler) { BaseAddress = listener.Address };

        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetStringAsync("pair/hello", CancellationToken.None));

        // Asserted as a TLS failure specifically, not just "it threw" -- a connection refused
        // because the listener was not running would otherwise pass this test.
        Assert.IsAssignableFrom<AuthenticationException>(failure.InnerException);
    }

    [Fact]
    public async Task RejectsACallerPresentingTheWrongKey()
    {
        // The other direction, which a listener-side-only check would miss: the caller holds a
        // perfectly valid certificate of its own, just not the one this device pinned.
        await using var listener = await PinnedListener.StartAsync(keys.Alice, keys.Bob.PublicKeyInfo);

        using var handler = PairingTls.CreateHandler(keys.Impostor, keys.Alice.PublicKeyInfo);
        using var client = new HttpClient(handler) { BaseAddress = listener.Address };

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetStringAsync("pair/hello", CancellationToken.None));
    }

    [Fact]
    public async Task NegotiatesAtLeastTls12()
    {
        // The version is deliberately not forced, so this asserts a floor rather than a value.
        // Record what it actually negotiated: it decides whether a later slice can require 1.3.
        await using var listener = await PinnedListener.StartAsync(keys.Alice, keys.Bob.PublicKeyInfo);

        using var handler = PairingTls.CreateHandler(keys.Bob, keys.Alice.PublicKeyInfo);
        using var client = new HttpClient(handler) { BaseAddress = listener.Address };

        var negotiated = await client.GetStringAsync("pair/protocol", CancellationToken.None);
        var protocol = Enum.Parse<SslProtocols>(negotiated);

        Assert.True(
            protocol is SslProtocols.Tls12 or SslProtocols.Tls13,
            $"Negotiated {protocol}, which is below the TLS 1.2 floor.");
    }
}
```

Note the project uses **xUnit 2.9.3**, so `IAsyncLifetime` has `Task InitializeAsync()` / `Task DisposeAsync()` and there is no `TestContext.Current` — pass `CancellationToken.None`, matching every other test in this suite.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/LightDrop.Daemon.Tests --filter "FullyQualifiedName~PairingTlsTests"`

Expected: compile errors first (the helper does not exist), then real failures. Before implementing, confirm you have seen `ConnectsWhenBothSidesPresentThePinnedKey` fail for a reason that is about the connection, not about a typo.

**Then confirm the rejections are real.** Temporarily change `PairingTls.CreateHandler`'s `RemoteCertificateValidationCallback` to `(_, _, _, _) => true` and re-run: `RejectsAListenerPresentingTheWrongKey` must fail. Restore it. Do the same for `ClientCertificateValidation` and confirm `RejectsACallerPresentingTheWrongKey` fails. **Record both mutations and their output in your report.** A pinning test that passes because the connection failed for an unrelated reason is worse than no test, and this is the only way to know the difference.

- [ ] **Step 4: Make them pass**

Fix whatever the failures show. Expect to iterate on `TargetHost`, on whether `AllowAnyClientCertificate()` is needed before `ClientCertificateValidation` takes effect, and on the exact exception type surfaced through `HttpRequestException`. Adjust the test's exception assertions to what the platform actually throws rather than forcing the platform to match the plan — but keep each test asserting that the connection *failed*, never relaxing it to "something happened".

- [ ] **Step 5: Full suite, build, and trimmed publish**

Run: `dotnet build LightDrop.sln && dotnet test LightDrop.sln`

Expected: 0 warnings, all passing.

Run: `dotnet publish src/LightDrop.Cli -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true`

Expected: **zero trim warnings.** TLS and certificate code is exactly the reflection-adjacent kind that trimming breaks, and this is the first time any of it is on a real connection path.

- [ ] **Step 6: Commit**

```bash
git add src/LightDrop.Daemon/Pairing/PairingTls.cs tests/LightDrop.Daemon.Tests/PairingTlsTests.cs tests/LightDrop.Daemon.Tests/TestSupport/PinnedListener.cs
git commit -m "feat: a mutual-TLS handshake that only a pinned peer survives

Both ends of a pairing connection: a listener that requires and pins its caller's
certificate, and a caller that pins the listener's. Default validation is replaced
rather than disabled -- both sides are self-signed, so no chain will ever build and
the platform's verdict is useless, but a callback that returns true builds a
handshake trusting anyone and looks identical to one that works.

Which is why the tests that matter are the two rejections, not the acceptance, and
why each was verified by mutation: with the pin check replaced by `true`, the
corresponding rejection test fails. A pinning test that passes because the
connection broke for an unrelated reason is worse than no test.

The TLS version is left to the platform rather than forced to 1.3. Security rests
on the pinned key, not the version -- an attacker who cannot produce the pinned
private key fails at any version -- and forcing 1.3 risked breaking one of the two
platforms this project targets. The suite asserts a 1.2 floor and records what was
actually negotiated, so a later slice can raise it with evidence.

No LAN binding, no pairing session, no window: this is the handshake alone."
```

---

## What this slice does not do

Deliberately out of scope. The next slice picks these up:

- **No LAN listener.** Everything here is loopback, including the HTTPS listener, which lives only in tests. The daemon's own Kestrel endpoint is untouched.
- **No pairing session, no 60-second window, no role assignment by device id.**
- **No `lightdrop pair`, no UI, no endpoints.**
- **No use of `PairingCode` or `PairingService`.** They stay unused for one more slice.
- **Nothing is wired into `LightDropDaemon`.** `PairingTls` is exercised only by its tests.

One thing the next slice must handle, already known: the origin-check middleware is global and hard-wired to the loopback authority, so the LAN pairing listener will silently 403 every inbound POST until it is scoped by `Connection.LocalPort`. There is a warning comment on `LoopbackOriginMiddleware` saying so.
