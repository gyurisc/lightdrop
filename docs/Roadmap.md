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

## M2 — Secure pairing ✅ built — awaiting two-machine verification

**Goal:** peers explicitly trust each other; unknown peers are rejected.

**This is the one milestone that cannot be retrofitted.** Design agreed in
`docs/superpowers/specs/2026-08-16-m2-secure-pairing-design.md`. Never invent cryptography.

- [x] **Device key pair generated alongside identity** — `7eff527`. ECDSA P-256, private key in
      `state.json`, certificate reissued every start because pairing pins the key, not the
      certificate.
- [x] **Discovery captures a peer's address** — `eec47f4`. Pairing has to dial something, and the
      packet source is preferred over the claimed A record (`DECISIONS.md` #23).
- [x] **Pairing code and the trusted-peer store** — `03811ab`. `PairingCode.Derive` produces six
      digits from both public keys, sorted so both sides agree without a round trip.
      `PairingService` holds pin-or-reject and refuses to re-pin.
- [x] **Pinned TLS handshake** — `72d01db`. `PinnedCertificate.Matches` plus `PairingTls`
      configuring both ends of a mutual-TLS connection. Verified by mutation five times: replacing
      a pin check with `true` must fail exactly the corresponding rejection test.
- [x] **The pairing window itself** — `161a022`, `dd2c7c2`, `7c2aeb6`. A pinned-only peer listener
      (`DECISIONS.md` #26), 60-second deadlines, roles by device id, the session state machine in
      Core, and a commit-then-reveal code (#27).
- [x] **A deferred-validation TLS mode** — `dd2c7c2`. The listener admits any key only while a
      window is open; the dialer captures the first key and pins it for the rest of the session.
- [x] `lightdrop pair`, `lightdrop unpair`, `lightdrop peers --trusted` — `5f2e5c7`.
- [ ] The pairing ceremony in the web UI (phase B of the companion UI) — deferred; the CLI is enough
      to test with
- [ ] Manual verification between two real machines — the checklist below

### Verified on one machine (2026-10-09)

Two daemons on one Mac (macOS 15.7.4, Apple Silicon), the **published trimmed osx-arm64 binary**
built at `cf8e6e9`, **real mDNS**, separate ports and data dirs. This is not the milestone claim —
both daemons share a loopback interface, a firewall and an OS — but it exercises everything
except the network between two machines.

- Discovered each other within a second of start; no LAN socket until pairing
- `pair` run concurrently on both: the **same six digits on both screens** (`851 729`), both pinned,
  `peers --trusted` lists the other on each side, both peer listeners now on `*:<peerPort>`
- A second ceremony after unpairing showed a **different** code (`735 743`); answering `n` on one
  side left `trustedPeers` empty on **both**
- After a one-sided unpair, the still-pinned side's `send` is refused: "no longer trusts this device"
- A 1.2 KB, a 13-byte and two 50 MB files, both directions: SHA-256 identical; a repeat arrived as
  `small (1).bin`; no temp files left behind
- Restarting a paired daemon reopens the peer listener at start
- `curl` to the peer port with no client certificate, or an unpinned self-signed one, gets no HTTP
  response at all — `/health` and `/commands/SendFile` alike

### Manual verification between two real machines

Not done. Each needs a clean `state.json` the first time, and a `downloadFolder` worth checking.

- [ ] Both machines discover each other (`lightdrop peers`), as in M1
- [ ] `lightdrop pair` on both within a minute: same code on both screens, both pinned
- [ ] Windows: the Defender Firewall prompt appears for the peer port; allowed → pairing works;
      **denied** → what the user sees
- [ ] macOS: a Local Network or firewall prompt for the incoming peer port, if any
- [ ] Answering `n` on one side: nothing pinned on either
- [ ] Ctrl+C during `pair` on one side: the other times out cleanly, nothing pinned
- [ ] Restart both daemons: still paired, peer listener up at start

### Known traps

- **The origin-check middleware is global** to the loopback app. Resolved by giving the peer
  listener its own `WebApplication` (#26); do not "simplify" it into a second binding on the
  loopback app, which would expose `/health`, `/api/*` and the UI to the LAN.
- **macOS negotiates TLS 1.2**, not 1.3. The suite asserts a 1.2 floor rather than forcing 1.3,
  which would have broken the handshake on one of the two platforms this project targets.

## M3 — File transfer 🔨 first slice done

**Goal:** `lightdrop send screenshot.png "Work Laptop"` works.

- [x] Command route and dispatcher, sized to one handler — `5af505e` (`DECISIONS.md` #28)
- [x] `SendFile` with a streamed payload, never buffered — `5af505e`
- [x] Download folder resolution and collision handling — `5af505e`
- [x] Capabilities projected from registered handlers — `5af505e`
- [x] Sender hardened against its receiver and the network: sanitized errors, no proxy, a bounded
      dial (#29), exactly the declared length sent
- [ ] Progress reporting (the CLI reports size, duration and throughput at the end only)
- [ ] Manual verification between two real machines (one-Mac smoke test passed, see M2):
  - [ ] send a small and a multi-GB file both ways; checksums match; throughput is reasonable over
        Wi-Fi and Ethernet
  - [ ] Ctrl+C mid-send: nothing appears under the final name on the receiver
  - [ ] unpair on the receiver, then send: refused
  - [ ] a peer that went to sleep: `send` fails within ~10 s, not 75

**Known gap:** if the peer port cannot be bound when the daemon starts with peers already paired
(another process holds it, or it falls in a Hyper-V excluded range on Windows), the daemon logs one
warning and runs without receiving. Senders then see "could not send … may be offline". This is
deliberate today (`PeerListenerTests.ABindFailureOtherThanPortInUseIsReportedNotFatal`): the daemon
stays useful for sending. Making it visible in `lightdrop health`, or fatal while peers are pinned,
is open. When testing, watch the daemon's output for that warning.

## M4 — Persistent sessions

WebSocket transport for server-initiated commands. Required before anything push-based: notifications and inbound clipboard cannot work well over polling.

Must solve head-of-line blocking — a large transfer cannot stall small commands.

## Later

Clipboard text, clipboard images, screenshots, `ShowNotification`, `OpenUrl`, Explorer/Finder integration, Claude Code image handoff, agent-to-agent communication.

## Not doing

Sync, backup, cloud relay, accounts, remote desktop, general file management. LightDrop moves a thing from A to B on demand.
