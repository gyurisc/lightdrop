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
/// <strong>The nonce exchange.</strong> Between the key joining and the code appearing, the two
/// sides swap nonces in the order <see cref="PairingCode"/> explains: the listener commits
/// (<see cref="JoinAsListener"/>), the dialer reveals (<see cref="RevealAsListener"/> on the
/// listener), the listener opens its commitment (<see cref="OpenAsDialer"/> on the dialer). Each
/// side takes the other's contribution once and never replaces it -- a second, different nonce
/// accepted after the first would hand a man-in-the-middle the offline search the commitment
/// exists to deny it. The session stays <see cref="PairingState.WaitingForPeer"/> until the code
/// exists.
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

    /// <summary>
    /// How long past the deadline a session both humans accepted waits for its pin to be written.
    /// </summary>
    /// <remarks>
    /// Bounded, because an open session is an open TLS gate: if nothing ever pins -- a bug, a write
    /// that hangs -- the window must still close.
    /// </remarks>
    public static readonly TimeSpan PinGrace = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _localPublicKey;
    private readonly byte[] _localNonce = PairingCode.CreateNonce();

    private PairingState _state = PairingState.WaitingForPeer;
    private DateTimeOffset _deadline;
    private byte[]? _peerPublicKey;
    private byte[]? _commitment;
    private byte[]? _peerNonce;
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
    /// Listener: the dialer's key joins, and this side commits to its nonce.
    /// </summary>
    /// <returns>
    /// The commitment to send back, or null once another key has joined, after the window closed,
    /// or for this device's own key. The first key in wins and is never replaced: it is the key the
    /// humans will be shown digits for. The same key saying hello again gets the same commitment,
    /// so a dialer whose first answer was lost in transit can recover.
    /// </returns>
    public byte[]? JoinAsListener(ReadOnlySpan<byte> dialerPublicKey)
    {
        RequireRole(PairingRole.Listener);

        if (dialerPublicKey.IsEmpty)
        {
            return null;
        }

        lock (_gate)
        {
            Refresh();

            if (_state != PairingState.WaitingForPeer)
            {
                return null;
            }

            if (_peerPublicKey is not null)
            {
                return CryptographicOperations.FixedTimeEquals(_peerPublicKey, dialerPublicKey)
                    ? (byte[])_commitment!.Clone()
                    : null;
            }

            // Our own key coming back is a reflection, not a peer. PairingCode refuses it too;
            // checking here keeps the session open rather than throwing out of it.
            if (dialerPublicKey.SequenceEqual(_localPublicKey))
            {
                return null;
            }

            _peerPublicKey = dialerPublicKey.ToArray();
            _commitment = PairingCode.Commit(_localPublicKey, _peerPublicKey, _localNonce);
            return (byte[])_commitment.Clone();
        }
    }

    /// <summary>
    /// Listener: the dialer reveals its nonce; this side answers with its own, and the code exists.
    /// </summary>
    /// <returns>
    /// This side's nonce, or null before a key joined, after the window closed, or for a nonce
    /// other than the first one revealed. Repeating the first nonce gets the same answer, for a
    /// dialer that retries.
    /// </returns>
    public byte[]? RevealAsListener(ReadOnlySpan<byte> dialerNonce)
    {
        RequireRole(PairingRole.Listener);

        if (dialerNonce.Length != PairingCode.NonceLength)
        {
            return null;
        }

        lock (_gate)
        {
            Refresh();

            if (_peerNonce is not null)
            {
                // Once only. The dialer learns this side's nonce from the answer, so accepting a
                // different nonce afterwards would let whoever sent it pick one that makes the
                // digits come out as it likes.
                return _state == PairingState.Confirming
                       && CryptographicOperations.FixedTimeEquals(_peerNonce, dialerNonce)
                    ? (byte[])_localNonce.Clone()
                    : null;
            }

            if (_state != PairingState.WaitingForPeer || _peerPublicKey is null)
            {
                return null;
            }

            _peerNonce = dialerNonce.ToArray();
            ShowCode(PairingCode.Derive(_peerPublicKey, _localPublicKey, _peerNonce, _localNonce));
            return (byte[])_localNonce.Clone();
        }
    }

    /// <summary>
    /// Dialer: the listener's key and commitment arrive with its answer to hello.
    /// </summary>
    /// <returns>
    /// This side's nonce to reveal, or null if a key already joined, the window closed, or the
    /// input is malformed. The nonce is handed out only here, after the commitment is held, so it
    /// cannot be revealed before the listener is bound to its own.
    /// </returns>
    public byte[]? JoinAsDialer(ReadOnlySpan<byte> listenerPublicKey, ReadOnlySpan<byte> commitment)
    {
        RequireRole(PairingRole.Dialer);

        if (listenerPublicKey.IsEmpty || commitment.Length != PairingCode.CommitmentLength)
        {
            return null;
        }

        lock (_gate)
        {
            Refresh();

            if (_state != PairingState.WaitingForPeer
                || _peerPublicKey is not null
                || listenerPublicKey.SequenceEqual(_localPublicKey))
            {
                return null;
            }

            _peerPublicKey = listenerPublicKey.ToArray();
            _commitment = commitment.ToArray();
            return (byte[])_localNonce.Clone();
        }
    }

    /// <summary>
    /// Dialer: the listener opens its commitment. If it opens, the code exists.
    /// </summary>
    /// <returns>
    /// Whether the code is now showing. A nonce that does not open the commitment ends the session
    /// as failed: the listener tried to change its contribution after seeing this side's, which is
    /// exactly what a man-in-the-middle searching for matching digits needs to do.
    /// </returns>
    public bool OpenAsDialer(ReadOnlySpan<byte> listenerNonce)
    {
        RequireRole(PairingRole.Dialer);

        lock (_gate)
        {
            Refresh();

            if (_state != PairingState.WaitingForPeer || _peerPublicKey is null || _peerNonce is not null)
            {
                return false;
            }

            if (!PairingCode.Opens(_commitment, _peerPublicKey, _localPublicKey, listenerNonce))
            {
                End(
                    PairingState.Failed,
                    "The other device's answer did not match what it had committed to a moment earlier. " +
                    "That is what a device in the middle looks like. Nothing was saved.");
                return false;
            }

            _peerNonce = listenerNonce.ToArray();
            ShowCode(PairingCode.Derive(_localPublicKey, _peerPublicKey, _localNonce, _peerNonce));
            return true;
        }
    }

    /// <summary>
    /// Whether <paramref name="presented"/> is the key that joined. Constant-time; false before
    /// anyone joined.
    /// </summary>
    /// <remarks>
    /// Checked on every request after hello -- the reveal as much as the confirm. While the window is open the TLS layer accepts any
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

    /// <summary>
    /// Forgets the other device's "yes" because it stopped waiting for the answer. Returns whether
    /// it was forgotten.
    /// </summary>
    /// <remarks>
    /// The listener learns the dialer's answer from a request it holds open, and pins from that
    /// request. If the dialer goes away first -- its user pressed Ctrl+C, its daemon restarted --
    /// a "yes" left recorded would let this side's later "yes" reach both-accepted with nobody
    /// left to pin or to tell. So the "yes" counts only while the dialer is still there to hear
    /// the outcome; it says it again if it comes back. Refused once both accepted: the outcome is
    /// decided then, and the pin is already on its way.
    /// </remarks>
    public bool WithdrawPeerDecision()
    {
        lock (_gate)
        {
            Refresh();

            if (_state != PairingState.Confirming || _peerDecision != true || IsBothAccepted())
            {
                return false;
            }

            _peerDecision = null;
            return true;
        }
    }

    /// <summary>Records that the peer's key was pinned.</summary>
    /// <remarks>
    /// Checks the two answers, not the state: by the time a write finishes the session may have
    /// run past <see cref="PinGrace"/>, and the pin is on disk either way. The state should say
    /// what is true, not throw over it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Not both sides accepted.</exception>
    public void MarkPinned()
    {
        lock (_gate)
        {
            if (_localDecision != true || _peerDecision != true)
            {
                throw new InvalidOperationException("A peer is pinned only after both sides accepted.");
            }

            _state = PairingState.Paired;
            _reason = null;
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

    /// <summary>Ends an open session at the local user's request. Returns whether it ended.</summary>
    /// <remarks>
    /// Refused once both sides accepted. Both humans have already agreed, the pin may be half
    /// written, and the other device may already have pinned this one; "cancelled, nothing was
    /// saved" would no longer be true.
    /// </remarks>
    public bool Cancel()
    {
        lock (_gate)
        {
            Refresh();
            return !IsBothAccepted() && EndIfOpen(PairingState.Cancelled, "Pairing was cancelled.");
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

    private void ShowCode(string code)
    {
        _code = code;
        _state = PairingState.Confirming;
        _deadline = _timeProvider.GetUtcNow() + ConfirmTimeout;
    }

    private void RequireRole(PairingRole role)
    {
        if (Role != role)
        {
            throw new InvalidOperationException($"Only the {role.ToString().ToLowerInvariant()} takes this step.");
        }
    }

    private void Refresh()
    {
        if (_state is not (PairingState.WaitingForPeer or PairingState.Confirming))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();

        // Both-accepted gets a grace: both humans said yes inside the window, and pinning is a
        // file write that may land a moment after the deadline. Expiring then would discard a
        // pairing both people agreed to, over I/O latency. Only a moment, though -- see PinGrace.
        if (IsBothAccepted())
        {
            if (now >= _deadline + PinGrace)
            {
                End(PairingState.Expired, "Both devices accepted, but saving the pairing did not finish in time.");
            }

            return;
        }

        if (now < _deadline)
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
