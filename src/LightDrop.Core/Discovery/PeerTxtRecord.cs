using System.Globalization;
using LightDrop.Core.Devices;

namespace LightDrop.Core.Discovery;

/// <summary>
/// The DNS-SD TXT record LightDrop advertises, and how to read one back.
/// </summary>
/// <remarks>
/// Lives in Core, not in the mDNS transport: it deals only in strings and integers, with no
/// networking types, so keeping it here makes the parsing testable without multicast — which the
/// transport around it can never be.
/// <para>
/// Minimal presence metadata only. Deliberately excluded: the username, filesystem paths, the
/// download folder, the config location, anything key-shaped, and the application version.
/// <see cref="LightDropVersion.Protocol"/> is the compatibility gate, so broadcasting an exact
/// build number would hand a passive observer a version fingerprint for no product benefit; it
/// stays available on loopback via <c>GET /health</c>.
/// </para>
/// </remarks>
public static class PeerTxtRecord
{
    /// <summary>Shape of this record, independent of the wire protocol version.</summary>
    public const string TxtVersionKey = "txtvers";

    public const string DeviceIdKey = "id";
    public const string ProtocolVersionKey = "pv";
    public const string PlatformKey = "plat";
    public const string DeviceNameKey = "name";
    public const string CapabilitiesKey = "cap";

    public const string TxtVersion = "1";

    /// <summary>
    /// Builds the key/value pairs advertised for this device.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Build(DeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return
        [
            new(TxtVersionKey, TxtVersion),
            new(DeviceIdKey, identity.Id),
            new(ProtocolVersionKey, LightDropVersion.Protocol.ToString(CultureInfo.InvariantCulture)),
            new(PlatformKey, DevicePlatform.Current),
            new(DeviceNameKey, identity.Name),

            // `cap` is omitted entirely while empty: in DNS-SD an absent key is meaningfully
            // different from a present-but-empty one, and there is no reason to spend bytes on
            // nothing. It appears when the first command handler ships.
        ];
    }

    /// <summary>
    /// Reads an announcement out of raw <c>key=value</c> TXT strings.
    /// </summary>
    /// <remarks>
    /// Every value here is attacker-controlled. Parsing is total: malformed input yields
    /// <c>false</c> rather than throwing into the transport's event loop, and
    /// <see cref="PeerAnnouncement.TryCreate"/> sanitizes and bounds whatever survives.
    /// </remarks>
    public static bool TryParse(
        IEnumerable<string> txtStrings, int port, string? address, out PeerAnnouncement? announcement)
    {
        ArgumentNullException.ThrowIfNull(txtStrings);

        announcement = null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in txtStrings)
        {
            if (entry is null)
            {
                continue;
            }

            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                // A bare key with no value carries nothing we need; skip it rather than reject the
                // whole record.
                continue;
            }

            // First occurrence wins, per RFC 6763.
            values.TryAdd(entry[..separator], entry[(separator + 1)..]);
        }

        values.TryGetValue(DeviceIdKey, out var deviceId);
        values.TryGetValue(DeviceNameKey, out var deviceName);
        values.TryGetValue(PlatformKey, out var platform);

        // The key must be present, though its value need not be sensible. Browsing a service type
        // does not guarantee only that service type is delivered, so records belonging to other
        // protocols reach this parser; a Google Cast announcement carries an `id` key, which was
        // once enough to mint a peer and put a television in the peer list. Every LightDrop
        // record carries `pv`, so its absence is the cheapest reliable "not ours".
        if (!values.TryGetValue(ProtocolVersionKey, out var rawProtocolVersion))
        {
            return false;
        }

        // A non-numeric value means a malformed peer, not a reason to discard it entirely.
        _ = int.TryParse(rawProtocolVersion, CultureInfo.InvariantCulture, out var protocolVersion);

        var capabilities = values.TryGetValue(CapabilitiesKey, out var rawCapabilities)
            ? rawCapabilities.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        return PeerAnnouncement.TryCreate(
            deviceId, deviceName, platform, protocolVersion, capabilities, port, address, out announcement);
    }
}
