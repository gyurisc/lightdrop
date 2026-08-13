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

## M1 — Local peer discovery

**Goal:** two machines on a LAN see each other with no configuration.

- mDNS advertisement of `_lightdrop._tcp.local`
- mDNS browsing, with TXT metadata: name, id, platform, protocol version, capabilities, port
- In-memory peer registry with liveness/expiry
- `GET /api/peers`
- `lightdrop peers`
- Manual fallback for networks that block multicast

**Requires a decision first:** the daemon must bind beyond loopback to be discoverable, and there is no pairing yet. Either discovery is read-only (advertise and browse, accept no commands) or M2 lands first. Read-only discovery is the smaller step.

**Also needed:** a data-directory override so two daemons can run on one machine without fighting over a single identity. `LightDropDaemon` already takes one; the CLI does not expose it yet.

**Known risks:** multicast blocked on corporate networks; macOS firewall prompts; sleeping devices leaving stale entries; hostnames on macOS surfacing as `Krisztians-MacBook-Air.local` rather than a friendly name.

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
