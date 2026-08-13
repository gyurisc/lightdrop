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

## Direction: discovery

- mDNS / Zeroconf, service type `_lightdrop._tcp.local`
- TXT metadata: device name, id, platform, protocol version, capabilities, port
- A manual fallback is **required** — corporate networks routinely block multicast

Use a mature library. Do not hand-roll mDNS.

## Direction: trust

The local network is not trusted.

- Stable device identity (implemented)
- Explicit pairing before any command is accepted
- Trusted peer store with certificate fingerprint or public-key pinning
- Unknown peers rejected — no anonymous file writes, no unpaired command execution

Never invent cryptography; use .NET and platform primitives.

Until pairing exists, the daemon binds to **loopback only**. Binding to the LAN before there is authentication would expose an unauthenticated endpoint to every device on the network.
