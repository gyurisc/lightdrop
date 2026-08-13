using System.Net;
using System.Net.Http.Json;
using LightDrop.Core;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using LightDrop.Daemon.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// End-to-end over real HTTP against a real Kestrel binding.
/// </summary>
/// <remarks>
/// Not using <c>WebApplicationFactory</c>: it reflects over an entry point to build the host, and
/// LightDrop.Daemon is a class library with no <c>Main</c> — the only executable is the CLI. It
/// also serves over an in-memory transport, which would not exercise the Kestrel binding this
/// test exists to cover.
/// </remarks>
public sealed class HealthEndpointTests
{
    [Fact]
    public async Task ServesIdentityAndVersionInformationOverHttp()
    {
        using var directory = new TempDataDirectory();
        var endpoint = new DaemonEndpointOptions { Host = "127.0.0.1", Port = FreeTcpPort.Get() };

        await using var app = LightDropDaemon.Create(endpoint, directory.FullPath);
        await app.StartAsync(CancellationToken.None);

        using var client = new HttpClient { BaseAddress = endpoint.ClientAddress };
        using var response = await client.GetAsync("health", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var health = await response.Content.ReadFromJsonAsync(
            LightDropJsonContext.Default.HealthResponse,
            CancellationToken.None);

        Assert.NotNull(health);
        Assert.Equal(LightDropVersion.Current, health.Version);
        Assert.Equal(LightDropVersion.Protocol, health.ProtocolVersion);
        Assert.False(string.IsNullOrWhiteSpace(health.DeviceId));
        Assert.False(string.IsNullOrWhiteSpace(health.DeviceName));
        Assert.False(string.IsNullOrWhiteSpace(health.Platform));

        await app.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PersistsIdentityIntoTheConfiguredDataDirectory()
    {
        // Proves the whole chain — Kestrel to DI to JsonStateStore — is pointed at the directory
        // the caller supplied. If this regressed, the suite would silently start writing to the
        // real user profile.
        using var directory = new TempDataDirectory();
        var endpoint = new DaemonEndpointOptions { Host = "127.0.0.1", Port = FreeTcpPort.Get() };

        await using var app = LightDropDaemon.Create(endpoint, directory.FullPath);
        await app.StartAsync(CancellationToken.None);

        using var client = new HttpClient { BaseAddress = endpoint.ClientAddress };
        var health = await client.GetFromJsonAsync(
            "health",
            LightDropJsonContext.Default.HealthResponse,
            CancellationToken.None);

        Assert.True(File.Exists(directory.StateFilePath));
        Assert.NotNull(health);
        Assert.Contains(health.DeviceId, await File.ReadAllTextAsync(directory.StateFilePath), StringComparison.Ordinal);

        await app.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ReportsTheNameConfiguredByTheUser()
    {
        using var directory = new TempDataDirectory();
        directory.WriteConfig("""{ "deviceName": "Work Laptop" }""");
        var endpoint = new DaemonEndpointOptions { Host = "127.0.0.1", Port = FreeTcpPort.Get() };

        await using var app = LightDropDaemon.Create(endpoint, directory.FullPath);
        await app.StartAsync(CancellationToken.None);

        using var client = new HttpClient { BaseAddress = endpoint.ClientAddress };
        var health = await client.GetFromJsonAsync(
            "health",
            LightDropJsonContext.Default.HealthResponse,
            CancellationToken.None);

        Assert.NotNull(health);
        Assert.Equal("Work Laptop", health.DeviceName);

        await app.StopAsync(CancellationToken.None);
    }
}
