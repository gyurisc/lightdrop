namespace LightDrop.Core.Pairing;

/// <summary>How a lookup came out.</summary>
public enum PeerLookupOutcome
{
    Found,
    NotFound,
    Ambiguous,
}

/// <summary>The device a query named, or the candidates that make it ambiguous.</summary>
public sealed record PeerLookupResult<T>(PeerLookupOutcome Outcome, T? Match, IReadOnlyList<T> Candidates)
    where T : class;

/// <summary>
/// Resolves what a user typed -- a device name or a device-id prefix -- to exactly one device.
/// </summary>
/// <remarks>
/// Generic over the row type because <c>pair</c> looks among discovered peers and <c>unpair</c>
/// among trusted ones, and the rule must be the same for both.
/// <para>
/// <strong>It never guesses.</strong> Names are chosen by whoever announces them and are not
/// unique, so a name shared by two devices, or a name that is also another device's id prefix,
/// is ambiguous and the caller has to say which. The one exception is a full device id, which
/// always wins: it is the handle a user falls back on when names collide, and a hostile device
/// naming itself after a real one's id must not be able to take that away.
/// </para>
/// </remarks>
public static class PeerLookup
{
    public static PeerLookupResult<T> Find<T>(
        string? query,
        IEnumerable<T> peers,
        Func<T, string> deviceId,
        Func<T, string> deviceName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(deviceName);

        var wanted = query?.Trim();
        if (string.IsNullOrEmpty(wanted))
        {
            // An empty prefix matches every id, which would pick whichever device happened to be
            // alone in the list.
            return new PeerLookupResult<T>(PeerLookupOutcome.NotFound, null, []);
        }

        var candidates = peers.ToArray();

        var exactId = candidates
            .Where(peer => string.Equals(deviceId(peer), wanted, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exactId.Length == 1)
        {
            return new PeerLookupResult<T>(PeerLookupOutcome.Found, exactId[0], exactId);
        }

        var matches = candidates
            .Where(peer =>
                string.Equals(deviceName(peer), wanted, StringComparison.OrdinalIgnoreCase)
                || deviceId(peer).StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length switch
        {
            0 => new PeerLookupResult<T>(PeerLookupOutcome.NotFound, null, []),
            1 => new PeerLookupResult<T>(PeerLookupOutcome.Found, matches[0], matches),
            _ => new PeerLookupResult<T>(PeerLookupOutcome.Ambiguous, null, matches),
        };
    }
}
