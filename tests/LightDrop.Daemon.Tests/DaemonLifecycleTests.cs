using LightDrop.Core.Configuration;
using LightDrop.Daemon.Discovery;
using LightDrop.Daemon.Tests.TestSupport;

namespace LightDrop.Daemon.Tests;

public sealed class DaemonLifecycleTests
{
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task StartsServingAndThenStopsCleanlyWhenCancelled()
    {
        using var directory = new TempDataDirectory();
        var endpoint = new DaemonEndpointOptions { Host = "127.0.0.1", Port = FreeTcpPort.Get() };
        using var cancellation = new CancellationTokenSource();

        var run = LightDropDaemon.RunAsync(
            endpoint, directory.FullPath, cancellation.Token, new NoOpPeerDiscoveryTransport());

        using var client = new HttpClient { BaseAddress = endpoint.ClientAddress, Timeout = TimeSpan.FromSeconds(2) };

        // Confirm it actually came up before cancelling. Cancelling a host that never started
        // would also "complete quickly", for entirely the wrong reason.
        Assert.True(await DaemonProbe.WaitUntilServingAsync(client));

        await cancellation.CancelAsync();

        // Raced against a generous budget rather than asserting a duration: a wall-clock
        // assertion is exactly what flakes on a loaded CI runner.
        var finished = await Task.WhenAny(run, Task.Delay(ShutdownBudget));
        Assert.Same(run, finished);

        // Surfaces a faulted shutdown rather than letting it pass as "completed".
        await run;

        // The real proof the listener released the socket, independent of the task completing.
        // A fresh client avoids a pooled connection reporting a false success.
        using var probe = new HttpClient { BaseAddress = endpoint.ClientAddress, Timeout = TimeSpan.FromSeconds(2) };
        Assert.False(await DaemonProbe.IsServingAsync(probe));
    }

    [Fact]
    public async Task FailsToStartOnAnInvalidEndpointRatherThanBindingSomethingUnexpected()
    {
        using var directory = new TempDataDirectory();
        var endpoint = new DaemonEndpointOptions { Host = "not-an-ip-address", Port = 5533 };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await LightDropDaemon.RunAsync(
                endpoint, directory.FullPath, CancellationToken.None, new NoOpPeerDiscoveryTransport()));
    }
}
