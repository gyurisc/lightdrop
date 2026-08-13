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
