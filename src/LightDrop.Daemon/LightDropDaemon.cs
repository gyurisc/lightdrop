using System.Net;
using System.Net.Sockets;
using LightDrop.Core;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using LightDrop.Core.Devices;
using LightDrop.Core.Discovery;
using LightDrop.Daemon.Commands;
using LightDrop.Daemon.Discovery;
using LightDrop.Daemon.Endpoints;
using LightDrop.Daemon.Infrastructure;
using LightDrop.Daemon.Pairing;
using LightDrop.Daemon.Peers;
using LightDrop.Daemon.Security;
using LightDrop.Daemon.Transfers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LightDrop.Daemon;

/// <summary>
/// Builds and runs the LightDrop daemon.
/// </summary>
/// <remarks>
/// Exposed as a library entry point rather than a <c>Main</c> so the CLI can host it in-process
/// and LightDrop can ship as a single executable.
/// </remarks>
public static class LightDropDaemon
{
    /// <summary>
    /// Builds the daemon without starting it. Useful for tests that need the wired-up host.
    /// </summary>
    /// <param name="endpoint">Where to bind. Resolved from the environment when null.</param>
    /// <param name="dataDirectory">Overrides the config and state location. For tests.</param>
    /// <param name="peerDiscoveryTransport">
    /// Overrides how peers are discovered. Tests pass a
    /// <see cref="NoOpPeerDiscoveryTransport"/> so the suite never opens a multicast socket —
    /// multicast cannot be routed on CI runners and fails silently on macOS without the Local
    /// Network permission.
    /// </param>
    public static WebApplication Create(
        DaemonEndpointOptions? endpoint = null,
        string? dataDirectory = null,
        IPeerDiscoveryTransport? peerDiscoveryTransport = null) =>
        Create(endpoint, dataDirectory, peerDiscoveryTransport, TimeProvider.System);

    /// <param name="pairingClock">
    /// The clock pairing deadlines are measured against. Tests pass one they drive, so a deadline
    /// can be crossed without waiting a minute for it. Only pairing reads it: discovery's expiry
    /// and Kestrel's own timeouts stay on real time.
    /// </param>
    internal static WebApplication Create(
        DaemonEndpointOptions? endpoint,
        string? dataDirectory,
        IPeerDiscoveryTransport? peerDiscoveryTransport,
        TimeProvider pairingClock)
    {
        endpoint ??= DaemonEndpointOptions.FromEnvironment();
        endpoint.Validate();

        // CreateSlimBuilder omits the parts of the default host LightDrop does not need
        // (IIS integration, hosting startup assemblies, the full configuration pipeline),
        // which keeps the published binary smaller and startup faster.
        var builder = WebApplication.CreateSlimBuilder();

        ConfigureLogging(builder);
        ConfigureKestrel(builder, endpoint);
        ConfigureServices(builder, endpoint, dataDirectory, peerDiscoveryTransport, pairingClock);

        var app = builder.Build();
        app.UseLoopbackOriginCheck(endpoint);
        app.MapUiEndpoints();
        app.MapHealthEndpoints();
        app.MapPeerEndpoints();
        app.MapPairingEndpoints();
        app.MapSendEndpoints();
        return app;
    }

    /// <summary>
    /// Runs the daemon until the process is asked to stop.
    /// </summary>
    /// <param name="endpoint">Where to bind. Resolved from the environment when null.</param>
    /// <param name="dataDirectory">
    /// Overrides the config and state location. Needed whenever two daemons run on one machine —
    /// sharing a state file would make them fight over a single device identity.
    /// </param>
    /// <param name="cancellationToken">Cancelling triggers a graceful shutdown.</param>
    public static async Task RunAsync(
        DaemonEndpointOptions? endpoint = null,
        string? dataDirectory = null,
        CancellationToken cancellationToken = default,
        IPeerDiscoveryTransport? peerDiscoveryTransport = null)
    {
        // UiCommand duplicates this body rather than calling it: it needs to catch a bind failure
        // from StartAsync specifically (to fall back to "already running, opening a browser tab"),
        // which a shared helper covering both StartAsync and WaitForShutdownAsync could not do
        // without changing this method's own error handling. Keep the two in sync by hand.
        var app = Create(endpoint, dataDirectory, peerDiscoveryTransport);
        await using (app.ConfigureAwait(false))
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);

