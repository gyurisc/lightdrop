using System.Net;
using LightDrop.Core.Contracts;
using LightDrop.Core.Devices;
using LightDrop.Core.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LightDrop.Daemon.Tests.TestSupport;

/// <summary>
/// A hostile or broken "listening device" the test scripts, for exercising a real daemon's dialer.
/// </summary>
/// <remarks>
/// The dialer's checks are about what the <em>other</em> side does -- answer as the wrong device,
/// change keys between hello and confirm, reveal a nonce it never committed to -- and a real
/// daemon never misbehaves that way. This stands in for an attacker on the LAN who does.
/// </remarks>
internal sealed class ScriptedPairingListener : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly byte[] _nonce = PairingCode.CreateNonce();
    private int _revealCalls;
    private byte[]? _revealedByDialer;
    private int _confirmCalls;
    private volatile bool _helloServed;

    private ScriptedPairingListener(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    /// <summary>How many times anything reached <c>/pair/reveal</c>.</summary>
    public int RevealCalls => Volatile.Read(ref _revealCalls);

    /// <summary>How many times anything reached <c>/pair/confirm</c>.</summary>
    public int ConfirmCalls => Volatile.Read(ref _confirmCalls);

    /// <summary>The nonce this listener committed to at hello.</summary>
    public byte[] Nonce => (byte[])_nonce.Clone();

    /// <summary>The nonce the dialer revealed, once it has.</summary>
    public byte[]? RevealedByDialer => Volatile.Read(ref _revealedByDialer);

    /// <param name="key">The key presented while hello is being answered.</param>
    /// <param name="answerAs">The device id hello claims to be.</param>
    /// <param name="keyAfterHello">The key presented on every connection after hello was answered.</param>
    /// <param name="revealAnotherNonce">
    /// Answer the reveal with a fresh nonce rather than the committed one -- what a listener
    /// choosing its digits after seeing the dialer's nonce would have to do.
    /// </param>
    public static async Task<ScriptedPairingListener> StartAsync(
        DeviceKeyPair key,
        string answerAs,
        DeviceKeyPair? keyAfterHello = null,
        bool revealAnotherNonce = false)
    {
        var port = FreeTcpPort.Get();
        ScriptedPairingListener? listener = null;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, port, listen =>
                listen.UseHttps(https =>
                {
                    https.ServerCertificateSelector = (_, _) =>
                        listener!._helloServed && keyAfterHello is not null
                            ? keyAfterHello.Certificate
                            : key.Certificate;

                    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    https.ClientCertificateValidation = (_, _, _) => true;
                })));

        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, LightDropJsonContext.Default));

        var app = builder.Build();
        listener = new ScriptedPairingListener(app, port);

        // An honest commitment over the dialer's real key, so that every test except the one that
        // asks for a lie gets as far as the code.
        app.MapPost("/pair/hello", (PairHelloRequest _, HttpContext context) =>
        {
            Assert.True(PinnedCertificate.TryExportPublicKeyInfo(context.Connection.ClientCertificate, out var dialerKey));
            listener._helloServed = true;

            return TypedResults.Ok(new PairHelloResponse
            {
                DeviceId = answerAs,
                Commitment = PairingCode.Commit(key.PublicKeyInfo, dialerKey, listener._nonce),
            });
        });

        app.MapPost("/pair/reveal", (PairingNonce revealed) =>
        {
            Interlocked.Increment(ref listener._revealCalls);
            Volatile.Write(ref listener._revealedByDialer, revealed.Nonce);
            return TypedResults.Ok(new PairingNonce
            {
                Nonce = revealAnotherNonce ? PairingCode.CreateNonce() : listener._nonce,
            });
        });

        app.MapPost("/pair/confirm", (PairingDecision _) =>
        {
            Interlocked.Increment(ref listener._confirmCalls);
            return TypedResults.Ok(new PairingDecision { Accepted = true });
        });

        await app.StartAsync();
        return listener;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
