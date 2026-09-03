using System.Net;
using LightDrop.Core.Configuration;
using LightDrop.Daemon.Discovery;
using LightDrop.Daemon.Security;
using LightDrop.Daemon.Tests.TestSupport;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// Which requests the daemon will act on.
/// </summary>
/// <remarks>
/// Binding to loopback keeps the LAN out; it does nothing about the browser already running on
/// this machine. Any page the user has open can send a request to 127.0.0.1, so a state-changing
/// endpoint needs to know where the request came from.
/// </remarks>
public sealed class LoopbackOriginPolicyTests
{
    private static readonly DaemonEndpointOptions Endpoint = new() { Host = "127.0.0.1", Port = 5533 };

    [Fact]
    public void AllowsReadsFromAnywhere()
    {
        // A foreign Origin on a read is allowed as long as Host is correct: the page itself sends
        // an Origin, and a legitimate cross-origin read of a public endpoint must not break.
        Assert.True(LoopbackOriginPolicy.IsAllowed("GET", "https://evil.example", "127.0.0.1:5533", Endpoint));
    }

    [Fact]
    public void RejectsAReadWithAForeignHost()
    {
        // This is the DNS-rebinding bug: a GET was exempt from the Host check entirely, so a name
        // that resolves to 127.0.0.1 could read /health and /api/peers. Host must be validated
        // before the method is allowed to shortcut anything.
        Assert.False(LoopbackOriginPolicy.IsAllowed("GET", null, "attacker.example:5533", Endpoint));
    }

    [Fact]
    public void AllowsAReadWithTheCorrectHostAndAForeignOrigin()
    {
        // The Origin rule only applies to non-safe methods. A correct Host plus a foreign Origin
        // is still a legitimate local read — the page and any cross-origin read of a public GET
        // endpoint must keep working.
        Assert.True(LoopbackOriginPolicy.IsAllowed("GET", "https://evil.example", "127.0.0.1:5533", Endpoint));
    }

    [Fact]
    public void AllowsAWriteFromThePageItself()
    {
        Assert.True(LoopbackOriginPolicy.IsAllowed("POST", "http://127.0.0.1:5533", "127.0.0.1:5533", Endpoint));
    }

    [Fact]
    public void RejectsAWriteFromAnotherSite()
    {
        // The attack this exists for: a page the user happens to have open posting to the daemon.
        Assert.False(LoopbackOriginPolicy.IsAllowed("POST", "https://evil.example", "127.0.0.1:5533", Endpoint));
    }

    [Fact]
    public void RejectsAWriteFromTheSameHostOnAnotherPort()
    {
        // Another local server is a different origin, and on a shared machine a different user.
        Assert.False(LoopbackOriginPolicy.IsAllowed("POST", "http://127.0.0.1:9999", "127.0.0.1:5533", Endpoint));
    }

    [Fact]
    public void RejectsAnOpaqueOrigin()
    {
        // Sandboxed iframes and file:// pages send the literal string "null".
        Assert.False(LoopbackOriginPolicy.IsAllowed("POST", "null", "127.0.0.1:5533", Endpoint));
    }

    [Fact]
    public void AllowsAWriteWithNoOriginFromLoopback()
    {
        // This is the CLI. It sends no Origin header, and `lightdrop pair` will POST.
        Assert.True(LoopbackOriginPolicy.IsAllowed("POST", null, "127.0.0.1:5533", Endpoint));
    }

    [Fact]
    public void RejectsAWriteWithNoOriginFromAnotherHost()
    {
        // DNS rebinding: a name that resolves to 127.0.0.1 arrives with its own Host header.
        Assert.False(LoopbackOriginPolicy.IsAllowed("POST", null, "attacker.example", Endpoint));
    }
}

public sealed class LoopbackOriginEndpointTests
{
    [Fact]
    public async Task RejectsACrossOriginWriteOverRealHttp()
    {
        // Asserted against a path no route serves: the check runs ahead of routing, and phase A
        // has no write endpoint of its own to aim at.
        using var directory = new TempDataDirectory();
        var endpoint = new DaemonEndpointOptions { Host = "127.0.0.1", Port = FreeTcpPort.Get() };
        using var cancellation = new CancellationTokenSource();

        var app = LightDropDaemon.Create(endpoint, directory.FullPath, new NoOpPeerDiscoveryTransport());
        await using (app.ConfigureAwait(false))
        {
            await app.StartAsync(cancellation.Token);

            using var client = new HttpClient { BaseAddress = endpoint.ClientAddress };

            using var hostile = new HttpRequestMessage(HttpMethod.Post, "anything");
            hostile.Headers.Add("Origin", "https://evil.example");
            using var rejected = await client.SendAsync(hostile, cancellation.Token);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);

            // The same request without a foreign Origin gets as far as routing, which is what
            // proves the middleware is not simply blocking everything.
            using var local = new HttpRequestMessage(HttpMethod.Post, "anything");
            using var routed = await client.SendAsync(local, cancellation.Token);
            Assert.Equal(HttpStatusCode.NotFound, routed.StatusCode);

            await app.StopAsync(cancellation.Token);
        }
    }

    [Fact]
    public async Task RejectsAGetWithAForeignHostOverRealHttp()
    {
        // The DNS-rebinding regression this whole finding is about: a GET with a foreign Host
        // header must be rejected over real HTTP, not just in the policy's unit tests.
        using var directory = new TempDataDirectory();
        var endpoint = new DaemonEndpointOptions { Host = "127.0.0.1", Port = FreeTcpPort.Get() };
        using var cancellation = new CancellationTokenSource();

        var app = LightDropDaemon.Create(endpoint, directory.FullPath, new NoOpPeerDiscoveryTransport());
        await using (app.ConfigureAwait(false))
        {
            await app.StartAsync(cancellation.Token);

            using var client = new HttpClient { BaseAddress = endpoint.ClientAddress };

            using var hostile = new HttpRequestMessage(HttpMethod.Get, "health");
            hostile.Headers.Host = "attacker.example:15534";
            using var rejected = await client.SendAsync(hostile, cancellation.Token);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);

            await app.StopAsync(cancellation.Token);
        }
    }
}
