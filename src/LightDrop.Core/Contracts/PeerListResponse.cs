namespace LightDrop.Core.Contracts;

/// <summary>
/// The response shape of <c>GET /api/peers</c>: the nearby devices heard recently, and enough
/// about discovery itself to interpret them.
/// </summary>
/// <remarks>
/// The peer list alone is ambiguous when empty. Immediately after the daemon starts it means
/// "browsing has not heard back yet"; a minute later it means "something is stopping multicast";
/// and if the transport never started it means "discovery is not running at all". Those need
/// different answers from the user, so the state travels with the list rather than being guessed
/// at the far end.
/// <para>
/// <strong>Every peer here is a stranger.</strong> See <see cref="DiscoveredPeer"/>.
/// </para>
/// </remarks>
public sealed record PeerListResponse
{
    /// <summary>
    /// Whether the discovery transport started successfully and is browsing.
    /// </summary>
    /// <remarks>
    /// False means the daemon is running without discovery — the expected shape of a blocked
    /// firewall or a denied macOS Local Network permission. The daemon still serves
    /// <c>/health</c>, because discovery is a convenience rather than a prerequisite.
    /// </remarks>
    public required bool DiscoveryRunning { get; init; }

    /// <summary>
    /// When discovery started browsing, or <c>null</c> if it never did.
    /// </summary>
    /// <remarks>
    /// Present so a caller can tell "still searching" from "searched long enough to conclude
    /// something is wrong" without this contract hard-coding what long enough means. The clock is
    /// the daemon's, and the only caller is on the same machine over loopback, so there is no
    /// skew to reconcile.
    /// </remarks>
    public required DateTimeOffset? DiscoveryStartedAt { get; init; }

    public required IReadOnlyList<DiscoveredPeer> Peers { get; init; }
}
