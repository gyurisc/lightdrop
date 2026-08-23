using Makaretu.Dns;
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
public sealed class MdnsPeerDiscoveryTransport(ILogger<MdnsPeerDiscoveryTransport> logger) : IPeerDiscoveryTransport
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

    private MulticastService? _mdns;
    private ServiceDiscovery? _discovery;
    private ServiceProfile? _profile;
    private string? _localDeviceId;

    public event Action<PeerAnnouncement>? PeerAnnounced;

    public event Action<string>? PeerGone;

    public Task StartAsync(DeviceIdentity identity, int servicePort, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        _localDeviceId = identity.Id;

        var mdns = new MulticastService(UsableNetworkInterfaces.Filter)
        {
            // IPv4 only this milestone: it avoids link-local scope handling that differs between
            // Windows and macOS, and adding IPv6 later needs no protocol change.
            UseIpv4 = true,
            UseIpv6 = false,
            IgnoreDuplicateMessages = true,
        };

        var discovery = new ServiceDiscovery(mdns);

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

        foreach (var (key, value) in PeerTxtRecord.Build(identity))
        {
            profile.AddProperty(key, value);
        }

        // Held before anything can throw. Advertise and Start below fail on exactly the paths this
        // milestone expects — a blocked firewall, a denied macOS Local Network permission — and if
        // the fields were assigned only afterwards, DisposeAsync would see nulls and leak whatever
        // sockets these had already opened.
        _mdns = mdns;
        _discovery = discovery;
        _profile = profile;

        discovery.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
        discovery.ServiceInstanceShutdown += OnServiceInstanceShutdown;

        // Re-query whenever an interface appears, so plugging in Ethernet or joining Wi-Fi does
        // not require a restart.
        mdns.NetworkInterfaceDiscovered += (_, _) =>
        {
            try
            {
                discovery.QueryServiceInstances(new DomainName(ServiceName));
            }
            catch (Exception ex)
            {
                // One unusable adapter must not take discovery down on the others.
                DiscoveryLog.QueryFailed(logger, ex);
            }
        };

        discovery.Advertise(profile);
        mdns.Start();

        DiscoveryLog.Started(logger, ServiceName, servicePort);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
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

        DiscoveryLog.Stopped(logger);
        return Task.CompletedTask;
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

            // The instance label is the peer's device identifier, by construction above.
            var deviceId = e.ServiceInstanceName.Labels.Count > 0 ? e.ServiceInstanceName.Labels[0] : null;

            if (!string.IsNullOrEmpty(deviceId) && !string.Equals(deviceId, _localDeviceId, StringComparison.Ordinal))
            {
                PeerGone?.Invoke(deviceId);
            }
        }
        catch (Exception ex)
        {
            DiscoveryLog.AnnouncementIgnored(logger, ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        _discovery?.Dispose();
        _mdns?.Dispose();
        return ValueTask.CompletedTask;
    }
}
