# LightDrop Protocol

Status: **v1 in progress.** Only `/health` is implemented. Everything under "Direction" is intent, not contract.

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
  "capabilities": []
}
```

| Field | Notes |
|---|---|
| `version` | Application version. Informational. |
| `protocolVersion` | Compatibility gate. |
| `deviceId` | Generated once, persisted, never changes without an explicit reset. Trust pins to this. |
| `deviceName` | Human-readable. How the CLI addresses peers. |
| `platform` | `windows` \| `macos` \| `linux` \| `unknown`. A short stable token, not `RuntimeInformation.OSDescription`. |
| `capabilities` | Commands this device accepts. Empty until file transfer lands. |

Health stays a **plain HTTP GET**, not a command. It is the bootstrap probe: it must answer before any socket, pairing, or negotiation exists, and it keeps the daemon debuggable with `curl`.

JSON is camelCase, serialized through a source-generated context.

## Direction: commands, not endpoints

The mental model:

```
Peer A sends a Command to Peer B
Peer B validates trust
Peer B executes the capability
Peer B returns a Result
```

New features become **registered handlers, not new endpoints**. `POST /clipboard`, `POST /notification` and friends would each add client code, error handling, and versioning surface. One envelope gives one transport, one dispatch table, one trust check.

Anticipated commands: `Ping`, `GetCapabilities`, `SendFile`, `SendClipboardText`, `SendClipboardImage`, `SendScreenshot`, `ShowNotification`, `OpenUrl`.

Two rules that must survive implementation:

1. **Capabilities are derived from the registered handler set**, never hand-maintained. A hardcoded list drifts from reality the first time someone adds a handler and forgets it.
2. **The envelope carries metadata only.** File bytes stream on a separate channel keyed by the envelope id. Base64-in-JSON costs ~33% overhead and forces whole-payload buffering, which the product explicitly rules out.

**The command scaffold is not built yet, on purpose.** An earlier draft existed with zero handlers and was deleted: its shape had been validated by nothing, and streaming requirements are likely to reshape it. It will be rebuilt at the file-transfer milestone, sized to what a real handler needs.

## Direction: transport

Boring and inspectable.

- Kestrel + HTTP for simple request/response endpoints (`/health`, `/api/peers`)
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

DNS-SD requires a port in the SRV record, so the daemon's real port is advertised. **It is not
reachable from the network** — Kestrel binds loopback only — and it does not mean "this peer
accepts connections". Do not treat it as one until pairing exists. Port `0` was rejected: the
DNS-SD signal for "no service here" is `Target="."`, not port zero, and zero breaks well-behaved
clients for no security gain.

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
      "protocolVersion": 1, "capabilities": [], "port": 5533,
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

- Stable device identity (implemented)
- Explicit pairing before any command is accepted
- Trusted peer store with certificate fingerprint or public-key pinning
- Unknown peers rejected — no anonymous file writes, no unpaired command execution

Never invent cryptography; use .NET and platform primitives.

Until pairing exists, the daemon binds to **loopback only**. Binding to the LAN before there is authentication would expose an unauthenticated endpoint to every device on the network.
