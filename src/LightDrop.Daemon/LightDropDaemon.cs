using System.Net;
using LightDrop.Core;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using LightDrop.Daemon.Endpoints;
using LightDrop.Daemon.Infrastructure;
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
    public static WebApplication Create(DaemonEndpointOptions? endpoint = null, string? dataDirectory = null)
    {
        endpoint ??= DaemonEndpointOptions.FromEnvironment();
        endpoint.Validate();

        // CreateSlimBuilder omits the parts of the default host LightDrop does not need
        // (IIS integration, hosting startup assemblies, the full configuration pipeline),
        // which keeps the published binary smaller and startup faster.
        var builder = WebApplication.CreateSlimBuilder();

        ConfigureLogging(builder);
        ConfigureKestrel(builder, endpoint);
        ConfigureServices(builder, endpoint, dataDirectory);

        var app = builder.Build();
        app.MapHealthEndpoints();
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
        CancellationToken cancellationToken = default)
    {
        var app = Create(endpoint, dataDirectory);
        await using (app.ConfigureAwait(false))
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);

            // The generic host already traps SIGINT and SIGTERM, so this covers both Ctrl+C on
            // Windows and a terminating service manager on macOS.
            await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ConfigureLogging(WebApplicationBuilder builder)
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
        string? dataDirectory)
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

        // Command handlers register here as they are built. The health endpoint's capability
        // list is projected from them, so no separate list needs maintaining.

        services.AddHostedService<DaemonLifetimeService>();

        services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, LightDropJsonContext.Default));
    }
}
