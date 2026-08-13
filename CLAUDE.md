# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build LightDrop.sln            # warnings are errors; keep this at 0 warnings
dotnet test LightDrop.sln             # all tests
dotnet test --filter "FullyQualifiedName~CommandRegistryTests"          # one class
dotnet test --filter "FullyQualifiedName~CommandRegistryTests.RejectsDuplicateCommandNames"   # one test

dotnet run --project src/LightDrop.Cli -- daemon    # run the daemon in the foreground
dotnet run --project src/LightDrop.Cli -- health    # query a running daemon
```

Publish the single portable executable (`lightdrop.exe` / `lightdrop`):

```bash
dotnet publish src/LightDrop.Cli -c Release -r win-x64  --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true
dotnet publish src/LightDrop.Cli -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true
```

Trimming takes the Windows build from ~99 MB to ~18 MB and currently produces **zero trim warnings**. That is a property worth protecting — see "AOT and trimming" below.

## Architecture

Three projects plus tests. The dependency direction is the load-bearing part:

```
Cli ──▶ Daemon ──▶ Core
 └────────────────▶ Core
```

**`LightDrop.Cli` is the only executable.** `AssemblyName` is `lightdrop`, so the artifact matches the command in the README. `lightdrop daemon` hosts the daemon **in-process** — there is deliberately no second binary, because "single portable executable" is a product requirement, not a packaging detail.

**`LightDrop.Daemon` is a class library**, not a web app. It consumes ASP.NET Core through `<FrameworkReference Include="Microsoft.AspNetCore.App" />` and exposes `LightDropDaemon.Create(...)` / `RunAsync(...)` instead of a `Main`. Do not give it an `OutputType` of `Exe`.

**`LightDrop.Core` is platform independent and has no ASP.NET Core dependency.** It owns:

- the wire contracts (`HealthResponse`, the command envelope) — anything crossing a process or network boundary lives here so producer and consumer cannot drift
- the ports (`IConfigStore`, `IStateStore`) and the orchestration logic over them (`DeviceIdentityProvider`, `HealthService`)

**All file I/O lives in `LightDrop.Daemon/Infrastructure/`.** Core never touches `Environment.SpecialFolder`, paths, or the filesystem. This is why `DeviceIdentityProvider` — which holds the get-or-create logic — is unit-testable from `LightDrop.Core.Tests` with in-memory fakes and no temp directories.

There is no separate `LightDrop.Infrastructure` project on purpose. Extract one only when a second host needs the adapters; the CLI does not, because it talks to the daemon over HTTP.

## Two things named "command" — do not conflate them

- **`ICliCommand`** (`LightDrop.Cli`) — a verb the *user types*: `daemon`, `health`. Hand-rolled dispatch, resolved from DI by name. No parsing library; revisit when `send` needs real option parsing.
- **`ICommandHandler`** (`LightDrop.Core.Protocol`) — a command a *peer sends over the wire*: `file.send`, `clipboard.text`.

## The protocol is command-oriented, not endpoint-oriented

New features become **registered handlers, not new endpoints**. `POST /clipboard`, `POST /notification` and friends would each add client code and versioning surface; one command envelope gives one transport, one dispatch table, one auth path.

Two consequences to preserve:

- **Capabilities are derived from DI.** `CommandRegistry` projects `CommandName` from every registered `ICommandHandler` into the list `/health` advertises. Registering a handler is the *only* step needed to advertise it. Never hand-maintain a capability list — a hardcoded one drifts from reality the first time someone forgets to update it.
- **`/health` stays a plain HTTP GET.** It is the bootstrap probe: it must answer before any socket, pairing, or negotiation exists, and it keeps the daemon debuggable with `curl`. Do not route it through the command bus.

**The envelope carries metadata only.** File bytes must stream on a separate channel keyed by the envelope id — base64-in-JSON costs ~33% overhead and forces whole-payload buffering.

**Transports must stay swappable.** `ICommandDispatcher` and handlers know nothing about HTTP or WebSockets. A persistent WebSocket is the expected next transport (server-initiated push is the real reason: notifications and inbound clipboard cannot work well over polling). When it lands, no handler should need to change. Watch for head-of-line blocking — a multi-GB transfer must not stall clipboard and notifications on the same socket.

## config.json vs state.json — never merge these

Both live in `%APPDATA%\LightDrop` / `~/.config/LightDrop`, and the split is deliberate:

| File | Owner | LightDrop may | Contains |
|---|---|---|---|
| `config.json` | the user, by hand | **read only** | `deviceName`, `downloadFolder` |
| `state.json` | the application | read and write | `deviceId`, `trustedPeers` |

Putting `trustedPeers` in `config.json` would mean pairing rewrites a file the user edits by hand, clobbering their formatting and racing their edits. If a new setting is written by the app, it belongs in state.

The failure behavior is intentionally asymmetric:

- **`config.json` unreadable → warn and use defaults.** A typo should not take down a background utility.
- **`state.json` corrupt → throw.** Silently starting fresh would mint a new `deviceId` and invalidate every pairing on every other machine. `JsonStateStore` also writes via temp-file-then-rename so a crash mid-write cannot truncate it.

Device identity is generated once and must never change unless explicitly reset.

## Configuration deliberately avoids `ConfigurationBuilder`

LightDrop owns its config format. No `appsettings.json` — shipping one contradicts both zero-config and single-portable-executable. Defaults live in code; `IOptions<T>` is fed by hand rather than from the ASP.NET configuration pipeline, which has no clean write-back story for state.

Keep `config.json` tiny. The endpoint is intentionally *not* in it — `LIGHTDROP_HOST` / `LIGHTDROP_PORT` are a development escape hatch, mainly so two daemons can run on one machine while testing discovery and pairing.

## Kestrel binds to loopback by default

`127.0.0.1:5533`. This is a security decision, not an oversight: there is no pairing or authentication yet, so binding to the LAN would expose an unauthenticated endpoint to every device on the network. **LAN binding stays opt-in until pairing exists.**

## AOT and trimming

`IsAotCompatible` is set on all three projects, which turns trim/AOT violations into build errors. Two things keep it passing, and both must be maintained:

- **`System.Text.Json` source generation.** Every serializable type must be listed on `LightDropJsonContext`. A missing entry fails at *runtime*, not at build.
- **`EnableRequestDelegateGenerator`** on the daemon. Without it, `MapGet` reflects over the handler and trips IL2026/IL3050.

Use `[LoggerMessage]` source-generated logging rather than `logger.LogInformation(...)` for the same reason.

## Product constraints that veto designs

From `README.md` and `docs/PRD.md`:

- **No cloud, no accounts, no installation, no configuration.** Anything requiring a signup, relay server, or config file to work out of the box is out of scope.
- **Local network only**, peer-to-peer, encrypted.
- **Explicit non-goals**: not Dropbox, Syncthing, Google Drive, Remote Desktop, SSH, a sync service, or a backup solution. LightDrop moves a thing from A to B on demand — it does not reconcile folders, maintain cross-device state, or version anything. Flag drift toward sync semantics as scope creep.

## v1 milestone

Daemon starts → peers discover each other → secure pairing → send one file. Nothing else.

**Pairing is the only item that cannot be retrofitted** — the rest is mechanical. It needs its own design pass before implementation, and it is the reason the loopback default stands.

## Known gaps

- `docs/Architecture.md`, `docs/Protocol.md`, and `docs/Roadmap.md` are linked from `README.md` but do not exist.
- Tests cover Core only. `JsonConfigStore` / `JsonStateStore` (atomic write, corrupt-state behavior) and the `/health` endpoint have **no automated coverage** — that needs a `tests/LightDrop.Daemon.Tests` project using `WebApplicationFactory`.
