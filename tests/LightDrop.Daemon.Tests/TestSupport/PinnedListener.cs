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
