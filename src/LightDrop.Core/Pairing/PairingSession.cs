using System.Security.Cryptography;
using System.Text.Json.Serialization;
using LightDrop.Core.Contracts;

namespace LightDrop.Core.Pairing;

/// <summary>Which side of a pairing connection this device plays.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PairingRole>))]
public enum PairingRole
{
    /// <summary>Waits on the peer listener for the other device to say hello.</summary>
    Listener,

    /// <summary>Connects to the other device's peer listener, retrying until it answers.</summary>
    Dialer,
}

/// <summary>Where a pairing session stands.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PairingState>))]
public enum PairingState
{
    /// <summary>Open; the other device has not connected yet.</summary>
    WaitingForPeer,

    /// <summary>Open; both sides hold the code and the humans are comparing it.</summary>
    Confirming,

    /// <summary>Both said yes and the peer's key is pinned.</summary>
    Paired,

    /// <summary>Someone said no. Nothing was persisted.</summary>
    Rejected,

    /// <summary>A deadline passed. Nothing was persisted.</summary>
    Expired,

    /// <summary>The local user gave up. Nothing was persisted.</summary>
    Cancelled,

    /// <summary>Something went wrong that is neither a "no" nor a timeout.</summary>
    Failed,
}

/// <summary>
/// The rules of one pairing window: roles, deadlines, and the both-said-yes condition for pinning.
/// </summary>
/// <remarks>
/// Pure logic, so every rule here is tested with a fake clock. The daemon's coordinator does the
/// waiting, the dialing and the pinning around it, and asks this class what is allowed.
/// <para>
/// <strong>What this protects.</strong> During the window the peer listener accepts any TLS
/// certificate, because the handshake is the only way the peer's key can arrive. The session is
/// what turns that into something safe: exactly one key joins, the code is derived from that key,
/// and nothing is pinned unless both humans looked at that code and said yes before the deadline.
/// </para>
/// <para>
/// Expiry is evaluated on read, like <see cref="Discovery.PeerRegistry"/>: there is no timer to
/// schedule or stop, and a session nobody asks about has no reason to change.
/// </para>
/// </remarks>
public sealed class PairingSession
{
    /// <summary>How long the other device has to connect.</summary>
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long the humans have to compare codes, counted from when the code appears.</summary>
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _localPublicKey;

    private PairingState _state = PairingState.WaitingForPeer;
    private DateTimeOffset _deadline;
    private byte[]? _peerPublicKey;
    private string? _code;
    private string? _reason;
    private bool? _localDecision;
    private bool? _peerDecision;

    public PairingSession(
        string localDeviceId,
        ReadOnlySpan<byte> localPublicKey,
        string peerDeviceId,
        string peerName,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerName);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (localPublicKey.IsEmpty)
        {
            throw new ArgumentException("A pairing session needs this device's public key.", nameof(localPublicKey));
        }

        Role = RoleFor(localDeviceId, peerDeviceId);
        PeerDeviceId = peerDeviceId;
        PeerName = peerName;

