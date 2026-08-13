using LightDrop.Core.Configuration;

namespace LightDrop.Core.Tests;

public sealed class DaemonEndpointOptionsTests
{
    [Fact]
    public void DefaultsToLoopbackUntilPairingExists()
    {
        // Binding to the LAN before there is any authentication would expose an unauthenticated
        // endpoint to every device on the network. This default is a security decision.
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
}
