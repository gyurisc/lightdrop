using System.Net;
using LightDrop.Core.Contracts;
using LightDrop.Core.Devices;
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
/// change keys between hello and confirm -- and a real daemon never misbehaves that way. This
/// stands in for an attacker on the LAN who does.
/// </remarks>
internal sealed class ScriptedPairingListener : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _confirmCalls;
    private volatile bool _helloServed;

    private ScriptedPairingListener(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    /// <summary>How many times anything reached <c>/pair/confirm</c>.</summary>
    public int ConfirmCalls => Volatile.Read(ref _confirmCalls);

    /// <param name="key">The key presented while hello is being answered.</param>
    /// <param name="answerAs">The device id hello claims to be.</param>
    /// <param name="keyAfterHello">The key presented on every connection after hello was answered.</param>
    public static async Task<ScriptedPairingListener> StartAsync(
        DeviceKeyPair key,
        string answerAs,
        DeviceKeyPair? keyAfterHello = null)
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

        app.MapPost("/pair/hello", (PairHelloRequest _) =>
        {
            listener._helloServed = true;
            return TypedResults.Ok(new PairHelloResponse { DeviceId = answerAs });
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
