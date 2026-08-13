using LightDrop.Core.Devices;

namespace LightDrop.Core.Discovery;

/// <summary>
/// What a peer claims about itself, after cleaning.
/// </summary>
/// <remarks>
/// Everything here is unverified. A peer is a nearby stranger: nothing about this type implies
/// trust, and nothing derived from it may reach persistent state. Construct only through
/// <see cref="TryCreate"/>, which is the single ingestion chokepoint where untrusted values are
/// sanitized and bounded.
/// </remarks>
public sealed record PeerAnnouncement
{
    /// <summary>Upper bound on an accepted identifier, in UTF-8 bytes.</summary>
    /// <remarks>
    /// Deliberately a loose opaque-token check rather than the current 32-hex-character GUID
    /// shape. Pinning the format here would silently break discovery the day device identity
    /// changes — which pairing may well force.
    /// </remarks>
    public const int MaxDeviceIdBytes = 64;

    /// <summary>Upper bound on a displayed name, in UTF-8 bytes.</summary>
    public const int MaxDeviceNameBytes = 63;

    public const int MaxPlatformBytes = 16;

    public const int MaxCapabilities = 32;

    public const int MaxCapabilityBytes = 32;

    /// <summary>
    /// Private so <see cref="TryCreate"/> is genuinely the only way in.
    /// </summary>
    /// <remarks>
    /// Without this the compiler emits a public parameterless constructor, and any caller could
    /// build one with an object initializer straight from raw network data — bypassing every
    /// sanitization and bound below. The chokepoint has to be enforced by the type, not by a
    /// comment asking people to be careful.
    /// </remarks>
    private PeerAnnouncement()
    {
    }

    public required string DeviceId { get; init; }

    public required string DeviceName { get; init; }

    public required string Platform { get; init; }

    public required int ProtocolVersion { get; init; }

    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>
    /// The port carried in the peer's SRV record.
    /// </summary>
    /// <remarks>
    /// Informational only. It is <strong>not</strong> an authorization boundary and nothing can
    /// be sent to it: LightDrop accepts no inbound commands, and every daemon binds HTTP to
    /// loopback, so this port is not reachable across the network at all.
    /// </remarks>
    public required int Port { get; init; }

    /// <summary>
    /// Cleans and bounds raw announcement data, rejecting only what cannot be salvaged.
    /// </summary>
    /// <returns><c>false</c> when the announcement carries no usable identifier.</returns>
    public static bool TryCreate(
        string? deviceId,
        string? deviceName,
        string? platform,
        int protocolVersion,
        IEnumerable<string>? capabilities,
        int port,
        out PeerAnnouncement? announcement)
    {
        announcement = null;

        var safeDeviceId = UntrustedText.Sanitize(deviceId, MaxDeviceIdBytes);
        if (safeDeviceId.Length == 0)
        {
            // Without an identifier there is nothing to deduplicate or display against.
            return false;
        }

        if (port is < 0 or > 65535)
        {
            return false;
        }

        var safeName = UntrustedText.Sanitize(deviceName, MaxDeviceNameBytes);
        if (safeName.Length == 0)
        {
            // Never show a blank row. Derived from the already-bounded identifier so a peer cannot
            // steer it beyond what it already controls. Sliced by rune rather than by index, or an
            // astral-plane character straddling the boundary would leave a lone surrogate.
            var prefix = string.Concat(safeDeviceId.EnumerateRunes().Take(8).Select(rune => rune.ToString()));
            safeName = $"Peer {prefix}";
        }

        var safePlatform = UntrustedText.Sanitize(platform, MaxPlatformBytes);
        if (safePlatform.Length == 0)
        {
            safePlatform = DevicePlatform.Unknown;
        }

        announcement = new PeerAnnouncement
        {
            DeviceId = safeDeviceId,
            DeviceName = safeName,
            Platform = safePlatform,
            ProtocolVersion = protocolVersion,
            Capabilities = SanitizeCapabilities(capabilities),
            Port = port,
        };

        return true;
    }

    private static IReadOnlyList<string> SanitizeCapabilities(IEnumerable<string>? capabilities)
    {
        if (capabilities is null)
        {
            return [];
        }

        var sanitized = new List<string>();

        foreach (var capability in capabilities)
        {
            if (sanitized.Count == MaxCapabilities)
            {
                break;
            }

            var safe = UntrustedText.Sanitize(capability, MaxCapabilityBytes);
            if (safe.Length > 0)
            {
                sanitized.Add(safe);
            }
        }

        return sanitized;
    }
}
