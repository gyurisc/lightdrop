# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build LightDrop.sln            # warnings are errors; keep this at 0 warnings
dotnet test LightDrop.sln             # all tests
dotnet test tests/LightDrop.Core.Tests            # logic only, fast
dotnet test tests/LightDrop.Daemon.Tests          # real file I/O and real HTTP
dotnet test --filter "FullyQualifiedName~JsonStateStoreTests"                        # one class
dotnet test --filter "FullyQualifiedName~JsonStateStoreTests.ThrowsOnCorruptState"   # one test

dotnet run --project src/LightDrop.Cli -- daemon    # run the daemon in the foreground
dotnet run --project src/LightDrop.Cli -- health    # query a running daemon
```

Publish the single portable executable (`lightdrop.exe` / `lightdrop`):

```bash
dotnet publish src/LightDrop.Cli -c Release -r win-x64   --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true
dotnet publish src/LightDrop.Cli -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true
```

Trimming takes the Windows build from ~99 MB to ~18 MB with **zero trim warnings**. Protect that — see "AOT and trimming".

CI runs restore/build/test on `windows-latest` and `macos-latest`.

## Documentation

`docs/Architecture.md` (responsibilities), `docs/Protocol.md` (contracts and direction), `docs/Roadmap.md` (milestones), `docs/DECISIONS.md` (tradeoffs and why). Update them when architecture or protocol changes. Prefer a useful two-page doc over a long stale one.

**The repository is public.** In docs, tests, code comments and commit messages, describe test machines generically ("the Mini", "a Windows desktop") and use placeholders for anything real. That means no real IP or MAC addresses, host names, device ids, people's names, home-folder paths, or the models of other devices on the network. Use generic private addresses such as `192.168.1.50` in tests (they must stay private, or `LocalNetworkAddress` rejects them). Recording OS versions is fine.

## Architecture

```
Cli ──▶ Daemon ──▶ Core
 └────────────────▶ Core
