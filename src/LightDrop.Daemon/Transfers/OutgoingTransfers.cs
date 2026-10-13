using System.Collections.Concurrent;
using LightDrop.Core.Contracts;

namespace LightDrop.Daemon.Transfers;

/// <summary>
/// The sends under way, by the id their caller chose, so <c>GET /api/send/{id}</c> can say how far
/// each has got.
/// </summary>
/// <remarks>
/// Polled rather than pushed: <c>POST /api/send</c> keeps its single answer and its status codes,
/// and a CLI talking to an older daemon -- one that ignores the id -- simply shows no progress.
/// Pairing works the same way: the daemon holds the state and the CLI asks. An entry lives exactly
/// as long as its send, so the set is bounded by the sends running, each holding a request open.
/// </remarks>
internal sealed class OutgoingTransfers
{
    private readonly ConcurrentDictionary<Guid, Entry> _running = new();

    /// <summary>Reports a send under <paramref name="id"/> until the returned handle is disposed.</summary>
    /// <param name="sent">Bytes sent so far; called from whichever thread is asking.</param>
    /// <returns><c>null</c> if a send with this id is already running.</returns>
    public IDisposable? Begin(Guid id, long length, Func<long> sent)
    {
        var entry = new Entry(length, sent);
        return _running.TryAdd(id, entry) ? new Registration(_running, id, entry) : null;
    }

    /// <summary>How far the send with this id has got, or <c>null</c> if none is running.</summary>
    public SendProgress? Find(Guid id) =>
        _running.TryGetValue(id, out var entry)
            ? new SendProgress { Length = entry.Length, Sent = Math.Min(entry.Sent(), entry.Length) }
            : null;

    private sealed record Entry(long Length, Func<long> Sent);

    private sealed class Registration(ConcurrentDictionary<Guid, Entry> running, Guid id, Entry entry) : IDisposable
    {
        // Removes this send's entry only, never a later send that reused the id.
        public void Dispose() => running.TryRemove(new KeyValuePair<Guid, Entry>(id, entry));
    }
}
