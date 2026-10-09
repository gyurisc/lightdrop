using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using LightDrop.Core.Devices;
using LightDrop.Core.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace LightDrop.Daemon.Tests.TestSupport;

/// <summary>
/// A real daemon on loopback with its own data directory, a fake discovery transport, and a
/// peer listener kept on loopback.
/// </summary>
/// <remarks>
/// The device id is fixed by the test, because pairing roles follow from comparing two ids: a
/// test that let them be random would exercise whichever role the dice chose.
/// </remarks>
internal sealed class TestDaemon : IAsyncDisposable
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(20);

    private TestDaemon(
        string deviceId,
        string name,
        TempDataDirectory directory,
        DaemonEndpointOptions endpoint,
        FakePeerDiscoveryTransport transport,
        WebApplication app)
    {
        DeviceId = deviceId;
        Name = name;
        Directory = directory;
        Endpoint = endpoint;
        Transport = transport;
        App = app;
        Api = new HttpClient { BaseAddress = endpoint.ClientAddress, Timeout = TimeSpan.FromSeconds(30) };
    }

    public string DeviceId { get; }

    public string Name { get; }

    public TempDataDirectory Directory { get; }

    public DaemonEndpointOptions Endpoint { get; }

    public FakePeerDiscoveryTransport Transport { get; }

    public WebApplication App { get; }

    /// <summary>The loopback API, as the CLI would call it.</summary>
    public HttpClient Api { get; }

    public Uri PeerAddress => new($"https://127.0.0.1:{Endpoint.PeerPort}/");

    /// <param name="deviceId">Fixes the role this daemon plays: lower id listens.</param>
    /// <param name="trusted">Peers already pinned when the daemon starts.</param>
    public static Task<TestDaemon> StartAsync(
        string deviceId,
        string name,
        params TrustedPeer[] trusted) =>
        StartAsync(deviceId, name, trusted, clock: null);

    /// <param name="clock">Pairing deadlines are measured against this; real time when null.</param>
    /// <param name="peerHost">Where the peer listener binds. Loopback unless a test needs a bind to fail.</param>
    public static async Task<TestDaemon> StartAsync(
        string deviceId,
        string name,
        TrustedPeer[] trusted,
        TimeProvider? clock,
        string peerHost = "127.0.0.1")
    {
        var directory = new TempDataDirectory();

        directory.WriteConfig(JsonSerializer.Serialize(
            new LightDropConfig { DeviceName = name }, LightDropJsonContext.Default.LightDropConfig));
        directory.WriteState(JsonSerializer.Serialize(
            new LightDropState { DeviceId = deviceId, TrustedPeers = trusted }, LightDropJsonContext.Default.LightDropState));

        var endpoint = new DaemonEndpointOptions
        {
            Host = "127.0.0.1",
            Port = FreeTcpPort.Get(),
            PeerPort = FreeTcpPort.Get(),

            // Never the default 0.0.0.0: a test must not open a LAN-reachable socket.
            PeerHost = peerHost,
        };

        var transport = new FakePeerDiscoveryTransport();
        var app = LightDropDaemon.Create(endpoint, directory.FullPath, transport, clock ?? TimeProvider.System);
        await app.StartAsync(CancellationToken.None);

        return new TestDaemon(deviceId, name, directory, endpoint, transport, app);
    }

    public Task<DeviceKeyPair> GetKeyAsync() =>
        App.Services.GetRequiredService<DeviceKeyProvider>().GetAsync(CancellationToken.None).AsTask();

    /// <summary>Makes this daemon hear <paramref name="other"/> on its peer port.</summary>
    public void Discover(TestDaemon other) =>
        Transport.Announce(other.DeviceId, other.Name, port: other.Endpoint.PeerPort, address: "127.0.0.1");

    public Task<HttpResponseMessage> StartPairingAsync(string peer) =>
        Api.PostAsJsonAsync(
            "api/pairing", new StartPairingRequest { Peer = peer }, LightDropJsonContext.Default.StartPairingRequest);

    public Task<HttpResponseMessage> ConfirmAsync(bool accepted) =>
        Api.PostAsJsonAsync(
            "api/pairing/confirm", new PairingDecision { Accepted = accepted }, LightDropJsonContext.Default.PairingDecision);

    public async Task<PairingStatus> GetPairingAsync() =>
        (await Api.GetFromJsonAsync("api/pairing", LightDropJsonContext.Default.PairingStatus))!;

    public async Task<IReadOnlyList<TrustedPeerSummary>> GetTrustedAsync() =>
        (await Api.GetFromJsonAsync("api/trusted", LightDropJsonContext.Default.TrustedPeerListResponse))!.Peers;

    /// <summary>Polls the session until it reaches <paramref name="state"/>, failing with the last seen status.</summary>
    public async Task<PairingStatus> WaitForStateAsync(PairingState state)
    {
        var stopwatch = Stopwatch.StartNew();
        PairingStatus status;

        do
        {
            status = await GetPairingAsync();
            if (status.State == state)
            {
                return status;
            }

            await Task.Delay(50);
        }
        while (stopwatch.Elapsed < WaitBudget);

        Assert.Fail($"{Name} never reached {state}; last seen {status.State} ({status.Reason}).");
        return status;
    }

    /// <summary>Polls until the session leaves the open states, returning where it ended.</summary>
    public async Task<PairingStatus> WaitForEndAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        PairingStatus status;

        do
        {
            status = await GetPairingAsync();
            if (status.State is not (PairingState.WaitingForPeer or PairingState.Confirming))
            {
                return status;
            }

            await Task.Delay(50);
        }
        while (stopwatch.Elapsed < WaitBudget);

        Assert.Fail($"{Name}'s session never ended; last seen {status.State}.");
        return status;
    }

    public async ValueTask DisposeAsync()
    {
        Api.Dispose();
        await App.StopAsync(CancellationToken.None);
        await App.DisposeAsync();
        Directory.Dispose();
    }
}
