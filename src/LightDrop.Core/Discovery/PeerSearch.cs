using LightDrop.Core.Contracts;

namespace LightDrop.Core.Discovery;

/// <summary>What an empty peer list means right now.</summary>
public enum PeerSearchState
{
    /// <summary>Discovery is not running; the list cannot fill.</summary>
    Stopped,

    /// <summary>Discovery is running but has not had long enough to conclude anything.</summary>
    Searching,

    /// <summary>Discovery has been running long enough that hearing nothing is meaningful.</summary>
    Silent,

    /// <summary>At least one peer is known.</summary>
    PeersFound,
}

/// <summary>
/// Reads a <see cref="PeerListResponse"/> as one of four situations.
/// </summary>
/// <remarks>
/// Lives in Core rather than in the CLI because it is a rule, not a rendering: given the same
/// response it must always reach the same conclusion, and that is worth pinning with tests. The
/// CLI decides the words.
/// </remarks>
public static class PeerSearch
{
    /// <summary>
    /// How long discovery must run before an empty list is worth explaining.
    /// </summary>
    /// <remarks>
    /// Measured, not guessed: on a working Windows-to-macOS pair a freshly started daemon still
    /// listed nothing at 30 seconds and had the peer by 96, with the other machine advertising
    /// unchanged throughout. Two minutes clears that observation with margin. Erring long is the
    /// safe direction — a premature "check your firewall" sends someone after a problem they do
    /// not have, while a late one costs them a few seconds of waiting.
    /// </remarks>
    public static readonly TimeSpan SettlesAfter = TimeSpan.FromSeconds(120);

    public static PeerSearchState Classify(PeerListResponse response, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);

        // Checked before the peer list: a stopped transport cannot refresh what it already heard,
        // so any remaining rows are memories rather than presence.
        if (!response.DiscoveryRunning || response.DiscoveryStartedAt is not { } startedAt)
        {
            return PeerSearchState.Stopped;
        }

        if (response.Peers.Count > 0)
        {
            return PeerSearchState.PeersFound;
        }

        return now - startedAt < SettlesAfter ? PeerSearchState.Searching : PeerSearchState.Silent;
    }
}
