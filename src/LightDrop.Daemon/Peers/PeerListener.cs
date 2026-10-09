using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using LightDrop.Core.Devices;
using LightDrop.Core.Pairing;
using LightDrop.Daemon.Commands;
using LightDrop.Daemon.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LightDrop.Daemon.Peers;

/// <summary>
/// The one LAN-reachable socket: HTTPS with mutual TLS on <see cref="DaemonEndpointOptions.PeerPort"/>,
/// admitting only pinned peers, or anyone while a pairing window is open.
/// </summary>
/// <remarks>
/// <strong>A separate <see cref="WebApplication"/>, not a second binding on the loopback one.</strong>
/// The loopback app's route table holds <c>/health</c>, <c>/api/*</c> and the UI, and its middleware
/// is the loopback origin check. Neither belongs here, and the only reliable way to keep them out
/// is for this app never to have them: it maps the peer routes and nothing else. Scoping routes by
/// host (<c>RequireHost</c>) would not do it -- that matches the <c>Host</c> header, which the
/// caller writes, not the port the connection actually arrived on.
/// <para>
/// <strong>Started only when needed.</strong> At daemon start if any peer is pinned, otherwise when
/// a pairing window first opens, so a fresh install with nobody paired has no LAN socket at all.
/// It is not stopped on unpair: after the last unpair every handshake is refused anyway, and a
/// restart closes it.
/// </para>
/// <para>
/// <strong>The TLS handshake is the gate.</strong> Each connection's validation closes over
/// <c>trustedPeers</c> as read for that connection, so an unpair takes effect on the next
/// connection with nothing to invalidate. While a window is open any certificate gets through,
/// because the handshake is the only way the new peer's key can arrive -- which is why every
/// route behind this gate also checks for itself: the pairing routes against the session's
/// joined key, the <c>/commands</c> group through <see cref="PinnedPeerFilter"/>.
/// </para>
/// </remarks>
internal sealed class PeerListener(
    DaemonEndpointOptions endpoint,
    DeviceKeyProvider keys,
    PairingService pairing,
    PairingCoordinator coordinator,
    IEnumerable<ICommandHandler> commandHandlers,
    ILogger<PeerListener> logger) : IHostedService, IAsyncDisposable
{
    /// <summary>
    /// The largest request body a peer may send. Every pairing body is a few dozen bytes, and the
    /// caller may be a stranger. A route that needs more must raise it for itself, per request,
    /// as <c>SendFile</c> does to exactly its declared length.
    /// </summary>
    internal const long MaxRequestBodyBytes = 4096;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;

    private string Endpoint => $"{endpoint.PeerHost}:{endpoint.PeerPort}";

    /// <summary>Starts the listener if any peer is pinned. A failure is logged, not fatal.</summary>
    /// <remarks>
    /// Not fatal because the loopback daemon is still useful without it -- discovery, health, the
    /// CLI -- and the next <c>lightdrop pair</c> tries again and reports the reason to the user.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var trusted = await pairing.ListAsync(cancellationToken).ConfigureAwait(false);
        if (trusted.Count == 0)
        {
            return;
        }

        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            PairingLog.ListenerFailed(logger, Endpoint, ex);
        }
    }

    /// <summary>Starts the listener unless it is already running.</summary>
    /// <exception cref="IOException">The port could not be bound, for whatever reason.</exception>
    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_app is not null)
            {
                return;
            }

            var local = await keys.GetAsync(cancellationToken).ConfigureAwait(false);
            var app = Build(local);

            try
            {
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                // Kestrel wraps only "address in use" in an IOException; every other bind error
                // escapes raw. Windows refusing a port inside a Hyper-V / WinNAT excluded range
                // (common with WSL2 or Docker Desktop) is one, an address no interface holds is
                // another. Rethrown as the one type both callers handle, naming the way out.
                await app.DisposeAsync().ConfigureAwait(false);
                throw new IOException(
                    $"Could not listen on {Endpoint}: {ex.Message}. Set {DaemonEndpointOptions.PeerPortEnvironmentVariable} " +
                    "to use a different port.",
                    ex);
            }
            catch
            {
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _app = app;
            PairingLog.ListenerStarted(logger, Endpoint);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_app is null)
            {
                return;
            }

            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }

        _gate.Dispose();
    }

    private WebApplication Build(DeviceKeyPair local)
    {
        var builder = WebApplication.CreateSlimBuilder();

        LightDropDaemon.ConfigureLogging(builder);

        // The loopback app's host owns Ctrl+C and SIGTERM and stops this one itself. A console
        // lifetime here would be a second process-signal handler stopping half the daemon on its own.
        builder.Services.AddSingleton<IHostLifetime, OwnedByDaemonLifetime>();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaxRequestBodyBytes;

            kestrel.Listen(IPAddress.Parse(endpoint.PeerHost), endpoint.PeerPort, listen =>
                listen.UseHttps(new TlsHandshakeCallbackOptions
                {
                    OnConnection = context => OnConnectionAsync(local, context.CancellationToken),
                }));
        });

        // The same instances the loopback app uses, not copies: the session a CLI opened over
        // loopback is the one a peer joins here.
        builder.Services.AddSingleton(coordinator);
        builder.Services.AddSingleton(pairing);

        // The handler instances the loopback app's container built, so the set dispatched to here
        // is the set /health advertises there.
        foreach (var handler in commandHandlers)
        {
            builder.Services.AddSingleton(handler);
        }

        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, LightDropJsonContext.Default));

        var app = builder.Build();
        app.MapPairingPeerEndpoints();
        app.MapCommandEndpoints();
        return app;
    }

    /// <summary>
    /// The handshake gate, decided per connection.
    /// </summary>
    /// <remarks>
    /// <c>trustedPeers</c> is re-read for every connection rather than cached and invalidated: a
    /// cache is one more thing an unpair could forget to update, and the read is one small file.
    /// The pairing check runs inside the validation callback rather than here, so a window that
    /// closes between connection and certificate is honoured.
    /// </remarks>
    private async ValueTask<SslServerAuthenticationOptions> OnConnectionAsync(
        DeviceKeyPair local,
        CancellationToken cancellationToken)
    {
        var trusted = await pairing.ListAsync(cancellationToken).ConfigureAwait(false);

        return PairingTls.CreateServerOptions(local, presented =>
            trusted.Any(peer => PinnedCertificate.Matches(presented, peer.PublicKey))
            || coordinator.AdmitsForPairing(presented));
    }

    /// <summary>A host lifetime that leaves process signals to the loopback app's host.</summary>
    private sealed class OwnedByDaemonLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
