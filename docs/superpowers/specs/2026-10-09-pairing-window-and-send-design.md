# Pairing window and first file transfer — design

Status: **agreed 2026-10-09, being implemented.** Goal: make LightDrop usable end to end so it can
be tested on two real machines — pair once, then `lightdrop send <file> <peer>`.

Binding authority for the cryptography remains
`docs/superpowers/specs/2026-08-16-m2-secure-pairing-design.md` (compare-and-confirm, SAS
derivation, random GUID id + pinned SPKI, mutual TLS with self-signed certs). This document
settles what that spec left open and adds the smallest M3 slice that makes a transfer possible.

## Decision: one pinned-only LAN listener (reopens spec decision 2)

The 2026-08-16 spec kept the LAN reachable only during a pairing window and said M3 must reopen
that, because receiving a file needs a listener when no human is at the keyboard. Reopened and
decided by the user on 2026-10-09:

- A **peer listener** runs on `0.0.0.0:<peerPort>`, HTTPS, mutual TLS, `RequireCertificate`.
- It is started **only when needed**: at daemon start if `trustedPeers` is non-empty, or when a
  pairing window opens. A fresh install with zero peers still has **no LAN socket**, as today.
  It is not stopped on unpair (simplicity); after the last unpair every handshake is rejected
  anyway, and a restart closes it.
- **The TLS handshake is the gate.** A client certificate is accepted only if its SPKI is pinned in
  the *current* `trustedPeers` (re-read per connection, so unpair takes effect for the next
  connection without a restart). The single exception: while a pairing window is open, the
  handshake accepts any certificate — deferred validation — because the handshake is the only
  carrier of the peer's key.
