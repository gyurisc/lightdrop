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
