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

## M1 — Local peer discovery ✅ (verified by hand, Windows ↔ macOS)

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

Verified 2026-08-16 between a Windows 11 desktop (Ethernet, `192.168.0.222/24`) and a Mac Mini
(Apple Silicon, macOS 15.7.4), both built from source at `8a67966`.

- [x] Two daemons on one Windows machine (`LIGHTDROP_PORT`, `LIGHTDROP_DATA_DIR`) discover each
      other; neither lists itself
- [x] Trimmed single-file binary runs discovery without a reflection failure — now on macOS too
- [x] **Two real machines on the same LAN discover each other** — the actual milestone claim.
      Both directions, cross-platform, neither listing itself. A device-name change propagated
      into the existing entry without duplicating it.
- [x] macOS: denying the Local Network permission leaves the daemon running, `/health` answering,
      and `peers` returning promptly rather than hanging
- [ ] macOS: the permission prompt appears on first run — **inconclusive.** Terminal held the grant
      and no prompt was observed for LightDrop specifically. Note the grant attaches to the
      *terminal app*, not to `lightdrop`; launching from Finder is a different path, untested.
- [ ] Windows: behaviour with the Defender Firewall prompt **denied** — the allowed case works, but
      it was never tested from a clean state: six `lightdrop` allow rules already existed on the
      Public profile, alongside the built-in `mDNS (UDP-In)` Public rule
- [x] Ctrl+C on one machine removes it from the other's list promptly (goodbye packet) — immediate,
      nowhere near the 180s window
- [ ] Sleeping a machine ages it out within ~180s rather than instantly or never
- [ ] A multicast-blocking network: daemon still starts, `/health` still works, peers list empty
- [x] macOS device name shows a friendly name — `Environment.MachineName` returned `Pips-Mac-mini`,
      with no `.local` suffix
- [x] A machine with Hyper-V/WSL adapters up still discovers — a `vEthernet` adapter was up on
      `172.26.176.1` throughout; the interface filter did its job. VPN adapters still untested.

**Surprise worth chasing:** on macOS 15.7.4, denying the Local Network permission did not stop
discovery at all — the Mac kept advertising *and* kept seeing the Windows peer with the permission
explicitly off. The documented premise that discovery requires it is unverified. Caveats: one
machine, and no reboot between revoking the permission and testing, so this is an observation, not
proven causation.

**Known risks:** the macOS Local Network permission never re-prompts once denied — the System
Settings toggle is the only way back, and the app cannot explain that in-band. The mDNS library is
a community fork of a project abandoned in 2019.

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
