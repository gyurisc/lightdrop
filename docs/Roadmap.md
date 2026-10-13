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

Verified 2026-08-16 between a Windows 11 desktop (Ethernet) and a Mac Mini
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
- [x] Sleeping a machine ages it out within ~180s rather than instantly or never — measured
      2026-10-13 (Mini ↔ Air): the Mini dropped the sleeping Air exactly 180 s after last hearing
      it, and heard it again about 25 s after it woke. While the lid was still shut the Air also
      woke briefly on its own (Power Nap) and announced for about a minute, which explains the 22
      reappearances the Mini logged overnight.
- [ ] A multicast-blocking network: daemon still starts, `/health` still works, peers list empty
- [x] macOS device name shows a friendly name — `Environment.MachineName` returned the plain computer
      name, with no `.local` suffix
- [x] A machine with Hyper-V/WSL adapters up still discovers — a `vEthernet` adapter was up
      throughout; the interface filter did its job. VPN adapters still untested.

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

Started 2026-10-12: Mac Mini (Ethernet and Wi-Fi, published trimmed binary) and a MacBook Air M3
(Wi-Fi only, `dotnet run`), both macOS, both with the macOS firewall on, home router.

- [x] Both machines discover each other (`lightdrop peers`), as in M1 — **only after `60e6a56`.**
      The router carried the Air's multicast to the Mini but none of the Mini's to the Air, and the
      daemon never re-asked, so peers also vanished after three minutes. Fixed by a per-minute
      unicast-answer query plus re-announcement (`DECISIONS.md` #32)
- [x] `lightdrop pair` on both within a minute: same code on both screens (`606 896`), both pinned
- [ ] Windows: the Defender Firewall prompt appears for the peer port; allowed → pairing works;
      **denied** → what the user sees
- [x] macOS: the **firewall** (when on) asks whether `lightdrop` may accept incoming connections;
      allowed → works. Under `dotnet run` it asks again after every rebuild. Denied: untested
- [ ] Answering `n` on one side: nothing pinned on either
- [ ] Ctrl+C during `pair` on one side: the other times out cleanly, nothing pinned
- [x] Restart both daemons: still paired, peer listener up at start — 2026-10-13, after both were
      redeployed with `1839c41` and restarted under `autostart`: a send each way succeeded

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
  - [x] 100 MB both ways, SHA-256 identical (2026-10-12); Mini (Ethernet) → Air (Wi-Fi) at
        13.1 MB/s,
        Air → Mini at 31.2 MB/s
  - [x] a multi-GB file — 4.5 GiB (past both the 2 GiB and 4 GiB marks) Mini → Air in 1 min 16 s
        at 60.2 MB/s, SHA-256 identical (2026-10-13)
  - [x] a small file — 100 bytes each way (2026-10-13)
  - [ ] Wi-Fi ↔ Wi-Fi throughput
  - [x] Ctrl+C mid-send: nothing appears under the final name on the receiver — interrupted about
        20 s into the 4.5 GiB send: the CLI exited in 0.02 s, the Mini's connection to the Air
        closed and its upload stopped, no `.partial` was left on the Air, and the full resend was
        saved under the original name rather than as a copy (2026-10-13)
  - [ ] unpair on the receiver, then send: refused
  - [x] a peer that went to sleep: `send` fails within ~10 s, not 75 — 8.1 s while the Air was still
        listed ("may be offline or asleep"); 0.2 s once the Mini had dropped it ("paired but not on
        the network right now") (2026-10-13)

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

`lightdrop autostart` on Windows without a console window (`DECISIONS.md` #33). Clipboard text, clipboard images, screenshots, `ShowNotification`, `OpenUrl`, Explorer/Finder integration, Claude Code image handoff, agent-to-agent communication.

## Not doing

Sync, backup, cloud relay, accounts, remote desktop, general file management. LightDrop moves a thing from A to B on demand.
