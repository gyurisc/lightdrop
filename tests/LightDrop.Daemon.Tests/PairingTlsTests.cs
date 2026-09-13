using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using LightDrop.Core.Pairing;
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

        // Weaker than its sibling's assertion on purpose, not by oversight: a listener-side
        // handshake rejection surfaces to the client as IOException/SocketException, not
        // AuthenticationException, so the stronger assertion above is not available here. This
        // still asserts the request failed, which a connection-refused would also satisfy -- but
        // there is no TLS-specific exception type on this side to narrow it further.
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetStringAsync("pair/hello", CancellationToken.None));
    }

    [Fact]
    public async Task RejectsACallerPresentingNoCertificate()
    {
        // The one line separating mutual TLS from "anonymous callers welcome":
        // ClientCertificateMode.RequireCertificate. A handler that never attaches a client
        // certificate at all -- not the wrong one, none -- must not get a connection. If this
        // were relaxed to AllowCertificate, Kestrel would never invoke ClientCertificateValidation
        // for a caller with no certificate to validate, and the connection would simply succeed.
        await using var listener = await PinnedListener.StartAsync(keys.Alice, keys.Bob.PublicKeyInfo);

        using var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "lightdrop",
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is X509Certificate2 presented
                    && PinnedCertificate.Matches(presented, keys.Alice.PublicKeyInfo),
            },
        };
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
