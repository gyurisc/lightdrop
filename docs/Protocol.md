# LightDrop Protocol

Status: **v1 in progress.** Implemented: `/health`, discovery, pairing, and one command, `SendFile`. Everything under "Direction" is intent, not contract.

## Versioning

Two independent numbers:

- **Application version** (`0.1.0`) — changes every release. Read from the assembly; never hardcoded.
- **Protocol version** (`1`) — changes only when the wire format changes in a way peers must negotiate.

Additive capability changes must **not** bump the protocol version. That is what the capability list is for.

## Implemented: `GET /health`

Liveness, identity, and everything a peer needs before it can talk to this device.

```json
{
  "version": "0.1.0",
  "protocolVersion": 1,
  "deviceId": "ae471fdc0c004589b6ab810fcf6c9324",
  "deviceName": "Work Laptop",
  "platform": "windows",
  "capabilities": ["SendFile"]
}
```

| Field | Notes |
|---|---|
| `version` | Application version. Informational. |
| `protocolVersion` | Compatibility gate. |
| `deviceId` | Generated once, persisted, never changes without an explicit reset. Trust pins to this. |
| `deviceName` | Human-readable. How the CLI addresses peers. |
| `platform` | `windows` \| `macos` \| `linux` \| `unknown`. A short stable token, not `RuntimeInformation.OSDescription`. |
| `capabilities` | Commands this device accepts, projected from its registered handlers. Also the TXT `cap` key. |

Health stays a **plain HTTP GET**, not a command. It is the bootstrap probe: it must answer before any socket, pairing, or negotiation exists, and it keeps the daemon debuggable with `curl`.

JSON is camelCase, serialized through a source-generated context.

## Commands, not endpoints

The mental model:

```
Peer A sends a Command to Peer B
Peer B validates trust
Peer B executes the capability
Peer B returns a Result
```

New features become **registered handlers, not new endpoints**. `POST /clipboard`, `POST /notification` and friends would each add client code, error handling, and versioning surface. One route gives one transport, one dispatch table, one trust check.

Anticipated commands: `Ping`, `GetCapabilities`, `SendClipboardText`, `SendClipboardImage`, `SendScreenshot`, `ShowNotification`, `OpenUrl`.

Two rules that hold now and must survive the next handler:

1. **Capabilities are derived from the registered handler set**, never hand-maintained (`CommandServices.AddCommandHandlers`). A hardcoded list drifts from reality the first time someone adds a handler and forgets it.
2. **Metadata travels apart from the bytes.** No base64-in-JSON (~33% overhead and whole-payload buffering, which the product rules out).

### Implemented: `POST /commands/{name}`

