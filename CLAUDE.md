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

`DeviceIdentityProvider` and `HealthService` are **concrete classes** — each has one implementation and no test double. Do not add an interface because "services get interfaces." When the peer registry arrives, it does not need `IPeerRegistry`: an in-memory registry has no boundary to abstract.

## The protocol direction is command-oriented — but nothing is built yet

New capabilities should become **registered handlers, not new endpoints**. See `docs/Protocol.md`.

A command envelope, dispatcher and registry were written and then **deleted** before any handler existed (`docs/DECISIONS.md` #11). Do not rebuild them until the file-transfer milestone, and then size them to what a real handler needs. Two rules must survive that rebuild:

- **Capabilities are derived from the registered handler set**, never hand-maintained. `HealthResponse.Capabilities` currently returns `[]`.
- **The envelope carries metadata only.** File bytes stream separately; no base64-in-JSON, no whole-file buffering.

`/health` stays a plain HTTP GET, not a command — it is the bootstrap probe and must answer before any socket, pairing, or negotiation exists.

## Discovery is presence, not trust

Peers are **nearby strangers**. Three invariants that should fail review if broken:

- **Nothing under `Discovery` may reference `IStateStore`.** A discovered peer must never reach
  `state.json` or `trustedPeers`. Pairing crosses that boundary explicitly, not by inheriting a
  path that already exists.
- **Kestrel stays loopback-bound.** All peer metadata rides in mDNS TXT records, so discovery added
  no LAN-reachable HTTP. `GET /api/peers` is for the local CLI.
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

Configuration deliberately avoids `ConfigurationBuilder` and `appsettings.json`. Defaults live in code; `IOptions<T>` is fed by hand. `LIGHTDROP_HOST` / `LIGHTDROP_PORT` are a development escape hatch.

## Kestrel binds to loopback by default

`127.0.0.1:5533`. A security decision, not an oversight: there is no pairing yet, so LAN binding would expose an unauthenticated endpoint to every device on the network. **Do not change this without pairing or an explicit decision** — see the open question in `docs/Roadmap.md` M1.

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

- macOS was verified by hand on 2026-08-16 (Mac Mini, Apple Silicon, macOS 15.7.4): clean build, full test suite, trimmed single-file publish, `state.json` created `0600`, config loading, stable identity across restarts, and two-way discovery with Windows. `Environment.MachineName` returned a clean `Pips-Mac-mini` — the old claim that it yields `Something.local` was wrong.
- **Denying the macOS Local Network permission did not stop discovery** on 15.7.4 — the daemon kept advertising *and* kept seeing peers with the permission explicitly off. The README's claim that discovery requires it is unverified. One machine, no reboot between toggling and testing, so this is an observation rather than proven causation.
- macOS release binaries need an executable bit and a Gatekeeper story (codesign/notarize, or a documented `xattr` workaround). CI does not publish.
- Discovery is verified between two daemons on one Windows machine, **not yet between two real machines**, and not at all on macOS by a human.
- The mDNS library is a community fork of a project abandoned in 2019, and its `Common.Logging` dependency required a targeted trim suppression (`docs/DECISIONS.md` #16).