- Because of that exception, **every non-pairing peer route also checks the pin at the application
  layer** (the connection's client certificate SPKI must match a trusted peer). Both checks must
  exist, and each must be shown load-bearing by mutation: replace it with `true` and exactly the
  corresponding test fails.
- The listener is a **separate `WebApplication`**, not a second binding on the loopback app. That
  is the fix the Roadmap's "Known traps" asks for: the loopback app's routes (`/health`,
  `/api/*`, the UI) are simply not in the peer app's route table, and the loopback origin check is
  not weakened. **Do not use `RequireHost`** to scope routes — it matches the attacker-controlled
  `Host` header, not the connection's port.

`peerPort` defaults to the loopback port + 1 (5534), overridable with `LIGHTDROP_PEER_PORT`. So two
daemons on one machine with different `LIGHTDROP_PORT` values do not collide. The **SRV record now
advertises the peer port** — it finally means "where this service is reachable", so DECISIONS #17
and the Protocol.md paragraph "The SRV port is not an authorization boundary" need updating
(it is still not an authorization boundary: the TLS pin is).

Windows will show a Defender Firewall prompt the first time the peer listener binds. Document it.

## Pairing ceremony

Both users run `lightdrop pair <peer>` within about a minute of each other. `<peer>` is matched
against **discovered** peers by device name (case-insensitive) or device-id prefix; ambiguous or
missing matches are errors that name the candidates.

**The CLI owns the terminal, the daemon owns the session** (settles the spec's open question). The
CLI starts a session over loopback, polls it, prints the code, asks `Do the codes match on both
screens? [y/N]`, and posts the answer. Ctrl+C cancels the session.

Roles are deterministic: compare device ids ordinally; **lower id listens, higher id dials**. The
dialer retries `hello` roughly once a second until it connects or its window expires — the other
side may not have started yet (connection refused, or a TLS rejection from a listener that is up for
other peers).

Peer routes (LAN, on the peer listener; small bodies, cap ~4 KB):

| Route | Purpose |
|---|---|
| `POST /pair/hello` `{deviceId, protocolVersion}` → `{deviceId, commitment}` | Accepted only if this side has an open session, is the listener, the caller's `deviceId` equals the session target, and no peer has joined yet. Captures the caller's client-cert SPKI. Otherwise 409. |
| `POST /pair/reveal` `{nonce}` → `{nonce}` | *(Added 2026-10-09, `DECISIONS.md` #27.)* Same-SPKI rule as confirm. The dialer reveals its nonce; the listener answers with the nonce its commitment opens to. The first nonce is kept. |
| `POST /pair/confirm` `{accepted}` → `{accepted}` | Must arrive on a connection presenting the **same SPKI** captured at hello (fixed-time compare). Long-polls until the local user decides or the deadline passes, then answers with the local decision. |

The dialer captures the listener's server-certificate SPKI during the hello handshake, and its
`confirm` request must go to that **same key** (e.g. reuse `PairingTls.CreateHandler` with the
captured key, or a handler that captures on first handshake and requires equality afterwards).
The dialer also checks the hello response's `deviceId` equals its target.

Both sides compute the code from both keys and both nonces (see `docs/Protocol.md`, *Pairing*) and
show it. **Each side pins only if
both users accepted.** Pin via `PairingService.PinAsync`, using the peer's **sanitized name from the
discovery registry** — never a name claimed in a pairing body (no new untrusted-text surface).

Timing: 60 s for the peer to join; once a code is shown, another 60 s to confirm. Expiry discards
the session with nothing persisted.

Failure table (from the spec, unchanged in substance): mismatch / "no" on either side → abort,
nothing persisted on either side, both log it; window expires → session discarded; peer not
discovered → error naming the peer; already paired → refuse, say to `unpair` first; second
concurrent session → busy.

The session's rules (states, transitions, both-accepted-to-pin, deadlines) are pure logic and
belong in **Core** as a small state machine tested with a fake `TimeProvider`. The daemon side
(timers, dialing, the listener, calling `PinAsync`) is a concrete coordinator in Daemon. No
interfaces for either.

## Loopback API (CLI-facing, behind the existing origin check)

| Route | Purpose |
|---|---|
| `POST /api/pairing` `{peer}` | Start a session. 404 unknown peer, 409 already paired / busy. |
| `GET /api/pairing` | Snapshot: state, peer name, code (when known), failure reason. |
| `POST /api/pairing/confirm` `{accepted}` | The local user's answer. |
| `DELETE /api/pairing` | Cancel. |
| `GET /api/trusted` | Paired peers (id, name, pairedAt) — never the key material. |
| `DELETE /api/trusted/{deviceId}` | Unpair (local only, as the spec says). |
| `POST /api/send` `{path, peer}` | Send a file; see below. |

Every new contract type goes on `LightDropJsonContext`.

## First file transfer (M3, minimal)

CLAUDE.md asks for the command scaffold to be rebuilt at this milestone, sized to a real handler.
Sized to exactly one:

- `POST /commands/{name}` on the peer listener. Metadata travels in a request header
  (`LightDrop-Metadata`, JSON — base64url if needed for header safety); **the body is the raw byte
  stream**. No base64-in-JSON, no buffering of the whole file.
- Handlers are registered in DI as a collection (the same justification as `ICliCommand`), looked up
  by name. **Capabilities are derived from the registered handler set** and flow into
  `HealthResponse.Capabilities` (and the TXT `cap` key if that is cheap). One handler: `SendFile`.
- The pinned-peer application-layer check sits in front of the whole `/commands` group, so a future
  handler cannot forget it.

`SendFile` on the receiver:

- Metadata `{fileName, length}`; `Content-Length` must equal `length`. Kestrel's 30 MB default
  `MaxRequestBodySize` must be lifted **for this route only**.
- The file name is attacker-controlled. A pure Core function sanitizes it: take the last segment
  after normalizing both `/` and `\`, strip what `UntrustedText` strips, replace `<>:"/\|?*` and
  controls, trim trailing dots and spaces, reject empty / `.` / `..`, prefix Windows reserved names
  (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`, with or without extension), cap the length while
  keeping the extension. Then the daemon additionally asserts the final full path's directory *is*
  the download folder.
- Download folder: `config.json` `downloadFolder` if set, else `~/Downloads`. Created if missing.
- Stream to a temporary file in the download folder, then move into place with collision handling
  (`name (1).ext`, `name (2).ext`, … — never overwrite). A short or aborted body deletes the temp
  file and nothing appears under the final name.
- Respond `{savedAs}` — the file name only, not the receiver's full path. Log one line naming file,
  size, and sending peer.

The sender:

- `lightdrop send <file> <peer>`: the CLI resolves `<file>` to an absolute path (the daemon's working
  directory differs) and calls `POST /api/send`. `<peer>` is matched against **trusted** peers; the
  address and port come from the discovery registry by device id. A trusted peer that is not
  currently discovered is an error saying so.
- The daemon streams the file with `PairingTls.CreateHandler(local, pinnedKey)` — the existing,
  mutation-tested pinned client.
- **Timeouts:** `HttpClient.Timeout` defaults to 100 s and will kill large transfers, both for the
  daemon→peer request and for the CLI→daemon `/api/send` call. Use an infinite timeout and rely on
  cancellation.
- CLI prints size, duration and throughput on success. Live progress is deferred.

`/api/send` lets any same-user local process send any file that user can read to an
*already-paired* peer. Browsers are kept out by the origin check (POST needs no Origin or the
loopback Origin). That is accepted for a solo tool; record it in DECISIONS.

## CLI surface added

```
lightdrop pair <peer>
lightdrop unpair <peer>          # matched against trusted peers
lightdrop peers --trusted
lightdrop send <file> <peer>
```

`peers` (untrusted) should mark which discovered peers are already paired.

## Tests (behaviour, not implementation)

- **Core:** session state machine (both accept → pin; either rejects → no pin; deadlines with a fake
  clock; role by id), file-name sanitization vectors including traversal, reserved names, controls,
  bidi, empty-after-sanitizing, long names, peer matching by name / id prefix / ambiguity.
- **Daemon:** two in-process daemons on loopback with separate `TempDataDirectory`s and
  `FakePeerDiscoveryTransport`s announcing each other at `127.0.0.1:<peerPort>` (free ports), driven
  through their loopback APIs:
  full pair → both pinned; one side says no → neither pinned; unpinned client rejected at the
  handshake when no window is open; during an open window an unpinned client gets through TLS but
  cannot reach `/commands/*`; the peer listener does not exist with zero trusted peers and no window;
  the peer listener serves no `/health` or `/api/*`; send A→B lands in B's download folder (set via
  `config.json` in the temp data dir — **tests must never touch the real `~/Downloads`**);
  collision naming; a hostile file name lands sanitized inside the folder; an aborted upload leaves
  no final file.
- **Manual:** two real machines — added to the Roadmap checklist, not claimed.