            // The generic host already traps SIGINT and SIGTERM, so this covers both Ctrl+C on
            // Windows and a terminating service manager on macOS.
            await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a startup failure was the port already being in use.
    /// </summary>
    /// <remarks>
    /// Kestrel reports this as an <see cref="IOException"/> wrapping the socket error, so the
    /// chain has to be walked rather than the outermost type matched. Narrow on purpose: a
    /// permission failure must not be reported as "already running", which would send the user to
    /// a browser tab instead of telling them what actually went wrong.
    /// </remarks>
    public static bool IsAddressInUse(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }

        return false;
    }

    /// <remarks>
    /// Shared with the peer listener's own <see cref="WebApplication"/>, so both write the same
    /// structured lines to the same console.
    /// </remarks>
    internal static void ConfigureLogging(WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();

        // Structured by default: one JSON object per line, machine-parseable, no dependency.
        builder.Logging.AddJsonConsole(options =>
        {
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
            options.IncludeScopes = true;
        });

        // Kestrel's per-request chatter is noise for a personal file-sharing utility.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
    }

    private static void ConfigureKestrel(WebApplicationBuilder builder, DaemonEndpointOptions endpoint)
    {
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Parse(endpoint.Host), endpoint.Port);
        });
    }

    private static void ConfigureServices(
        WebApplicationBuilder builder,
        DaemonEndpointOptions endpoint,
        string? dataDirectory,
        IPeerDiscoveryTransport? peerDiscoveryTransport,
        TimeProvider pairingClock)
    {
        var services = builder.Services;

        services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(5));
        services.AddSingleton(endpoint);

        services.Configure<StorageOptions>(storage =>
        {
            if (!string.IsNullOrWhiteSpace(dataDirectory))
            {
                storage.DataDirectory = dataDirectory;
            }
        });

        // Infrastructure adapters for the ports Core declares.
        services.AddSingleton<IConfigStore, JsonConfigStore>();
        services.AddSingleton<IStateStore, JsonStateStore>();

        services.AddLightDropCore();

        // The commands paired peers may send, and the capabilities projected from them.
        services.AddCommandHandlers();

        services.AddSingleton<OutgoingTransfers>();
        services.AddSingleton<FileSender>();

        // Discovered peers live here and nowhere else — in memory, for this process only. The
        // registry has no route to IStateStore by design: a discovered peer is a stranger, and
        // pairing must cross that boundary explicitly rather than inherit an existing path.
        services.AddSingleton(provider =>
        {
            // Resolved synchronously at startup so the registry can filter out this device's own
            // announcements, which multicast loops straight back. Safe here: there is no
            // synchronization context to deadlock against, and this is one small file read, once.
            var identity = provider.GetRequiredService<DeviceIdentityProvider>()
                .GetAsync().AsTask().GetAwaiter().GetResult();

            return new PeerRegistry(TimeProvider.System, identity.Id);
        });

        services.AddSingleton(new DiscoveryStatus(TimeProvider.System));

        if (peerDiscoveryTransport is not null)
        {
            services.AddSingleton(peerDiscoveryTransport);
        }
        else
        {
            services.AddSingleton<IPeerDiscoveryTransport, MdnsPeerDiscoveryTransport>();
        }

        services.AddHostedService<PeerDiscoveryService>();
        services.AddHostedService<DaemonLifetimeService>();

        // Pairing crosses from discovery (the registry, for the dial address and sanitized name)
        // into trust (PairingService, for the pin) -- deliberately here, in one class, rather than
        // through any path from the registry itself.
        // The clock is handed to the coordinator alone rather than registered as TimeProvider:
        // the framework resolves that type for itself, and a test's frozen clock must not reach it.
        services.AddSingleton(provider =>
            ActivatorUtilities.CreateInstance<PairingCoordinator>(provider, pairingClock));

        // A singleton the pairing endpoint can start on demand, and a hosted service so it starts
        // with the daemon when a peer is already pinned and stops with it. Registered last so it
        // stops first, ahead of the services its connections lean on.
        services.AddSingleton<PeerListener>();
        services.AddHostedService(provider => provider.GetRequiredService<PeerListener>());

        services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, LightDropJsonContext.Default));
    }
}
