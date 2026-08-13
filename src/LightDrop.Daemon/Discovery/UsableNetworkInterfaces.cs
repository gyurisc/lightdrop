using System.Net.NetworkInformation;

namespace LightDrop.Daemon.Discovery;

/// <summary>
/// Picks the interfaces worth advertising and browsing on.
/// </summary>
/// <remarks>
/// A filter, not a seam — there is nothing to substitute, only real hardware to verify against.
/// <para>
/// Status and multicast-capability checks alone are not enough. A Windows development machine
/// routinely has Hyper-V, WSL and Docker adapters that all report up, Ethernet, and
/// multicast-capable while sitting on isolated subnets that never reach the real LAN. macOS
/// surfaces <c>awdl0</c> (Apple Wireless Direct Link, used by AirDrop) and <c>utun</c> tunnels
/// that exist even with no VPN installed.
/// </para>
/// </remarks>
internal static class UsableNetworkInterfaces
{
    // Matched against the adapter name on macOS and the description on Windows, since the two
    // platforms expose these very differently.
    private static readonly string[] ExcludedFragments =
    [
        // macOS pseudo-interfaces.
        "awdl", "llw", "utun", "bridge", "gif0", "stf0", "ap1",

        // Windows virtualisation and VPN adapters.
        "hyper-v", "vethernet", "virtual", "wsl", "docker", "vmware", "virtualbox", "tap-", "tailscale",
    ];

    public static IEnumerable<NetworkInterface> Filter(IEnumerable<NetworkInterface> candidates) =>
        candidates.Where(IsUsable);

    private static bool IsUsable(NetworkInterface adapter)
    {
        if (adapter.OperationalStatus != OperationalStatus.Up || !adapter.SupportsMulticast)
        {
            return false;
        }

        // Loopback is kept deliberately: it is how two daemons on one machine find each other,
        // which is the only way to exercise discovery without a second computer.
        if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
        {
            return true;
        }

        return !IsExcluded(adapter.Name) && !IsExcluded(adapter.Description);
    }

    private static bool IsExcluded(string value) =>
        ExcludedFragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
