using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LightDrop.Core;
using LightDrop.Core.Contracts;
using LightDrop.Core.Devices;
using LightDrop.Core.Discovery;
using LightDrop.Core.Pairing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LightDrop.Daemon.Pairing;

internal enum PairingStartOutcome
{
    Started,
    NotFound,
    Ambiguous,
    AlreadyPaired,
    Busy,
}

internal sealed record PairingStartResult(PairingStartOutcome Outcome, PairingStatus? Status, string? Error);

/// <summary>How a pairing request from the other device, after hello, is answered.</summary>
internal enum PeerRequestOutcome
{
    /// <summary>Answer with this side's part.</summary>
    Answered,

    /// <summary>The caller is not the key that joined.</summary>
    Forbidden,

    /// <summary>There is no session this request could belong to.</summary>
    Conflict,
}

internal readonly record struct PeerRevealResult(PeerRequestOutcome Outcome, byte[]? Nonce = null);

internal readonly record struct PeerConfirmResult(PeerRequestOutcome Outcome, bool Accepted = false);

/// <summary>
/// Runs pairing sessions: one at a time, over loopback for the local user and over the peer
/// listener for the other device.
/// </summary>
/// <remarks>
/// <see cref="PairingSession"/> holds the rules; this class does the waiting, the dialing and the
/// pinning around it. Concrete, with no interface: one implementation, and the tests drive it
/// through real daemons rather than a double.
/// <para>
/// <strong>The flow.</strong> Both users start a session naming the other device. The lower
/// device id listens on its peer listener; the higher one dials <c>POST /pair/hello</c> about once
/// a second until it is answered, capturing the listener's key from the TLS handshake while the
/// listener captures the dialer's. The hello answer carries the listener's commitment to a nonce;
/// the dialer reveals its own nonce with <c>POST /pair/reveal</c> and gets the listener's back,
/// which must open the commitment (<see cref="PairingCode"/> explains why the order matters). Both
/// derive the code and show it. When the dialer's user answers, it sends <c>POST /pair/confirm</c>;
/// the listener holds that request open until its own user answers, then replies with that answer.
/// Every request after hello goes to, and must come from, the <em>same</em> key that met at
/// hello. Each side pins only if both said yes.
/// </para>
/// </remarks>
internal sealed class PairingCoordinator(
    PairingService pairing,
    PeerRegistry registry,
    DeviceIdentityProvider identities,
    DeviceKeyProvider keys,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<PairingCoordinator> logger) : IDisposable
{
    /// <summary>How often the dialer tries again while the other side is not answering yet.</summary>
    private static readonly TimeSpan DialInterval = TimeSpan.FromSeconds(1);

    /// <summary>Per attempt: a hello or reveal that hangs must not eat the whole join window.</summary>
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Lets the listener's own expiry answer the confirm, rather than racing it.</summary>
    private static readonly TimeSpan ConfirmGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The largest response this device will read from a peer during pairing. Both answers are a
    /// few dozen bytes; the peer is a stranger until the humans say otherwise.
    /// </summary>
    private const int MaxResponseBytes = 4096;

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _pinGate = new(1, 1);
    private readonly TimeProvider _time = time;

    private ActiveSession? _active;
    private TaskCompletionSource _changed = NewSignal();

    /// <summary>
    /// The pairing half of the peer listener's TLS gate: whether <paramref name="presented"/> may
    /// complete a handshake on account of a pairing session. Pinned peers are admitted separately.
    /// </summary>
    /// <remarks>
    /// Any certificate while the window is open -- the handshake is the only carrier of the new
    /// peer's key. After it closes, only the key that joined, and only until the session's
    /// deadline: if this side's user says no first, the session ends here at once, and the dialer
    /// still has to connect to <c>/pair/confirm</c> to learn that. Without this it would be
    /// refused at the handshake and keep retrying until its own deadline, never told why.
    /// </remarks>
    public bool AdmitsForPairing(X509Certificate2 presented)
    {
        var session = Current?.Session;
        if (session is null)
        {
            return false;
        }

        if (session.IsOpen)
        {
            return true;
        }

        return _time.GetUtcNow() < session.Snapshot().Deadline
            && PinnedCertificate.TryExportPublicKeyInfo(presented, out var presentedKey)
            && session.IsPeerKey(presentedKey);
    }

    private ActiveSession? Current
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    /// <summary>Opens a pairing window with a discovered device.</summary>
    public async Task<PairingStartResult> StartAsync(string? query, CancellationToken cancellationToken)
    {
        // Discovered peers only. The registry is where the dial address comes from and where the
        // name that will be pinned comes from -- sanitized once, at ingestion -- so a device that
        // is not in it has neither.
        var lookup = PeerLookup.Find(
            query, registry.GetPeers(), peer => peer.DeviceId, peer => peer.DeviceName);

        switch (lookup.Outcome)
        {
            case PeerLookupOutcome.NotFound:
                return Refused(
                    PairingStartOutcome.NotFound,
                    $"No nearby device matches '{query}'. Run 'lightdrop peers' to see which devices are discovered.");

            case PeerLookupOutcome.Ambiguous:
                return Refused(
                    PairingStartOutcome.Ambiguous,
                    $"'{query}' matches more than one device: {Describe(lookup.Candidates)}. Use more of the device id.");
        }

        var peer = lookup.Match!;

        var trusted = await pairing.ListAsync(cancellationToken).ConfigureAwait(false);
        if (trusted.Any(pinned => string.Equals(pinned.DeviceId, peer.DeviceId, StringComparison.Ordinal)))
        {
            // Re-pairing would replace a pinned key, which is a downgrade path if done silently.
            return Refused(
                PairingStartOutcome.AlreadyPaired,
                $"'{peer.DeviceName}' is already paired with this device. Run 'lightdrop unpair {peer.DeviceId}' " +
                "first if you mean to pair it again.");
        }

        var identity = await identities.GetAsync(cancellationToken).ConfigureAwait(false);
        var local = await keys.GetAsync(cancellationToken).ConfigureAwait(false);

        ActiveSession active;
        ActiveSession? replaced;

        lock (_gate)
        {
            if (_active?.Session.IsOpen == true)
            {
                return Refused(
                    PairingStartOutcome.Busy,
                    $"Already pairing with '{_active.Session.PeerName}'. Finish or cancel that first.");
            }

            var session = new PairingSession(
                identity.Id, local.PublicKeyInfo, peer.DeviceId, peer.DeviceName, _time);

            replaced = _active;
            active = new ActiveSession(session);
            _active = active;
        }

        if (replaced is not null)
        {
            ReportEnd(replaced);
            await replaced.Cancellation.CancelAsync().ConfigureAwait(false);
        }

        PairingLog.Started(logger, peer.DeviceName, peer.DeviceId, active.Session.Role);
        Pulse();

        if (active.Session.Role == PairingRole.Dialer)
        {
            // Not awaited: the dialer runs for as long as the window does, and the CLI follows it
            // by polling. Every exit path of DialAsync ends the session or leaves it to expire.
            _ = Task.Run(() => DialAsync(active, peer, local, identity.Id), CancellationToken.None);
        }

        return new PairingStartResult(PairingStartOutcome.Started, active.Session.Snapshot(), null);
    }

    /// <summary>The current or most recent session, or null if there has never been one.</summary>
    public PairingStatus? GetStatus()
    {
        var active = Current;
        if (active is null)
        {
            return null;
        }

        // Expiry is evaluated on read, so a poll is also when an expired session gets logged.
        ReportEnd(active);
        return active.Session.Snapshot();
    }

    /// <summary>Records the local user's answer. <c>Applied</c> is false if there was nothing to answer.</summary>
    public (PairingStatus? Status, bool Applied) Decide(bool accepted)
    {
        var active = Current;
        if (active is null)
        {
            return (null, false);
        }

        var applied = active.Session.Decide(accepted);
        if (applied && accepted)
        {
            PairingLog.AcceptedHere(logger, active.Session.PeerName);
        }

        ReportEnd(active);
        Pulse();
        return (active.Session.Snapshot(), applied);
    }

    /// <summary>Cancels the open session, if there is one. Returns whether one was open.</summary>
    public bool Cancel() => EndCurrent(session => session.Cancel());

    /// <summary>Ends the open session as failed, for a failure outside the session's own rules.</summary>
    public bool Fail(string reason) => EndCurrent(session => session.Fail(reason));

    /// <summary>Forgets a paired peer. Returns whether it was paired.</summary>
    public async Task<bool> UnpairAsync(string deviceId, CancellationToken cancellationToken)
    {
        var trusted = await pairing.ListAsync(cancellationToken).ConfigureAwait(false);
        var peer = trusted.FirstOrDefault(
            pinned => string.Equals(pinned.DeviceId, deviceId, StringComparison.Ordinal));

        if (peer is null || !await pairing.UnpinAsync(deviceId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        PairingLog.Unpinned(logger, peer.DeviceName, peer.DeviceId);
        return true;
    }

    /// <summary>
    /// <c>POST /pair/hello</c> on the listening side: the dialer introduces itself, and its key
    /// joins the session.
    /// </summary>
    /// <returns>Null when there is no session this caller may join (the endpoint answers 409).</returns>
    public async Task<PairHelloResponse?> HelloAsync(
        PairHelloRequest request,
        X509Certificate2? clientCertificate,
        CancellationToken cancellationToken)
    {
        var active = Current;
        if (active is null || active.Session.Role != PairingRole.Listener)
        {
            return null;
        }

        var session = active.Session;

        if (request.ProtocolVersion != LightDropVersion.Protocol)
        {
            return null;
        }

        // A claimed id, so it proves nothing -- the code comparison is what catches an impostor.
        // It still matters: it keeps a session opened for one device from being joined by
        // whichever other device dials first, which would otherwise turn every open window into a
        // race the humans then have to notice.
        if (!string.Equals(request.DeviceId, session.PeerDeviceId, StringComparison.Ordinal))
        {
            return null;
        }

        var identity = await identities.GetAsync(cancellationToken).ConfigureAwait(false);

        if (!PinnedCertificate.TryExportPublicKeyInfo(clientCertificate, out var peerKey)
            || session.JoinAsListener(peerKey) is not { } commitment)
        {
            return null;
        }

        var snapshot = session.Snapshot();
        PairingLog.Joined(logger, snapshot.PeerName, snapshot.PeerDeviceId);
        return new PairHelloResponse { DeviceId = identity.Id, Commitment = commitment };
    }

    /// <summary>
    /// <c>POST /pair/reveal</c> on the listening side: the dialer reveals its nonce and gets this
    /// side's, which opens the commitment sent with hello. The code exists from here on.
    /// </summary>
    public PeerRevealResult RevealFromPeer(PairingNonce request, X509Certificate2? clientCertificate)
    {
        var active = Current;
        if (active is null || active.Session.Role != PairingRole.Listener)
        {
            return new PeerRevealResult(PeerRequestOutcome.Conflict);
        }

        var session = active.Session;

        // The same rule as confirm, for the same reason: only the key that joined at hello may
        // take part. A stranger revealing first would fix the dialer's half of the code.
        if (!PinnedCertificate.TryExportPublicKeyInfo(clientCertificate, out var presented)
            || !session.IsPeerKey(presented))
        {
            return new PeerRevealResult(PeerRequestOutcome.Forbidden);
        }

        if (session.RevealAsListener(request.Nonce) is not { } nonce)
        {
            return new PeerRevealResult(PeerRequestOutcome.Conflict);
        }

        OnCodeShown(active);
        return new PeerRevealResult(PeerRequestOutcome.Answered, nonce);
    }

    /// <summary>
    /// <c>POST /pair/confirm</c> on the listening side: records the dialer's answer, waits for the
    /// local user's, pins if both said yes, and answers with the local decision.
    /// </summary>
    public async Task<PeerConfirmResult> ConfirmFromPeerAsync(
        PairingDecision request,
        X509Certificate2? clientCertificate,
        CancellationToken requestAborted)
    {
        var active = Current;
        if (active is null || active.Session.Role != PairingRole.Listener)
        {
            return new PeerConfirmResult(PeerRequestOutcome.Conflict);
        }

        var session = active.Session;

        // The window's TLS gate admitted any certificate, and this request arrived on a new
        // connection. Only the key that joined at hello -- the key the code on screen was derived
        // from -- may answer for the peer. Anyone else could otherwise say "yes" on its behalf.
        if (!PinnedCertificate.TryExportPublicKeyInfo(clientCertificate, out var presented)
            || !session.IsPeerKey(presented))
        {
            return new PeerConfirmResult(PeerRequestOutcome.Forbidden);
        }

        session.RecordPeerDecision(request.Accepted);
        ReportEnd(active);
        Pulse();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            requestAborted, active.Cancellation.Token, lifetime.ApplicationStopping);

        try
        {
            // The long poll. Bounded by the session's own deadline, not by any HTTP timeout.
            await WaitUntilAsync(active, () => session.LocalDecision is not null || !session.IsOpen, stop.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The dialer stopped waiting -- Ctrl+C, a restart, a dropped link -- or this side is
            // shutting down. Its "yes" counted only while it could hear the answer; left behind,
            // this side's user saying yes later would reach both-accepted with nothing left to pin
            // or to tell. Atomic with the local decision: if both already accepted, it stays, and
            // the pin goes ahead below even though nobody will read this response.
            if (session.WithdrawPeerDecision())
            {
                return new PeerConfirmResult(PeerRequestOutcome.Conflict);
            }
        }

        // Before the both-accepted check, which is false once pinned: a dialer whose first confirm
        // was pinned here but never answered -- a dropped connection -- retries, and must hear yes
        // rather than conclude the other side gave up.
        if (session.State == PairingState.Paired)
        {
            return new PeerConfirmResult(PeerRequestOutcome.Answered, Accepted: true);
        }

        if (session.BothAccepted)
        {
            return new PeerConfirmResult(PeerRequestOutcome.Answered, await PinAsync(active).ConfigureAwait(false));
        }

        return session.State == PairingState.Rejected
            ? new PeerConfirmResult(PeerRequestOutcome.Answered, Accepted: false)
            : new PeerConfirmResult(PeerRequestOutcome.Conflict);
    }

    public void Dispose()
    {
        Current?.Cancellation.Cancel();
        _pinGate.Dispose();
    }

    /// <summary>The dialing side of a session, from first hello to pin.</summary>
    private async Task DialAsync(ActiveSession active, DiscoveredPeer peer, DeviceKeyPair local, string localId)
    {
        var session = active.Session;
        var address = new Uri($"https://{peer.Address}:{peer.Port}/");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            active.Cancellation.Token, lifetime.ApplicationStopping);

        try
        {
            if (await DialHelloAsync(session, address, local, localId, stop.Token).ConfigureAwait(false)
                is not { } joined)
            {
                return;
            }

            var (listenerKey, dialerNonce) = joined;
            PairingLog.Joined(logger, session.PeerName, session.PeerDeviceId);

            // Pinned to the key hello met, through the existing mutation-tested pinned handler --
            // not the capturing one. Everything from here on carries this side's half of the code
            // and then the "yes" that leads to a pin, so it must reach the device whose key the
            // code is derived from and nobody else.
            using var handler = PairingTls.CreateHandler(local, listenerKey);

            // Infinite, deliberately: the listener holds the confirm open until its user answers.
            // HttpClient's 100-second default happens to exceed today's 60-second window, but a
            // timeout that is right only because of how two unrelated constants compare is a bug
            // waiting for one of them to change. Each request below is bounded explicitly.
            using var client = CreateClient(handler, address, Timeout.InfiniteTimeSpan);

            if (!await RevealAsync(session, client, address, dialerNonce, stop.Token).ConfigureAwait(false))
            {
                return;
            }

            OnCodeShown(active);

            await WaitUntilAsync(active, () => session.LocalDecision is not null || !session.IsOpen, stop.Token)
                .ConfigureAwait(false);

            if (session.LocalDecision is not { } decision)
            {
                // Expired or cancelled before this device's user answered.
                return;
            }

            // Sent for a "no" too: the listener learns now rather than at its deadline.
            if (await SendConfirmAsync(session, client, address, decision, stop.Token)
                    .ConfigureAwait(false) is { } peerAccepted)
            {
                session.RecordPeerDecision(peerAccepted);
            }

            if (session.BothAccepted)
            {
                await PinAsync(active).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Cancelled by the user or by shutdown; the session already says which.
        }
        catch (Exception ex)
        {
            // A background task with nobody awaiting it: an escaping exception would vanish and
            // leave the session hanging until its deadline with no explanation.
            session.Fail($"Pairing stopped unexpectedly: {ex.Message}");
        }
        finally
        {
            ReportEnd(active);
            Pulse();
        }
    }

    /// <summary>
    /// Dials <c>hello</c> until the listener answers as the expected device, returning the key it
    /// presented and this side's nonce to reveal, or null if the window closed first.
    /// </summary>
    private async Task<(byte[] ListenerKey, byte[] DialerNonce)?> DialHelloAsync(
        PairingSession session,
        Uri address,
        DeviceKeyPair local,
        string localId,
        CancellationToken cancellationToken)
    {
        var hello = new PairHelloRequest { DeviceId = localId, ProtocolVersion = LightDropVersion.Protocol };

        while (session.State == PairingState.WaitingForPeer)
        {
            // A fresh capture per attempt: a failed attempt may have met some other key (a
            // listener that is up for its paired devices refuses us at the handshake, after
            // presenting its certificate), and that must not constrain the next try.
            var capture = new PeerKeyCapture();

            using (var handler = PairingTls.CreateCapturingHandler(local, capture))
            using (var client = CreateClient(handler, address, HelloTimeout))
            {
                try
                {
                    using var response = await client
                        .PostAsJsonAsync("pair/hello", hello, LightDropJsonContext.Default.PairHelloRequest, cancellationToken)
                        .ConfigureAwait(false);

                    // Anything else -- typically 409 -- means the other side has no session for this
                    // device yet. Its user may simply not have run `pair` yet.
                    if (response.IsSuccessStatusCode)
                    {
                        var answer = await response.Content
                            .ReadFromJsonAsync(LightDropJsonContext.Default.PairHelloResponse, cancellationToken)
                            .ConfigureAwait(false);

                        // The address came from an mDNS record anyone can send. Whoever answers
                        // there must at least claim to be the device the user named; the code
                        // comparison is what proves it.
                        if (!string.Equals(answer?.DeviceId, session.PeerDeviceId, StringComparison.Ordinal))
                        {
                            session.Fail(
                                $"The device at {address.Authority} answered as someone other than " +
                                $"'{session.PeerName}'. Nothing was saved.");
                            return null;
                        }

                        if (capture.Key is { } listenerKey
                            && answer?.Commitment is { } commitment
                            && session.JoinAsDialer(listenerKey, commitment) is { } dialerNonce)
                        {
                            return (listenerKey, dialerNonce);
                        }

                        session.Fail("The other device's key could not be used for pairing. Nothing was saved.");
                        return null;
                    }
                }
                catch (HttpRequestException ex)
                {
                    // Connection refused, or a TLS rejection from a listener that is up for its
                    // paired devices but has no window open yet. Both mean "not yet".
                    PairingLog.DialRetry(logger, session.PeerName, address.Authority, ex);
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    PairingLog.DialRetry(logger, session.PeerName, address.Authority, ex);
                }
                catch (JsonException)
                {
                    session.Fail("The other device sent an answer this build could not read. Nothing was saved.");
                    return null;
                }
            }

            await Task.Delay(DialInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Reveals this side's nonce and opens the listener's commitment with the nonce it answers
    /// with. Returns whether the code is now showing.
    /// </summary>
    private async Task<bool> RevealAsync(
        PairingSession session,
        HttpClient client,
        Uri address,
        byte[] dialerNonce,
        CancellationToken cancellationToken)
    {
        var body = new PairingNonce { Nonce = dialerNonce };

        // Retried on a transient failure with the same nonce, which the listener answers the same
        // way every time. Bounded by the join window, as hello is.
        while (session.State == PairingState.WaitingForPeer)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(HelloTimeout);

            try
            {
                using var response = await client
                    .PostAsJsonAsync("pair/reveal", body, LightDropJsonContext.Default.PairingNonce, attempt.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    session.Fail("The other device ended the pairing session before the code was shown. Nothing was saved.");
                    return false;
                }

                var answer = await response.Content
                    .ReadFromJsonAsync(LightDropJsonContext.Default.PairingNonce, attempt.Token)
                    .ConfigureAwait(false);

                // Fails the session itself when the nonce does not open the commitment.
                return answer?.Nonce is { } listenerNonce && session.OpenAsDialer(listenerNonce);
            }
            catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
            {
                FailOnKeyChange(session);
                return false;
            }
            catch (HttpRequestException ex)
            {
                PairingLog.DialRetry(logger, session.PeerName, address.Authority, ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                PairingLog.DialRetry(logger, session.PeerName, address.Authority, ex);
            }
            catch (JsonException)
            {
                session.Fail("The other device sent an answer this build could not read. Nothing was saved.");
                return false;
            }

            await Task.Delay(DialInterval, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Sends this device's answer to the listener and returns the listener's, or null if none
    /// arrived before the window closed.
    /// </summary>
    private async Task<bool?> SendConfirmAsync(
        PairingSession session,
        HttpClient client,
        Uri address,
        bool decision,
        CancellationToken cancellationToken)
    {
        var deadline = session.Snapshot().Deadline + ConfirmGrace;
        var body = new PairingDecision { Accepted = decision };

        while (true)
        {
            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(remaining);

            try
            {
                using var response = await client
                    .PostAsJsonAsync("pair/confirm", body, LightDropJsonContext.Default.PairingDecision, attempt.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    session.Fail("The other device ended the pairing session before both sides confirmed. Nothing was saved.");
                    return null;
                }

                var answer = await response.Content
                    .ReadFromJsonAsync(LightDropJsonContext.Default.PairingDecision, attempt.Token)
                    .ConfigureAwait(false);

                return answer?.Accepted == true;
            }
            catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
            {
                FailOnKeyChange(session);
                return null;
            }
            catch (HttpRequestException ex)
            {
                PairingLog.DialRetry(logger, session.PeerName, address.Authority, ex);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (JsonException)
            {
                session.Fail("The other device sent an answer this build could not read. Nothing was saved.");
                return null;
            }

            await Task.Delay(DialInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Something answered at the peer's address holding a different key than the one that
    /// answered hello seconds ago. That is what a man-in-the-middle looks like; retrying would
    /// only give it another go.
    /// </summary>
    private static void FailOnKeyChange(PairingSession session) =>
        session.Fail(
            "The other device answered with a different key than it used a moment ago. " +
            "Pairing stopped; nothing was saved.");

    /// <summary>Pins the peer if both sides accepted. The one place a session leads to a pin.</summary>
    private async Task<bool> PinAsync(ActiveSession active)
    {
        var session = active.Session;

        // Serialised: a dialer that retries confirm can reach the listener's handler twice.
        await _pinGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (session.State == PairingState.Paired)
            {
                return true;
            }

            if (!session.BothAccepted || session.PeerPublicKey is not { } peerKey)
            {
                return false;
            }

            try
            {
                // The name comes from the session, which took it from the discovery registry --
                // sanitized once at ingestion. Nothing the peer said during pairing is persisted.
                // Not cancellable: both humans already agreed, and a write abandoned halfway is a
                // worse outcome than one that finishes a moment late.
                await pairing.PinAsync(session.PeerDeviceId, session.PeerName, peerKey, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // InvalidOperationException: already paired -- another path pinned it since this
                // session started -- with a message written for the user. Anything else is the
                // state write failing. Either way the session must end here: left open, a
                // both-accepted session would hold the window -- and the TLS gate -- open until
                // its grace ran out, telling the user nothing.
                session.Fail(ex is InvalidOperationException
                    ? ex.Message
                    : $"Both devices accepted, but saving the pairing failed: {ex.Message}");
                return false;
            }

            // After a successful write this cannot throw: it checks the two answers, not the
            // clock, and nothing can take back a "yes" once both were given.
            session.MarkPinned();
            return true;
        }
        finally
        {
            _pinGate.Release();
            ReportEnd(active);
            Pulse();
        }
    }

    private void OnCodeShown(ActiveSession active)
    {
        var snapshot = active.Session.Snapshot();
        PairingLog.CodeShown(logger, snapshot.PeerName, snapshot.Code ?? "");
        Pulse();
    }

    private bool EndCurrent(Func<PairingSession, bool> end)
    {
        var active = Current;
        if (active is null)
        {
            return false;
        }

        // Only when it actually ended: a cancel refused because both sides already accepted must
        // not abort the pin that is on its way.
        var ended = end(active.Session);
        if (ended)
        {
            active.Cancellation.Cancel();
        }

        ReportEnd(active);
        Pulse();
        return ended;
    }

    /// <summary>
    /// Waits until <paramref name="done"/> holds, waking on every session change and at the
    /// session's deadline.
    /// </summary>
    private async Task WaitUntilAsync(ActiveSession active, Func<bool> done, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Taken before checking, so a change between the check and the wait is not missed.
            Task changed;
            lock (_gate)
            {
                changed = _changed.Task;
            }

            if (done())
            {
                return;
            }

            // Nothing pulses when a deadline passes -- expiry is evaluated on read -- so the wait
            // is bounded by the deadline itself, after which done() reads the expired state.
            var remaining = active.Session.Snapshot().Deadline - _time.GetUtcNow();
            var wait = remaining > TimeSpan.FromMilliseconds(10) ? remaining : TimeSpan.FromMilliseconds(10);

            await Task.WhenAny(changed, Task.Delay(wait, cancellationToken)).ConfigureAwait(false);
        }
    }

    private void Pulse()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            previous = _changed;
            _changed = NewSignal();
        }

        previous.TrySetResult();
    }

    /// <summary>Logs how a session ended, once.</summary>
    private void ReportEnd(ActiveSession active)
    {
        var snapshot = active.Session.Snapshot();
        if (snapshot.State is PairingState.WaitingForPeer or PairingState.Confirming || !active.TryClaimEndReport())
        {
            return;
        }

        switch (snapshot.State)
        {
            case PairingState.Paired:
                PairingLog.Pinned(logger, snapshot.PeerName, snapshot.PeerDeviceId);
                break;
            case PairingState.Rejected:
                PairingLog.Rejected(logger, snapshot.PeerName, snapshot.Reason);
                break;
            case PairingState.Expired:
                PairingLog.Expired(logger, snapshot.PeerName, snapshot.Reason);
                break;
            case PairingState.Cancelled:
                PairingLog.Cancelled(logger, snapshot.PeerName);
                break;
            default:
                PairingLog.Failed(logger, snapshot.PeerName, snapshot.Reason);
                break;
        }
    }

    private static HttpClient CreateClient(HttpMessageHandler handler, Uri address, TimeSpan timeout) =>
        new(handler, disposeHandler: false)
        {
            BaseAddress = address,
            Timeout = timeout,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };

    private static PairingStartResult Refused(PairingStartOutcome outcome, string error) =>
        new(outcome, null, error);

    private static string Describe(IEnumerable<DiscoveredPeer> peers) =>
        string.Join(", ", peers.Select(peer =>
            $"{peer.DeviceName} ({(peer.DeviceId.Length > 8 ? peer.DeviceId[..8] : peer.DeviceId)})"));

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ActiveSession(PairingSession session)
    {
        private int _endReported;

        public PairingSession Session { get; } = session;

        /// <summary>
        /// Cancelled when the session is cancelled or replaced. Never disposed: it has no timer
        /// and no parent registration, and disposing it would race the tasks still observing it.
        /// </summary>
        public CancellationTokenSource Cancellation { get; } = new();

        public bool TryClaimEndReport() => Interlocked.Exchange(ref _endReported, 1) == 0;
    }
}