        _timeProvider = timeProvider;
        _localPublicKey = localPublicKey.ToArray();
        _deadline = timeProvider.GetUtcNow() + JoinTimeout;
    }

    public string PeerDeviceId { get; }

    public string PeerName { get; }

    public PairingRole Role { get; }

    /// <summary>
    /// Lower id listens, higher id dials, compared ordinally.
    /// </summary>
    /// <remarks>
    /// Both machines reach the same answer from the two ids alone, with no negotiation, which
    /// removes the race where each connects to the other at once. Ordinal because a
    /// culture-aware compare can order the same two strings differently on two machines.
    /// </remarks>
    public static PairingRole RoleFor(string localDeviceId, string peerDeviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerDeviceId);

        var order = string.CompareOrdinal(localDeviceId, peerDeviceId);
        if (order == 0)
        {
            throw new ArgumentException("A device cannot pair with itself.", nameof(peerDeviceId));
        }

        return order < 0 ? PairingRole.Listener : PairingRole.Dialer;
    }

    public PairingState State
    {
        get
        {
            lock (_gate)
            {
                Refresh();
                return _state;
            }
        }
    }

    /// <summary>Whether the window is still open: a peer may join, or the humans may decide.</summary>
    public bool IsOpen => State is PairingState.WaitingForPeer or PairingState.Confirming;

    /// <summary>
    /// Both humans said yes, in time, about the key that joined. The only state in which a caller
    /// may pin.
    /// </summary>
    public bool BothAccepted
    {
        get
        {
            lock (_gate)
            {
                Refresh();
                return IsBothAccepted();
            }
        }
    }

    /// <summary>This device's user's answer, or null while they have not given one.</summary>
    public bool? LocalDecision
    {
        get
        {
            lock (_gate)
            {
                return _localDecision;
            }
        }
    }

    /// <summary>The key that joined, or null before anyone has. A copy.</summary>
    public byte[]? PeerPublicKey
    {
        get
        {
            lock (_gate)
            {
                return (byte[]?)_peerPublicKey?.Clone();
            }
        }
    }

    /// <summary>
    /// Accepts the first peer key into the session and derives the code from it.
    /// </summary>
    /// <returns>
    /// <c>false</c> once a key has joined, after the window closed, or for this device's own key.
    /// The first key in wins and is never replaced: it is the key the humans will be shown digits
    /// for, so letting a second connection swap it would detach the code from what gets pinned.
    /// </returns>
    public bool TryJoin(ReadOnlySpan<byte> peerPublicKey)
    {
        if (peerPublicKey.IsEmpty)
        {
            return false;
        }

        lock (_gate)
        {
            Refresh();

            if (_state != PairingState.WaitingForPeer)
            {
                return false;
            }

            // Our own key coming back is a reflection, not a peer. PairingCode.Derive refuses it
            // too; checking here keeps the session open rather than throwing out of it.
            if (peerPublicKey.SequenceEqual(_localPublicKey))
            {
                return false;
            }

            _peerPublicKey = peerPublicKey.ToArray();
            _code = PairingCode.Derive(_localPublicKey, _peerPublicKey);
            _state = PairingState.Confirming;
            _deadline = _timeProvider.GetUtcNow() + ConfirmTimeout;
            return true;
        }
    }

    /// <summary>
    /// Whether <paramref name="presented"/> is the key that joined. Constant-time; false before
    /// anyone joined.
    /// </summary>
    /// <remarks>
    /// Checked on every request after hello. While the window is open the TLS layer accepts any
    /// certificate, so this is what stops a second connection from answering on the first
    /// one's behalf.
    /// </remarks>
    public bool IsPeerKey(ReadOnlySpan<byte> presented)
    {
        lock (_gate)
        {
            return _peerPublicKey is not null
                && CryptographicOperations.FixedTimeEquals(_peerPublicKey, presented);
        }
    }

    /// <summary>Records this device's user's answer. Once only, and only while confirming.</summary>
    public bool Decide(bool accepted)
    {
        lock (_gate)
        {
            Refresh();

            if (_state != PairingState.Confirming || _localDecision is not null)
            {
                return false;
            }

            _localDecision = accepted;
            if (!accepted)
            {
                End(PairingState.Rejected, "The codes were declined on this device.");
            }

            return true;
        }
    }

    /// <summary>
    /// Records the other device's answer. Repeating the same answer is accepted; changing it
    /// is not.
    /// </summary>
    public bool RecordPeerDecision(bool accepted)
    {
        lock (_gate)
        {
            Refresh();

            if (_peerDecision is { } recorded)
            {
                return recorded == accepted;
            }

            if (_state != PairingState.Confirming)
            {
                return false;
            }

            _peerDecision = accepted;
            if (!accepted)
            {
                End(PairingState.Rejected, "The codes were declined on the other device.");
            }

            return true;
        }
    }

    /// <summary>Records that the peer's key was pinned.</summary>
    /// <exception cref="InvalidOperationException">Not both sides accepted.</exception>
    public void MarkPinned()
    {
        lock (_gate)
        {
            Refresh();

            if (!IsBothAccepted())
            {
                throw new InvalidOperationException("A peer is pinned only after both sides accepted.");
            }

            _state = PairingState.Paired;
        }
    }

    /// <summary>Ends an open session as failed. Returns whether it was open.</summary>
    public bool Fail(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_gate)
        {
            Refresh();
            return EndIfOpen(PairingState.Failed, reason);
        }
    }

    /// <summary>Ends an open session at the local user's request. Returns whether it was open.</summary>
    public bool Cancel()
    {
        lock (_gate)
        {
            Refresh();
            return EndIfOpen(PairingState.Cancelled, "Pairing was cancelled.");
        }
    }

    /// <summary>Everything the CLI shows, read under one lock so the fields agree.</summary>
    public PairingStatus Snapshot()
    {
        lock (_gate)
        {
            Refresh();

            return new PairingStatus
            {
                State = _state,
                Role = Role,
                PeerDeviceId = PeerDeviceId,
                PeerName = PeerName,
                Code = _code,
                Reason = _reason,
                Deadline = _deadline,
            };
        }
    }

    private bool IsBothAccepted() =>
        _state == PairingState.Confirming && _localDecision == true && _peerDecision == true;

    private void Refresh()
    {
        // Both-accepted is exempt: both humans said yes inside the window, and pinning is a file
        // write that may land a moment after the deadline. Expiring then would discard a pairing
        // both people agreed to, over I/O latency.
        if (_state is not (PairingState.WaitingForPeer or PairingState.Confirming) || IsBothAccepted())
        {
            return;
        }

        if (_timeProvider.GetUtcNow() < _deadline)
        {
            return;
        }

        End(
            PairingState.Expired,
            _state == PairingState.WaitingForPeer
                ? $"The other device did not connect within {JoinTimeout.TotalSeconds:0} seconds."
                : $"The codes were not confirmed on both devices within {ConfirmTimeout.TotalSeconds:0} seconds.");
    }

    private bool EndIfOpen(PairingState state, string reason)
    {
        if (_state is not (PairingState.WaitingForPeer or PairingState.Confirming))
        {
            return false;
        }

        End(state, reason);
        return true;
    }

    private void End(PairingState state, string reason)
    {
        _state = state;
        _reason = reason;
    }
}
