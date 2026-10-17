using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using LightDrop.Core.Devices;
using LightDrop.Core.Pairing;
using LightDrop.Daemon.Peers;
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

    /// <summary>Where this daemon saves received files: inside its temp directory, never the real one.</summary>
    public string DownloadFolder => DownloadFolderIn(Directory);

    /// <summary>The files in <see cref="DownloadFolder"/>, by name; empty if it does not exist yet.</summary>
    public IReadOnlyList<string> ReceivedFiles =>
        System.IO.Directory.Exists(DownloadFolder)
            ? [.. System.IO.Directory.EnumerateFileSystemEntries(DownloadFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal)!]
            : [];

    private static string DownloadFolderIn(TempDataDirectory directory) => Path.Combine(directory.FullPath, "Downloads");

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

        // The download folder is always inside the temp directory and deliberately not created:
        // no test may ever write to the real ~/Downloads, and the daemon must create it itself.
        directory.WriteConfig(JsonSerializer.Serialize(
            new LightDropConfig { DeviceName = name, DownloadFolder = DownloadFolderIn(directory) },
            LightDropJsonContext.Default.LightDropConfig));
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

    /// <summary>
    /// Pins <paramref name="other"/>'s key under its name, as a completed pairing would, and makes
    /// sure the peer listener is up. For tests about what comes after pairing; the ceremony
    /// itself is <c>PairingFlowTests</c>' subject.
    /// </summary>
    public async Task TrustAsync(TestDaemon other)
    {
        var key = await other.GetKeyAsync();
        await App.Services.GetRequiredService<PairingService>().PinAsync(other.DeviceId, other.Name, key.PublicKeyInfo);
        await App.Services.GetRequiredService<PeerListener>().EnsureStartedAsync(CancellationToken.None);
    }

    public async Task<HealthResponse> GetHealthAsync() =>
        (await Api.GetFromJsonAsync("health", LightDropJsonContext.Default.HealthResponse))!;

    /// <summary><c>POST /api/send</c>, as <c>lightdrop send</c> calls it.</summary>
    public Task<HttpResponseMessage> SendAsync(string path, string peer, Guid? transferId = null) =>
        Api.PostAsJsonAsync(
            "api/send",
            new SendRequest { Path = path, Peer = peer, TransferId = transferId },
            LightDropJsonContext.Default.SendRequest);

    /// <summary><c>GET /api/send/{id}</c>, as <c>lightdrop send</c> polls it.</summary>
    public Task<HttpResponseMessage> GetSendProgressAsync(Guid transferId) =>
        Api.GetAsync($"api/send/{transferId}");

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
