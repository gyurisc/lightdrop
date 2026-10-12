using Makaretu.Dns;
using LightDrop.Core.Commands;
using LightDrop.Core.Devices;
using LightDrop.Core.Discovery;
using Microsoft.Extensions.Logging;

namespace LightDrop.Daemon.Discovery;

/// <summary>
/// Presence over mDNS: advertises <c>_lightdrop._tcp.local</c> and listens for others.
/// </summary>
/// <remarks>
/// Not unit tested, deliberately. Testing it would need either real multicast — which CI runners
/// cannot route, and which macOS drops silently without the Local Network permission — or mocks
/// of the library's internals, which would only re-test the library. It is verified by hand on
/// two machines; see the checklist in <c>docs/Roadmap.md</c>.
/// </remarks>
public sealed class MdnsPeerDiscoveryTransport(
    CommandCapabilities capabilities,
    ILogger<MdnsPeerDiscoveryTransport> logger) : IPeerDiscoveryTransport
{
    /// <summary>The DNS-SD service type. Nine characters, inside the 15-character limit.</summary>
    public const string ServiceName = "_lightdrop._tcp";

    /// <summary>
    /// The fully qualified service type, used to reject instances of other services.
    /// </summary>
    /// <remarks>
    /// Browsing one service type does not mean only that type is delivered: the library raises
    /// discovery events for every instance it sees on the link. Without this check any TXT record
    /// carrying an <c>id</c> key reached the parser, and a Google Cast television was minted as a
    /// peer.
    /// </remarks>
    private static readonly DomainName ServiceDomain = new($"{ServiceName}.local");

    /// <summary>How often this device asks for peers and announces itself.</summary>
    /// <remarks>
    /// A third of <see cref="PeerRegistry.StaleAfter"/>, so a peer must miss three refreshes in a
    /// row before it is dropped. Nothing else refreshes the registry: the library announces nothing
    /// on its own and, on macOS, re-queries only when it sees a new interface — so without this
    /// every peer vanished three minutes after start.
    /// </remarks>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    /// <summary>How long without hearing its own announcement before discovery reopens its sockets.</summary>
    /// <remarks>
    /// Two and a half refresh intervals: two announcements missed, acted on at the third refresh.
    /// See <see cref="DiscoveryWatchdog"/>.
    /// </remarks>
    public static readonly TimeSpan DeafAfter = RefreshInterval * 2.5;

    // Guards swapping the sockets out (a rebuild on the timer thread) against stopping.
    private readonly Lock _gate = new();
    private MulticastService? _mdns;
    private ServiceDiscovery? _discovery;
    private ServiceProfile? _profile;
    private DiscoveryWatchdog? _watchdog;
    private ITimer? _refresh;
    private string? _localDeviceId;
    private bool _stopped;

    public event Action<PeerAnnouncement>? PeerAnnounced;

    public event Action<string>? PeerGone;

    public Task StartAsync(DeviceIdentity identity, int servicePort, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        _localDeviceId = identity.Id;

        // The instance name is the device identifier, not the device name. Two machines can
        // legitimately share a name, and an identifier collision is not a realistic event — which
        // sidesteps the library's DNS-SD name-conflict handling entirely. It also avoids
        // broadcasting a human-readable name as the browsable label; the friendly name travels in
        // the TXT record, which is what `lightdrop peers` renders.
        var profile = new ServiceProfile(
            new DomainName(identity.Id),
            new DomainName(ServiceName),
            (ushort)servicePort,
            addresses: null,
            sharedProfile: false);

        foreach (var (key, value) in PeerTxtRecord.Build(identity, capabilities.Names))
        {
            profile.AddProperty(key, value);
        }

        _profile = profile;
        _watchdog = new DiscoveryWatchdog(TimeProvider.System, DeafAfter);

        lock (_gate)
        {
            OpenSockets(profile);
        }

        // Due immediately, so peers hear the first announcement without waiting a full interval.
        _refresh = TimeProvider.System.CreateTimer(_ => Refresh(), state: null, TimeSpan.Zero, RefreshInterval);

        DiscoveryLog.Started(logger, ServiceName, servicePort);
        return Task.CompletedTask;
    }

    /// <summary>Opens the multicast sockets and starts answering for <paramref name="profile"/>.</summary>
    /// <remarks>Callers hold <see cref="_gate"/>.</remarks>
    private void OpenSockets(ServiceProfile profile)
    {
        var mdns = new MulticastService(UsableNetworkInterfaces.Filter)
        {
            // IPv4 only this milestone: it avoids link-local scope handling that differs between
            // Windows and macOS, and adding IPv6 later needs no protocol change.
            UseIpv4 = true,
            UseIpv6 = false,
            IgnoreDuplicateMessages = true,
        };

        var discovery = new ServiceDiscovery(mdns);

        // Held before anything can throw. Advertise and Start below fail on exactly the paths this
        // milestone expects — a blocked firewall, a denied macOS Local Network permission — and if
        // the fields were assigned only afterwards, DisposeAsync would see nulls and leak whatever
        // sockets these had already opened.
        _mdns = mdns;
        _discovery = discovery;

        discovery.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
        discovery.ServiceInstanceShutdown += OnServiceInstanceShutdown;

        // Re-query whenever an interface appears, so plugging in Ethernet or joining Wi-Fi does
        // not require a restart.
        mdns.NetworkInterfaceDiscovered += (_, _) => Query(discovery);

        discovery.Advertise(profile);
        mdns.Start();
    }

    /// <summary>Closes the sockets without a goodbye: the device is not leaving.</summary>
    /// <remarks>Callers hold <see cref="_gate"/>.</remarks>
    private void CloseSockets()
    {
        try
        {
            _discovery?.Dispose();
            _mdns?.Dispose();
        }
        catch (Exception ex)
        {
            DiscoveryLog.StopFailed(logger, ex);
        }

        _discovery = null;
        _mdns = null;
    }

    /// <summary>Reopens deaf sockets, asks for peers, then announces this device.</summary>
    /// <remarks>
    /// Asking and announcing are both needed on a network that carries multicast in one direction
    /// only. Seen 2026-10-12: a router delivered a Wi-Fi MacBook Air's multicast to an Ethernet Mac
    /// Mini but none of the Mini's to the Air, while unicast worked both ways. The Air learns of the
    /// Mini from the Mini's unicast answer to the Air's QU question (<see cref="Query"/>); the Mini,
    /// whose questions never reach the Air, learns of the Air from the Air's multicast announcement.
    /// The announcement is also the watchdog's heartbeat: this device should hear it come back.
    /// </remarks>
    private void Refresh()
    {
        ServiceDiscovery? discovery;
        ServiceProfile? profile;

        lock (_gate)
        {
            if (_stopped || _profile is null || _watchdog is null)
            {
                return;
            }

            var health = _watchdog.Check();
            if (health != DiscoveryHealth.Healthy)
            {
                if (health == DiscoveryHealth.WentDeaf)
                {
                    DiscoveryLog.WentDeaf(logger, (int)DeafAfter.TotalSeconds);
                }
                else
                {
                    DiscoveryLog.StillDeaf(logger);
                }

                CloseSockets();

                try
                {
                    OpenSockets(_profile);
                }
                catch (Exception ex)
                {
                    // Tried again at the next refresh, once the watchdog's window has passed.
                    DiscoveryLog.ReopenFailed(logger, ex);
                    return;
                }
            }

            discovery = _discovery;
            profile = _profile;
        }

        if (discovery is null)
        {
            return;
        }

        Query(discovery);

        try
        {
            // The library's minimum: two unsolicited responses, about a second apart, blocking
            // this timer callback in between. Once a minute that is harmless.
            discovery.Announce(profile, numberOfTimes: 2);
        }
        catch (Exception ex)
        {
            DiscoveryLog.AnnounceFailed(logger, ex);
        }
    }

    /// <summary>Asks every peer on the link to answer this device directly.</summary>
    /// <remarks>
    /// A QU question: the unicast-response bit asks responders to reply to the querier rather than
    /// to the multicast group, so a device that receives no multicast still hears the answers.
    /// Unicast gets through wherever pairing and transfer could anyway; both are unicast.
    /// </remarks>
    private void Query(ServiceDiscovery discovery)
    {
        try
        {
            discovery.QueryUnicastServiceInstances(new DomainName(ServiceName));
        }
        catch (Exception ex)
        {
            // One unusable adapter must not take discovery down on the others, and a failed
            // periodic query must not kill the timer.
            DiscoveryLog.QueryFailed(logger, ex);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_refresh is not null)
        {
            // Waits out a callback in flight, so no announcement or rebuild can follow the goodbye
            // below and list this device again on its peers until it ages out.
            await _refresh.DisposeAsync().ConfigureAwait(false);
        }

        lock (_gate)
        {
            _stopped = true;

            if (_discovery is not null && _profile is not null)
            {
                try
                {
                    // Goodbye: peers drop this device immediately rather than waiting out the
                    // staleness window.
                    _discovery.Unadvertise(_profile);
                }
                catch (Exception ex)
                {
                    DiscoveryLog.GoodbyeFailed(logger, ex);
                }
            }

            try
            {
                _mdns?.Stop();
            }
            catch (Exception ex)
            {
                DiscoveryLog.StopFailed(logger, ex);
            }
        }

        DiscoveryLog.Stopped(logger);
    }

    /// <summary>
    /// Where the peer appears to be: the source of the packet, or failing that what it claimed.
    /// </summary>
    /// <remarks>
    /// The source address is preferred because it is what the network observed rather than what
    /// the sender asserted — an announcer can put any address in an A record, including a third
    /// party's. The claimed record is a fallback for the case where the library surfaces no
    /// endpoint. Neither is trusted here: <see cref="PeerAnnouncement.TryCreate"/> is the only
    /// thing that decides an address is acceptable.
    /// </remarks>
    private static string? AddressOf(
        ServiceInstanceDiscoveryEventArgs e, IReadOnlyList<ResourceRecord> records, SRVRecord? srv)
    {
        if (e.RemoteEndPoint?.Address is { } source)
        {
            return source.ToString();
        }

        var claimed = records
            .OfType<ARecord>()
            .FirstOrDefault(record => srv is null || record.Name.Equals(srv.Target));

        return claimed?.Address.ToString();
    }

    private void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        try
        {
            if (!e.ServiceInstanceName.IsSubdomainOf(ServiceDomain))
            {
                return;
            }

            // Our own announcement, looped back: the watchdog's proof that the sockets work. The
            // registry would discard it anyway.
            if (IsThisDevice(e.ServiceInstanceName))
            {
                if (_watchdog?.HeardSelf() == true)
                {
                    DiscoveryLog.Recovered(logger);
                }

                return;
            }

            var records = e.Message.Answers.Concat(e.Message.AdditionalRecords).ToArray();

            var txt = records
                .OfType<TXTRecord>()
                .FirstOrDefault(record => record.Name.Equals(e.ServiceInstanceName));

            if (txt is null)
            {
                return;
            }

            var srv = records
                .OfType<SRVRecord>()
                .FirstOrDefault(record => record.Name.Equals(e.ServiceInstanceName));

            if (PeerTxtRecord.TryParse(txt.Strings, srv?.Port ?? 0, AddressOf(e, records, srv), out var announcement)
                && announcement is not null)
            {
                PeerAnnounced?.Invoke(announcement);
            }
        }
        catch (Exception ex)
        {
            // A hostile or buggy peer must not be able to kill the listener by sending something
            // unexpected. Everything on this path is untrusted network data.
            DiscoveryLog.AnnouncementIgnored(logger, ex);
        }
    }

    private void OnServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        try
        {
            if (!e.ServiceInstanceName.IsSubdomainOf(ServiceDomain))
            {
                return;
            }

            var deviceId = DeviceIdOf(e.ServiceInstanceName);

            if (!string.IsNullOrEmpty(deviceId) && !IsThisDevice(e.ServiceInstanceName))
            {
                PeerGone?.Invoke(deviceId);
            }
        }
        catch (Exception ex)
        {
            DiscoveryLog.AnnouncementIgnored(logger, ex);
        }
    }

    /// <summary>The instance label: a peer's device identifier, by construction in StartAsync.</summary>
    private static string? DeviceIdOf(DomainName instance) =>
        instance.Labels.Count > 0 ? instance.Labels[0] : null;

    private bool IsThisDevice(DomainName instance) =>
        string.Equals(DeviceIdOf(instance), _localDeviceId, StringComparison.Ordinal);

    public ValueTask DisposeAsync()
    {
        _refresh?.Dispose();

        lock (_gate)
        {
            // Also stops a rebuild already waiting for the lock from opening fresh sockets.
            _stopped = true;
            CloseSockets();
        }

        return ValueTask.CompletedTask;
    }
}
