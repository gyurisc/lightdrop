using System.Net;
using LightDrop.Core.Configuration;
using LightDrop.Core.Pairing;
using LightDrop.Daemon.Infrastructure;
using LightDrop.Daemon.Pairing;
using LightDrop.Daemon.Peers;
using LightDrop.Daemon.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// The application-layer pin check that sits behind the handshake gate.
/// </summary>
/// <remarks>
/// The filter in isolation, on a test-only host whose TLS layer admits any certificate -- as the
/// peer listener's does while a pairing window is open -- built from the production
/// <see cref="PairingTls.CreateServerOptions"/> through the same per-connection Kestrel callback
/// the listener uses. That it is actually applied to the listener's <c>/commands</c> group is
/// <c>FileTransferTests</c>' job.
/// </remarks>
public sealed class PinnedPeerFilterTests(PairingKeys keys) : IClassFixture<PairingKeys>
{
    private async Task<(WebApplication App, Uri Address, TempDataDirectory Directory)> StartAsync()
    {
        var directory = new TempDataDirectory();
        var store = new JsonStateStore(Options.Create(new StorageOptions { DataDirectory = directory.FullPath }));
        var pairing = new PairingService(store, TimeProvider.System);
        await pairing.PinAsync("bob", "Bob", keys.Bob.PublicKeyInfo);

        var port = FreeTcpPort.Get();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(pairing);

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, port, listen =>
                listen.UseHttps(new TlsHandshakeCallbackOptions
                {
                    // Accept-anything, standing in for an open pairing window.
                    OnConnection = _ => ValueTask.FromResult(PairingTls.CreateServerOptions(keys.Alice, _ => true)),
                })));

        var app = builder.Build();

        app.MapGroup("/commands")
            .RequirePinnedPeer()
            .MapPost("/{name}", (HttpContext context) =>
                ((TrustedPeer)context.Items[PinnedPeerFilter.ItemsKey]!).DeviceName);

        await app.StartAsync();
        return (app, new Uri($"https://127.0.0.1:{port}/"), directory);
    }

    private static async Task<HttpResponseMessage> PostAsync(Core.Devices.DeviceKeyPair presenting, Uri address)
    {
        using var client = new HttpClient(PairingTls.CreateCapturingHandler(presenting, new PeerKeyCapture()))
        {
            BaseAddress = address,
        };

        return await client.PostAsync("commands/anything", content: null);
    }

    [Fact]
    public async Task LetsAPinnedPeerThroughAndSaysWhoItIs()
    {
        // Also the proof that the connection's client certificate reaches HttpContext when TLS
        // is configured per connection, which the filter depends on.
        var (app, address, directory) = await StartAsync();
        await using (app)
        using (directory)
        {
            using var response = await PostAsync(keys.Bob, address);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Bob", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task RefusesAnUnpinnedPeerThatGotThroughTheHandshake()
    {
        var (app, address, directory) = await StartAsync();
        await using (app)
        using (directory)
        {
            using var response = await PostAsync(keys.Impostor, address);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }
}
