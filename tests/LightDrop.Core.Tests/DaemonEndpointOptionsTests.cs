using LightDrop.Core.Configuration;

namespace LightDrop.Core.Tests;

public sealed class DaemonEndpointOptionsTests
{
    [Fact]
    public void TheApiDefaultsToLoopback()
    {
        // The loopback API has no authentication of its own; binding it to the LAN would expose
        // it to every device on the network. Peers use the separate, pinned peer listener. This
        // default is a security decision.
        var options = new DaemonEndpointOptions();

        Assert.Equal("127.0.0.1", options.Host);
        Assert.Equal(5533, options.Port);
    }

    [Fact]
    public void BaseAddressReflectsTheConfiguredEndpoint()
    {
        var options = new DaemonEndpointOptions { Host = "127.0.0.1", Port = 9000 };

        Assert.Equal(new Uri("http://127.0.0.1:9000/"), options.BaseAddress);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void ClientAddressRewritesWildcardBindsToLoopback(string host)
    {
        // A daemon bound to a wildcard is not reachable *at* the wildcard address, so the CLI
        // has to be pointed somewhere real.
        var options = new DaemonEndpointOptions { Host = host, Port = 5533 };

        Assert.Equal(new Uri("http://127.0.0.1:5533/"), options.ClientAddress);
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    public void FormatsIpv6HostsAsBracketedUris(string host)
    {
        // Validate() accepts IPv6 because Kestrel binds them fine, but Uri rejects an unbracketed
        // literal. Without bracketing the daemon binds and then throws formatting its own
        // startup log line.
        var options = new DaemonEndpointOptions { Host = host, Port = 5533 };

        options.Validate();

        Assert.Equal(new Uri($"http://[{host}]:5533/"), options.BaseAddress);
    }

    [Fact]
    public void ClientAddressMatchesBaseAddressForConcreteHosts()
    {
        var options = new DaemonEndpointOptions { Host = "192.168.1.20", Port = 5533 };

        Assert.Equal(options.BaseAddress, options.ClientAddress);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void RejectsPortsOutsideTheValidRange(int port)
    {
        var options = new DaemonEndpointOptions { Port = port };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void RejectsHostsThatAreNotIpAddresses()
    {
        // Kestrel binds to an address, not a name. Catching this at startup beats a confusing
        // bind failure later.
        var options = new DaemonEndpointOptions { Host = "localhost" };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void AcceptsAValidEndpoint()
    {
        var options = new DaemonEndpointOptions { Host = "127.0.0.1", Port = 5533 };

        options.Validate();
    }

    [Fact]
    public void PeerPortFollowsTheLoopbackPort()
    {
        // Port + 1, so two daemons on one machine with different LIGHTDROP_PORT values do not
        // collide on the peer listener as well.
        Assert.Equal(5534, new DaemonEndpointOptions().PeerPort);
        Assert.Equal(9001, new DaemonEndpointOptions { Port = 9000 }.PeerPort);
    }

    [Fact]
    public void PeerPortCanBeSetExplicitly()
    {
        var options = new DaemonEndpointOptions { Port = 9000, PeerPort = 7000 };

        options.Validate();

        Assert.Equal(7000, options.PeerPort);
    }

    [Fact]
    public void RejectsADerivedPeerPortPastTheTopOfTheRange()
    {
        // Port 65535 is legal on its own, but the peer port it implies is not. That must fail
        // loudly at startup, naming the variable that fixes it -- not wrap, and not surface later
        // as an obscure bind error from a listener that only starts when pairing begins.
        var options = new DaemonEndpointOptions { Port = 65535 };

        var failure = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(DaemonEndpointOptions.PeerPortEnvironmentVariable, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void RejectsPeerPortsOutsideTheValidRange(int peerPort)
    {
        var options = new DaemonEndpointOptions { PeerPort = peerPort };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void RejectsAPeerPortEqualToTheLoopbackPort()
    {
        // The peer listener binds every interface, which includes loopback: the two would fight
        // over one socket.
        var options = new DaemonEndpointOptions { Port = 5533, PeerPort = 5533 };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void PeerListenerBindsEveryInterfaceByDefault()
    {
        // The one LAN-reachable socket LightDrop opens. Its gate is the pinned TLS handshake, not
        // the bind address -- the loopback API keeps its own loopback default above.
        Assert.Equal("0.0.0.0", new DaemonEndpointOptions().PeerHost);
    }

    [Fact]
    public void RejectsAPeerHostThatIsNotAnIpAddress()
    {
        var options = new DaemonEndpointOptions { PeerHost = "localhost" };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