```

**`LightDrop.Cli` is the only executable** (`AssemblyName` = `lightdrop`). `lightdrop daemon` hosts the daemon in-process — there is no second binary, because single-executable is a product requirement.

**`LightDrop.Daemon` is a class library**, consuming ASP.NET Core via `FrameworkReference` and exposing `LightDropDaemon.Create` / `RunAsync` instead of a `Main`. Never give it `OutputType=Exe`. It owns Kestrel, endpoints, and **all file I/O**.

**`LightDrop.Core` is platform independent** — no ASP.NET Core, no filesystem, no OS-specific APIs. It owns wire contracts, the `IConfigStore`/`IStateStore` ports, and the logic over them.

The logic/I-O split sits one notch from the obvious place on purpose: `DeviceIdentityProvider` (get-or-create rules) lives in Core behind two dumb ports, so it is testable with in-memory fakes. There is deliberately no `LightDrop.Infrastructure` project.

## Interfaces are for ports, not habit

`IConfigStore` / `IStateStore` exist because infrastructure implements them and tests substitute them. `ICliCommand` exists because DI resolves it as a collection.

`DeviceIdentityProvider`, `HealthService`, `PeerRegistry`, `PairingCoordinator` and `PeerListener` are **concrete classes** — each has one implementation and no test double. Do not add an interface because "services get interfaces." `ICommandHandler` is the DI-collection exception, like `ICliCommand`.

## The protocol is command-oriented, sized to one handler

New capabilities should become **registered handlers, not new endpoints**: `POST /commands/{name}` on the peer listener, `ICommandHandler` in DI. See `docs/Protocol.md`.

The scaffold was built, deleted before any handler existed (`docs/DECISIONS.md` #11), and rebuilt for `SendFile` alone (#28). Grow it when a second handler needs more — a reply stream, a persistent session (M4) — not before. Three rules must survive:

- **Capabilities are derived from the registered handler set**, never hand-maintained (`CommandServices.AddCommandHandlers`).
- **Metadata travels apart from the bytes.** Metadata in the `LightDrop-Metadata` header; the body is the raw stream. No base64-in-JSON, no whole-file buffering.
- **The pin check guards the whole `/commands` group**, so a new handler cannot forget it.

`/health` stays a plain HTTP GET, not a command — it is the bootstrap probe and must answer before any socket, pairing, or negotiation exists.

## Discovery is presence, not trust

Peers are **nearby strangers**. Three invariants that should fail review if broken:

- **Nothing under `Discovery` may reference `IStateStore`.** A discovered peer must never reach
  `state.json` or `trustedPeers`. Pairing crosses that boundary explicitly, not by inheriting a
  path that already exists.
- **Discovery opens no socket.** All peer metadata rides in mDNS TXT records. `GET /api/peers` is
  for the local CLI. The one LAN socket belongs to pairing (below), not discovery.
- **The registry stays bounded** (256, least-recently-seen eviction). Anyone on the link can invent
  unlimited identifiers.

Every TXT value is attacker-controlled. Sanitization happens **once at ingestion** in
`PeerAnnouncement.TryCreate`, never at render time — otherwise the next consumer reintroduces the
terminal-escape bug. Do not construct a `PeerAnnouncement` any other way.

`MdnsPeerDiscoveryTransport` is intentionally untested: multicast cannot run in CI, and macOS drops
it silently without the Local Network permission, so a test would hang rather than fail. **Every
daemon test must pass a no-op or fake transport** or it will open real sockets.

## config.json vs state.json — never merge these

In `%APPDATA%\LightDrop` (Windows) / `~/Library/Application Support/LightDrop` (macOS):

| File | Owner | LightDrop may | Contains |
|---|---|---|---|
| `config.json` | the user, by hand | **read only** | `deviceName`, `downloadFolder` |
| `state.json` | the application | read and write | `deviceId`, `trustedPeers` |

If the app writes it, it is state. Putting `trustedPeers` in config would mean pairing clobbers hand-edited settings.

Failure behavior is intentionally asymmetric: **config unreadable → warn and use defaults** (a typo must not kill a background utility); **state corrupt → throw** (silently starting fresh would mint a new `deviceId` and invalidate every pairing everywhere). `JsonStateStore` writes temp-file-then-rename, and creates the file `0600` on Unix behind an `OperatingSystem.IsWindows()` guard — `UnixCreateMode` throws on Windows rather than being ignored.

Configuration deliberately avoids `ConfigurationBuilder` and `appsettings.json`. Defaults live in code; `IOptions<T>` is fed by hand. `LIGHTDROP_HOST` / `LIGHTDROP_PORT` / `LIGHTDROP_PEER_PORT` / `LIGHTDROP_DATA_DIR` are development escape hatches.

## The loopback app stays on loopback; the LAN gets one pinned-only listener

The loopback app — `/health`, `/api/*`, the page — binds `127.0.0.1:5533` and **never moves**. Several of its routes (`/api/send`, `/api/pairing`) are trusted precisely because only local processes reach them.

The one deliberate exception (`docs/DECISIONS.md` #26) is the **peer listener**: a *separate* `WebApplication` on `0.0.0.0:<peerPort>` (loopback port + 1, `LIGHTDROP_PEER_PORT`), mutual TLS, started only when a peer is pinned or a pairing window opens. Invariants that should fail review if broken:

- **Never a second binding on the loopback app**, and never `RequireHost` to scope routes — it matches the attacker-written `Host` header. The peer app's route table holds `/pair/*` and `/commands/*` and nothing else.
- **Trust is checked twice:** the handshake admits only SPKIs in the *current* `trustedPeers` (any key while a pairing window is open), and every non-pairing route re-checks the pin. Each must stay load-bearing under mutation: replace it with `true` and exactly its test fails.
- **No LAN socket on a fresh install.** Nothing paired and no window open means nothing listens.
- **Pin with the name discovery sanitized**, never a name from a pairing body; received file names go through `ReceivedFileName` and must land directly inside the download folder.

## AOT and trimming

`IsAotCompatible` is set on all three projects, turning trim/AOT violations into build errors. Two things keep it passing:

- **`System.Text.Json` source generation** — every serializable type must be listed on `LightDropJsonContext`. A missing entry fails at **runtime**, not at build.
- **`EnableRequestDelegateGenerator`** on the daemon — without it `MapGet` reflects over the handler and trips IL2026/IL3050.

Use `[LoggerMessage]` source-generated logging for the same reason.

## Testing

- **Core tests** — logic with in-memory port fakes. No filesystem, no network.
- **Daemon tests** — real file I/O in temp directories; real HTTP against a real Kestrel binding on an OS-assigned free port.

`WebApplicationFactory` does not work here: it reflects over an entry point the Daemon library does not have, and its in-memory transport would not exercise the Kestrel binding.

**Every daemon test must pass an explicit data directory.** `LightDropDaemon.Create` and `RunAsync` both take one; omitting it makes the test read and write the real user profile. Use `TempDataDirectory`.

Prefer behavior over implementation detail: do not assert on the `.tmp` filename, log message text, or shutdown timings. Crash-mid-write atomicity is not black-box testable — that guarantee rests on inspection of the rename.

## Product constraints that veto designs

- **No cloud, no accounts, no installation, no configuration.**
- **Local network only**, peer-to-peer, encrypted.
- **Not** Dropbox, Syncthing, Google Drive, Remote Desktop, SSH, a sync service, or a backup tool. LightDrop moves a thing from A to B on demand — it does not reconcile folders or maintain cross-device state. Flag drift toward sync semantics as scope creep.
- Never invent cryptography. Do not hand-roll mDNS if a mature library exists.

## Known gaps

- macOS was verified by hand on 2026-08-16 (Mac Mini, Apple Silicon, macOS 15.7.4): clean build, full test suite, trimmed single-file publish, `state.json` created `0600`, config loading, stable identity across restarts, and two-way discovery with Windows. `Environment.MachineName` returned the plain computer name — the old claim that it yields `Something.local` was wrong.
- **Denying the macOS Local Network permission did not stop discovery** on 15.7.4 — the daemon kept advertising *and* kept seeing peers with the permission explicitly off. The README's claim that discovery requires it is unverified. One machine, no reboot between toggling and testing, so this is an observation rather than proven causation.
- macOS release binaries need an executable bit and a Gatekeeper story (codesign/notarize, or a documented `xattr` workaround). CI does not publish.
- Discovery is verified between two real machines (Windows ↔ macOS, 2026-08-16; Mac Mini ↔ MacBook Air, 2026-10-12, after #32). **Pairing and transfers both ways are verified between two Macs** (2026-10-12/13): 100 MB each way, 4.5 GiB one way, Ctrl+C mid-send, a sleeping peer, and restarts on both sides. Unpair-then-send, declining the code and Ctrl+C during `pair` were verified the same day. Still open: Windows at either end, and Wi-Fi ↔ Wi-Fi throughput — the checklist is in `docs/Roadmap.md` M2/M3.
- `lightdrop autostart` is macOS-only (`docs/DECISIONS.md` #33), and its log file is never rotated. A launchd-started daemon needs its own Local Network permission; on 2026-10-12 the Mini's first background daemon heard nothing for 12 minutes, while the same binary started from a shell heard peers at once, and a restart fixed it. Likely it started before the grant existed, and its sockets stayed blocked. The daemon now reopens its mDNS sockets when it has not heard its own announcement for 150 s (`docs/DECISIONS.md` #34); whether that cures this exact case is unproven, because it could not be reproduced on demand. The `launchctl` side is verified by hand (2026-10-12, Mac Mini: on, off, crash restart, takeover from a terminal daemon); only the plist rendering is unit tested.
- If the peer port cannot be bound at daemon start (port in use, Hyper-V excluded range), the daemon logs one error and runs without receiving. `lightdrop health` reports it (`receiving NO`, with the reason) until a restart or the next pairing window starts the listener; nothing retries on its own (`docs/DECISIONS.md` #36).
- Windows shows a Defender Firewall prompt for the peer port; behaviour when it is denied is untested. With the macOS firewall on, macOS asks the same ("accept incoming network connections?"), and asks again after every rebuild under `dotnet run`, because the binary changes.
- Discovery re-asks (with unicast answers requested) and re-announces every 60 s (`docs/DECISIONS.md` #32), because a home router was found carrying multicast in one direction only. A network that drops multicast in both directions still defeats it; there is no manual-address fallback.
- The mDNS library is a community fork of a project abandoned in 2019, and its `Common.Logging` dependency required a targeted trim suppression (`docs/DECISIONS.md` #16).
