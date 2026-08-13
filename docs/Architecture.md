# LightDrop Architecture

## Shape

```
LightDrop.Cli ──▶ LightDrop.Daemon ──▶ LightDrop.Core
      └──────────────────────────────▶ LightDrop.Core
```

One executable. Three assemblies. The dependency direction is the load-bearing part: nothing points back into the CLI, and nothing in Core points outward.

## Responsibilities

### LightDrop.Cli — the only executable

Publishes as `lightdrop` (`AssemblyName`), matching the command users type. `lightdrop daemon` hosts the daemon **in-process**; there is deliberately no second binary to install, supervise, or keep in sync.

Owns command-line parsing, verb dispatch, and local user interaction. Verbs implement `ICliCommand` and are resolved from DI by name — a dictionary lookup, no parsing library. Revisit when a verb needs real option parsing; `send` is the likely trigger.

### LightDrop.Daemon — a class library, not an application

Consumes ASP.NET Core via `<FrameworkReference Include="Microsoft.AspNetCore.App" />` and exposes `LightDropDaemon.Create(...)` / `RunAsync(...)` instead of a `Main`. Giving it `OutputType=Exe` would break the single-executable guarantee.

Owns:
- Kestrel hosting and endpoint mapping
- **all file I/O** — `JsonConfigStore`, `JsonStateStore`, `LightDropDirectories`
- platform-specific path resolution
- networking implementations (as they arrive)

### LightDrop.Core — platform independent

No ASP.NET Core dependency. No filesystem access. No OS-specific APIs.

Owns:
- **wire contracts** (`HealthResponse`) — anything crossing a process or network boundary, so producer and consumer cannot drift
- **ports** — `IConfigStore`, `IStateStore`
- **logic over those ports** — `DeviceIdentityProvider`, `HealthService`
- device identity rules, protocol/version constants

The one concession: `DeviceIdentityProvider` reads `Environment.MachineName` for the default device name. That is a deterministic BCL read, not I/O, and tests assert against it directly.

## Why logic sits in Core and I/O sits in Daemon

The obvious split — an `IDeviceIdentityStore` port with a `JsonDeviceIdentityStore` adapter — puts the get-or-create rules (generate once, reuse forever, resolve the name) inside the Daemon project. Splitting one notch differently keeps those rules in Core behind two dumb ports, so they are unit-testable with in-memory fakes and no temp directories.

There is intentionally **no `LightDrop.Infrastructure` project**. Extract one when a second host needs the adapters. The CLI does not — it talks to the daemon over HTTP.

## Interfaces are for ports, not habit

`IConfigStore` and `IStateStore` exist because infrastructure implements them and tests substitute them. `ICliCommand` exists because DI resolves it as a collection.

`DeviceIdentityProvider` and `HealthService` are **concrete classes**. Each had exactly one implementation and no test double; the interfaces bought nothing. Adding an interface because "services get interfaces" is the pattern to avoid — the next contributor copies it into an in-memory peer registry that has no boundary to abstract.

## Discovery

Split along the same line as everything else — logic in Core, I/O in Daemon.

**Core** owns `PeerAnnouncement` (what a peer claims, after sanitizing), `UntrustedText` (the
ingestion chokepoint), and `PeerRegistry` (add, refresh, expire, deduplicate, self-filter, bound).
All of it is testable with a fake clock and no network.

**Daemon** owns `IPeerDiscoveryTransport` and its mDNS implementation, plus the network-interface
filter that excludes Hyper-V, WSL, Docker, VPN, `awdl0` and `utun` adapters.

`IPeerDiscoveryTransport` is a genuine port, not habit. Multicast cannot be exercised in tests: CI
runners cannot route it, and macOS drops it **silently** without the Local Network permission, so a
test would hang rather than fail. Every daemon test passes a no-op or fake transport; without the
seam, the whole suite would start opening multicast sockets.

`MdnsPeerDiscoveryTransport` is deliberately **not** unit tested — that would mean mocking the
library's internals or using real multicast. It is verified by hand; see `docs/Roadmap.md`.

**The registry has no route to `IStateStore`, by design.** A discovered peer is a stranger, and
pairing must cross that boundary explicitly and under review rather than inherit a path that
already exists. Adding a state-store reference to anything under `Discovery` should fail review.

## Storage

Both files live in `%APPDATA%\LightDrop` (Windows) or `~/.config/LightDrop` (macOS).

| File | Owner | LightDrop may | Contains |
|---|---|---|---|
| `config.json` | the user, by hand | **read only** | `deviceName`, `downloadFolder` |
| `state.json` | the application | read and write | `deviceId`, `trustedPeers` |

Merging them would mean a pairing write clobbers hand-edited settings. If the app writes it, it is state.

Failure behavior is deliberately asymmetric:

- **`config.json` unreadable** → warn, use defaults. A typo must not take down a background utility.
- **`state.json` corrupt** → throw. Starting fresh would mint a new `deviceId` and silently invalidate every pairing on every other machine.

Writes go temp-file-then-rename (atomic on both NTFS and APFS), and `state.json` is created `0600` on Unix because it will hold peer key material.

## Configuration

No `ConfigurationBuilder`, no `appsettings.json` — shipping a config file next to a portable binary contradicts both zero-config and single-executable, and `IConfiguration` has no clean write-back story for state.

Defaults live in code. `IOptions<T>` is fed by hand. `LIGHTDROP_HOST` / `LIGHTDROP_PORT` exist as a development escape hatch, mainly so two daemons can run on one machine.

## Trimming and AOT

`IsAotCompatible` is set on all three projects, turning trim/AOT violations into build errors. Two things keep it passing:

- **`System.Text.Json` source generation** — every serializable type must be listed on `LightDropJsonContext`. A missing entry fails at runtime, not at build.
- **`EnableRequestDelegateGenerator`** on the daemon — without it, `MapGet` reflects over the handler and trips IL2026/IL3050.

Use `[LoggerMessage]` source-generated logging for the same reason. The payoff is real: 99 MB self-contained versus 18 MB trimmed, with zero trim warnings.

## Testing

- `LightDrop.Core.Tests` — logic, with in-memory port fakes. No filesystem.
- `LightDrop.Daemon.Tests` — real file I/O in temp directories, and real HTTP against a real Kestrel binding on an OS-assigned port.

`WebApplicationFactory` is not used: it reflects over an entry point the Daemon library does not have, and its in-memory transport would not exercise the Kestrel binding. Every daemon test passes an explicit data directory — omitting one would make the suite read and write the real user profile.
