using System.Net;
using System.Net.Sockets;

namespace LightDrop.Core.Discovery;

/// <summary>
/// The addresses a peer is allowed to claim, and the only way one enters the system.
/// </summary>
/// <remarks>
/// Discovery captures an address because pairing has to dial something; until then a peer could
/// only misinform, and now it can attempt to steer. The whole announcement is attacker-controlled,
/// so the address is checked here at ingestion rather than where a connection is eventually
/// opened — the same rule the rest of <see cref="PeerAnnouncement"/> follows.
/// <para>
/// This is a range check, not a reachability check. Confirming an address really sits on one of
/// this machine's subnets would mean enumerating interfaces, which is exactly the OS-specific
/// dependency Core must not take. The range check is what stops the attack that matters — a peer
/// naming a host on the internet so that pairing opens a connection to a third party — and the
/// residual risk is a peer naming a different local machine, which is already on the same link
/// and can announce for itself anyway.
/// </para>
/// </remarks>
public static class LocalNetworkAddress
{
    /// <summary>
    /// Accepts an address on this link, rejecting everything else.
    /// </summary>
    /// <returns><c>false</c> when nothing usable was supplied.</returns>
    public static bool TryNormalize(string? candidate, out string? address)
    {
        address = null;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        // IPAddress.TryParse accepts forms nobody advertises — and historically some that are
        // ambiguous — so the round-tripped text is what gets stored, never the raw input.
        if (!IPAddress.TryParse(candidate.Trim(), out var parsed))
        {
            return false;
        }

        // IPv4 only, matching what the transport advertises and browses. IPv6 needs link-local
        // scope handling that differs between Windows and macOS; it is a later decision, not an
        // accident of what parsed.
        if (parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var octets = parsed.GetAddressBytes();

        if (!IsSingleHost(parsed, octets) || !IsOnThisLink(octets))
        {
            return false;
        }

        address = parsed.ToString();
        return true;
    }

    private static bool IsSingleHost(IPAddress parsed, byte[] octets) =>
        !parsed.Equals(IPAddress.Any)
        && !parsed.Equals(IPAddress.Broadcast)
        // 224.0.0.0/4. A multicast group is not a host, and the announcement arrived over one.
        && octets[0] is not (>= 224 and <= 239);

    private static bool IsOnThisLink(byte[] octets) => octets[0] switch
    {
        // Loopback, kept because two daemons on one machine is a supported way to exercise
        // discovery — the interface filter keeps loopback for the same reason.
        127 => true,

        10 => true,
        172 => octets[1] is >= 16 and <= 31,
        192 => octets[1] == 168,

        // Link-local: what a machine gives itself when DHCP does not answer. Two such machines on
        // one switch can still discover each other.
        169 => octets[1] == 254,

        _ => false,
    };
}