Sized to one handler (`DECISIONS.md` #28), on the **peer listener** only:

- **Trust:** the TLS handshake admits pinned keys (or, during a pairing window, anyone), and the
  whole `/commands` group sits behind a second, application-layer pin check — a caller whose key
  is not pinned gets `403`. The dispatcher refuses again if that check is ever missing.
- **Metadata** is JSON, base64url-encoded, in one `LightDrop-Metadata` request header (at most
  8 KB encoded). Two values are refused.
- **The body is the payload's raw bytes**, streamed. The listener caps every body at 4 KB; a
  handler that needs more raises the cap for its own request.
- An unknown command is `404`.

### `SendFile`

| | |
|---|---|
| Metadata | `{"fileName": "report.pdf", "length": 12345}` |
| Body | the file's bytes; `Content-Length` **required** (`411` without) and equal to `length` (`400` otherwise) |
| Answer | `200 {"savedAs": "report (1).pdf"}` — a file name only, never the receiver's path |
| Refusals | `400 {"error": "…"}` for bad metadata or a name that is empty once made safe |

The receiver treats a paired sender as careful about nothing:

- **The name is sanitized** (`ReceivedFileName`): last segment after both `/` and `\`, control and
  bidi characters stripped, `<>:"/\|?*` replaced, trailing dots and spaces trimmed, Windows
  reserved names prefixed, length capped keeping the extension. The final path must then sit
  directly inside the download folder, or nothing is written.
- **The download folder** is `downloadFolder` from `config.json`, else `~/Downloads`; created if
  missing.
- **Never half a file, never an overwrite.** The body streams to a hidden temp file in the folder,
  then moves over an atomically reserved `name (n).ext`. An aborted or short body leaves nothing.

The sender treats a paired receiver the same way: `savedAs`, refusals and even the HTTP stack's
error text about a malformed answer are sanitized before they reach a log or a terminal. It dials
the address discovery has for the pinned id, never through a proxy, with a 10-second bound on
connect plus handshake and no bound on the transfer. It sends exactly the `length` it declared,
even if the file grows meanwhile.

### Loopback: `POST /api/send`

`{"path": "/abs/path/file", "peer": "mac mini"}` → `200 {"savedAs", "length", "peerDeviceId", "peerName"}`.
`peer` matches a **paired** device by name or id prefix; the address comes from discovery by id.
Errors are `{"error"}`: `400` bad path or ambiguous peer, `404` not paired, `409` paired but not
discovered, `502` the peer could not be reached or refused. Held open for the whole transfer;
closing it cancels the upload. Behind the origin check, so a web page cannot call it — any process
of the same user can (`DECISIONS.md` #28).

## Direction: transport

Boring and inspectable.

- Kestrel + HTTP on loopback for the CLI and the page (`/health`, `/api/*`)
- Kestrel + HTTPS with mutual TLS on the LAN, peer to peer (`/pair/*`, `/commands/*`)
- **WebSocket later** for persistent peer sessions
- JSON contracts
- Streaming for large payloads; never buffer a whole file

The reason for a persistent connection is **direction, not handshake cost**: notifications, inbound clipboard, and remote actions are server-initiated. Request/response alone forces polling, which is either laggy or wasteful.

Open question for that milestone: a multi-GB transfer must not head-of-line block clipboard and notifications on the same socket. Either a separate stream for bulk data, or chunked interleaving.

No gRPC, WebRTC, or custom binary protocol without a demonstrated need.

## Implemented: discovery

**Discovery is presence, not trust.** A discovered peer is a nearby stranger. Nothing about it is
verified, nothing is persisted, and nothing can be sent to it.

Service type `_lightdrop._tcp.local`, IPv4 only.

**Instance name is the device id**, not the device name. Two machines can share a name, and an
identifier collision is not realistic — which sidesteps DNS-SD name-conflict probing entirely. It
also avoids making a human-readable name the browsable label. The friendly name travels in TXT,
and that is what `lightdrop peers` renders. A generic browser such as `dns-sd -B` will show a
GUID; that is accepted.

### TXT record

| Key | Value | Notes |
|---|---|---|
| `txtvers` | `1` | Shape of this record, independent of `pv` |
| `id` | device id | |
| `pv` | protocol version | |
| `plat` | `windows` \| `macos` \| `linux` | |
| `name` | device name | UTF-8, sanitized and bounded on receipt |
| `cap` | comma-separated | **Omitted entirely while empty** — in DNS-SD an absent key differs from an empty one |

About 75 bytes, well inside the 255-byte per-string and ~1300-byte total budgets.

**Deliberately excluded**: the username, filesystem paths, download folder, config location,
anything key-shaped, and the **application version**. `protocolVersion` is the compatibility gate,
so broadcasting an exact build number would hand a passive observer a version fingerprint for no
product benefit. `version` remains available on loopback via `/health`.

### `pv` is what makes a record ours

Browsing one service type does not guarantee only that type is delivered — the mDNS stack raises
instances of other services too. A Google Cast TXT record carries an `id` key, which was once
enough to mint a peer: a television appeared in `lightdrop peers` with a derived name, `unknown`
platform and protocol `0`. **A record without a `pv` key is rejected outright.** The value still
need not parse; a malformed one yields `0` rather than discarding an otherwise valid LightDrop
peer.

### The address is observed, not claimed

Discovery captures the peer's IPv4 address because pairing has to dial something. It is taken from
**the source address of the packet that carried the announcement**, falling back to the advertised
A record only when the transport surfaces no endpoint — a claimed record is the sender's opinion,
while the source address is what the network observed.

It is then checked at ingestion, and an announcement whose address does not survive is **rejected
entirely** rather than listed without one. Only the private, link-local and loopback IPv4 ranges
are accepted:

| Range | Why |
|---|---|
| `10/8`, `172.16/12`, `192.168/16` | ordinary LANs |
| `169.254/16` | link-local, when DHCP does not answer |
| `127/8` | two daemons on one machine, a supported way to exercise discovery |

Everything else is refused, including any public address. **This is the point of the check**: without
it a peer could announce a third party's address and make LightDrop open a connection to a host of
its choosing the moment pairing exists. It is a range check, not a route check — confirming an
address really sits on one of this machine's subnets would mean enumerating interfaces, which Core
must not do. The residual risk is a peer naming a different *local* machine, which is already on
the link and can announce for itself anyway.

### The SRV port is not an authorization boundary

The SRV record advertises the **peer listener's port** (see *Pairing*): where a paired device, or
one pairing right now, connects. It is still not an authorization boundary — knowing it gets a
stranger as far as a TLS handshake that refuses any key not pinned. With nothing paired and no
window open nothing listens on it at all. Port `0` was rejected: the DNS-SD signal for "no service
here" is `Target="."`, not port zero, and zero breaks well-behaved clients for no security gain.

### Untrusted input

Every TXT value is attacker-controlled. On ingestion LightDrop strips Unicode categories `Cc`,
`Cf`, `Zl` and `Zp` — control characters, bidi overrides, zero-width characters — before anything
reaches the registry, so a hostile device name cannot drive a terminal. Fields are length-bounded,
the registry is capped at 256 peers with least-recently-seen eviction, and a peer whose name
sanitizes to nothing is displayed as `Peer <id prefix>`. Sanitization happens **once at the
boundary**, not at render time, so every consumer inherits it.

### `GET /api/peers`

Loopback only, for the local `lightdrop peers` command. The response carries the state of
discovery alongside the list, because an empty list on its own is ambiguous:

```json
{
  "discoveryRunning": true,
  "discoveryStartedAt": "2026-08-23T09:12:35.5Z",
  "peers": [
    {
      "deviceId": "…", "deviceName": "Work Laptop", "platform": "windows",
      "protocolVersion": 1, "capabilities": ["SendFile"], "port": 5534,
      "address": "192.168.0.222", "lastSeen": "…"
    }
  ]
}
```

`discoveryRunning` is false when the transport failed to start — a blocked firewall or a denied
macOS Local Network permission — in which case no peer can ever appear and the daemon says so
definitively. When it is true, `discoveryStartedAt` distinguishes *still looking* from *looked
long enough*: a freshly started daemon has legitimately heard nothing yet, and telling that user
to check their firewall sends them after a problem they do not have. The threshold lives in the
caller, not in this contract.

### Liveness

Peers expire after **180 seconds** without being heard from — three times the announcement
interval, deliberately independent of the DNS TTL. The standard 75-minute PTR TTL would leave a
sleeping laptop listed for an hour, and a device that sleeps or drops off Wi-Fi never sends a
goodbye. A goodbye (TTL 0) evicts immediately. Expiry is computed on read; there is no sweep timer.

### Privacy: the device id is broadcast

The identifier is stable and announced on every network the machine joins. A passive observer who
sees it in two places can correlate the device across locations. This is inherent to mDNS
discovery and is the same tradeoff AirDrop and network printers already make, but it is an
accepted cost, stated here rather than shipped silently. The device name is broadcast too — set
`deviceName` in `config.json` if the machine name is revealing.

### When multicast is blocked

Discovery fails **silently**: an empty peer list, no error. Corporate and guest networks routinely
block multicast. `lightdrop peers` therefore explains the likely causes when it finds nothing.
A manual direct-dial fallback is deferred to pairing, because dialling a manually entered peer
requires that peer to accept LAN traffic.

## Direction: trust

The local network is not trusted.

- Stable device identity
- Explicit pairing before any command is accepted
- A trusted peer store pinning each peer's public key (SPKI), not its certificate
- Unknown peers rejected at the TLS handshake and again per route — no anonymous file writes, no
  unpaired command execution

All four are implemented.

Never invent cryptography; use .NET and platform primitives.

The loopback app (`/health`, `/api/*`, the UI) binds to **loopback only**, always. The one LAN
socket is the peer listener below.

## Pairing

Design: `docs/superpowers/specs/2026-10-09-pairing-window-and-send-design.md`; the cryptography is
`docs/superpowers/specs/2026-08-16-m2-secure-pairing-design.md` as amended there on 2026-10-09.

**The peer listener** is a separate `WebApplication` on `0.0.0.0:<peerPort>` (default loopback port
+ 1, `LIGHTDROP_PEER_PORT`), HTTPS with mutual TLS. It starts at daemon start if any peer is
pinned, or when a pairing window opens. Its handshake admits a client certificate only if the SPKI
is pinned in the current `trustedPeers` — or, while a window is open, any certificate, because the
handshake is the only carrier of a new peer's key; after the window, the key that joined until the
session's deadline. Its route table holds the peer routes and nothing from the loopback app.

**The ceremony.** Both users run `lightdrop pair <peer>`. The lower device id listens, the higher
dials. Bodies are JSON, capped at 4 KB; byte fields are base64.

| Route | Body → answer | Rule |
|---|---|---|
| `POST /pair/hello` | `{deviceId, protocolVersion}` → `{deviceId, commitment}` | Listener only; caller's id must be the session's target and its version this build's; the first key in joins. The same key repeating hello before revealing gets the same commitment. |
| `POST /pair/reveal` | `{nonce}` → `{nonce}` | Only from the key that joined. The first nonce is kept; a different one later is refused. |
| `POST /pair/confirm` | `{accepted}` → `{accepted}` | Only from the key that joined. Held open until the local user answers, then answered with that answer. |

The dialer sends reveal and confirm only to the key it met at hello (pinned client), and abandons
the session if the listener's nonce does not open its commitment.

```
commitment = SHA256("lightdrop-sas-commit-v2" || f(spki_listener) || f(spki_dialer) || f(nonce_listener) || f(""))
code       = SHA256("lightdrop-sas-v2" || f(spki_dialer) || f(spki_listener) || f(nonce_dialer) || f(nonce_listener))
             first 4 bytes, big-endian -> mod 10^6 -> 6 digits, zero-padded
f(x)       = uint32 big-endian length of x || x;   nonces are 32 random bytes per session
```

The listener commits before it sees the dialer's nonce and reveals after — Bluetooth Secure Simple
Pairing's numeric comparison. A device in the middle therefore gets one blind try at matching six
digits rather than an offline search, and the digits differ every session.

Each side pins (`trustedPeers`) only when both users said yes, using the peer's name as discovery
sanitized it. A dialer's "yes" counts only while its confirm is still waiting for the answer; if it
gives up, it says yes again when it retries. Unpair is local only.

**The loopback side** (`DECISIONS.md` #30): the CLI owns the prompt, the daemon owns the session.
Behind the origin check, like every loopback write.

| Route | Purpose |
|---|---|
| `POST /api/pairing` `{peer}` | Start a session with a **discovered** peer, matched by name (case-insensitive) or id prefix. `404` unknown, `400` ambiguous, `409` already paired or a session is busy, `500` the peer listener could not bind. |
| `GET /api/pairing` | Snapshot: state, peer, the code once known, a failure reason. |
| `POST /api/pairing/confirm` `{accepted}` | This user's answer. `409` if no code is waiting. |
| `DELETE /api/pairing` | Cancel (Ctrl+C in `lightdrop pair`). |
| `GET /api/trusted` | Paired devices: id, name, when — never key material. |
| `DELETE /api/trusted/{deviceId}` | Unpair. Only this side forgets; the other keeps its pin until it unpairs too, and its next connection is refused at the handshake. |

Deadlines: 60 s for the other device to join, then 60 s to confirm once the code is shown. Expiry,
a "no" on either side, or a nonce that does not open its commitment discards the session with
nothing persisted on either side.
