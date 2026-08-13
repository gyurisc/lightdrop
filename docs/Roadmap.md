# LightDrop Roadmap

One milestone at a time. Each ships working, tested software. Nothing starts before the milestone below it is reliable.

## M0 — Foundation ✅

- Solution, three projects, warnings-as-errors, central package management
- `lightdrop daemon` hosting Kestrel in-process; `lightdrop health`
- Stable device identity, generated once and persisted
- `config.json` / `state.json` split with atomic writes
- Structured JSON logging, graceful shutdown
- Core and Daemon test suites; CI on Windows and macOS
- Single-file publish, trimmed, zero trim warnings

## M1 — Local peer discovery ✅ (Windows; macOS unverified by hand)

**Goal:** two machines on a LAN see each other with no configuration.

- mDNS advertisement and browsing of `_lightdrop._tcp.local`, IPv4 only
- In-memory peer registry: expiry, deduplication, self-filtering, bounded at 256
- `GET /api/peers` (loopback) and `lightdrop peers`
- `LIGHTDROP_DATA_DIR` so two daemons can run on one machine

**Resolved:** discovery is read-only presence. Kestrel stays loopback-bound, all metadata rides in
mDNS TXT records, and no LAN-reachable HTTP was added. Pairing is where a peer-to-peer LAN endpoint
is opened deliberately, because that is when authenticated communication is actually needed.

**Deferred to M2:** the manual direct-dial fallback for multicast-blocked networks. Dialling a
manually entered peer requires that peer to accept LAN traffic, which reopens the question M1
closed. `lightdrop peers` explains the likely causes when it finds nothing.

### Manual verification

Automated tests cannot cover multicast: CI runners cannot route it, and macOS drops it silently
without the Local Network permission, so a test would hang rather than fail. These must be done by
hand.

- [x] Two daemons on one Windows machine (`LIGHTDROP_PORT`, `LIGHTDROP_DATA_DIR`) discover each
      other; neither lists itself
- [x] Trimmed single-file binary runs discovery without a reflection failure
- [ ] **Two real machines on the same LAN discover each other** — the actual milestone claim
- [ ] macOS: the Local Network permission prompt appears; denying it leaves the daemon running and
      the peer list empty rather than hanging
- [ ] Windows: behaviour with the Defender Firewall prompt allowed and denied
- [ ] Ctrl+C on one machine removes it from the other's list promptly (goodbye packet)
- [ ] Sleeping a machine ages it out within ~180s rather than instantly or never
- [ ] A multicast-blocking network: daemon still starts, `/health` still works, peers list empty
- [ ] macOS device name shows the friendly name, not `Something.local`
- [ ] A machine with VPN/Hyper-V/WSL adapters up still discovers

**Known risks:** macOS Local Network permission fails silently and cannot be reset with `tccutil`;
`Environment.MachineName` on macOS yields `Something.local`; the mDNS library is a community fork
of a project abandoned in 2019.

## M2 — Secure pairing

**Goal:** peers explicitly trust each other; unknown peers are rejected.

- Device key pair generated alongside identity
- Explicit pairing handshake with a short human-verifiable code
- Trusted peer store in `state.json`, with fingerprint or public-key pinning
- Reject every unpaired peer
- `lightdrop pair`, `lightdrop peers --trusted`

**This is the one milestone that cannot be retrofitted.** It needs its own design pass, a security review, and no shortcuts. Never invent cryptography.

## M3 — File transfer

**Goal:** `lightdrop send screenshot.png "Work Laptop"` works.

- Command envelope and dispatcher, sized to what a real handler needs
- `SendFile` with streamed payload — never buffer a whole file
- Download folder resolution and collision handling
- Progress reporting
- Capabilities projected from registered handlers

## M4 — Persistent sessions

WebSocket transport for server-initiated commands. Required before anything push-based: notifications and inbound clipboard cannot work well over polling.

Must solve head-of-line blocking — a large transfer cannot stall small commands.

## Later

Clipboard text, clipboard images, screenshots, `ShowNotification`, `OpenUrl`, Explorer/Finder integration, Claude Code image handoff, agent-to-agent communication.

## Not doing

Sync, backup, cloud relay, accounts, remote desktop, general file management. LightDrop moves a thing from A to B on demand.
