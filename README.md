# LightDrop

> Zero-config local sharing between your own devices.

LightDrop is a lightweight developer companion that lets you instantly transfer files, screenshots and clipboard content between your computers over your local network.

No cloud.

No accounts.

No installation.

Just run the binary and your devices find each other.

## Why?

Moving a screenshot from your desktop to your work laptop shouldn't require:

- email
- OneDrive
- Slack
- USB drives
- SCP
- shared folders

LightDrop makes nearby devices feel like they're part of the same workstation.

## Status

**Early development.** Devices discover each other on the local network. Nothing is transferred
yet, and no peer is trusted. See `docs/Roadmap.md` for what lands when.

```bash
lightdrop daemon   # run the daemon
lightdrop health   # ask it who it is
lightdrop peers    # list nearby devices
```

Discovered peers are **nearby strangers**: presence, not trust. Nothing is sent to them and nothing
is stored about them. Pairing comes next.

On first run, macOS asks for Local Network permission and Windows may show a Defender Firewall
prompt. Allow both. (On macOS 15.7.4 discovery kept working even with the permission denied, so
the requirement is not as absolute as it appears — but do not rely on that.) Networks that block
multicast — many corporate
and guest networks — prevent it entirely.

## Build and run from source

**Prerequisites:** the [.NET 10 SDK](https://dotnet.microsoft.com/download). `global.json` pins
`10.0.111` with `rollForward: latestFeature`, so any 10.0.1xx SDK works. Nothing else is needed —
no database, no services, no configuration file.

```bash
git clone https://github.com/gyurisc/lightdrop.git
cd lightdrop

dotnet build LightDrop.sln   # warnings are errors; a clean build reports 0 warnings
dotnet test LightDrop.sln    # Core tests are pure logic, Daemon tests do real file and HTTP I/O
```

Run the CLI without publishing. Everything after `--` is passed to LightDrop rather than to
`dotnet`:

```bash
dotnet run --project src/LightDrop.Cli -- --help    # commands and environment variables
dotnet run --project src/LightDrop.Cli -- daemon    # run the daemon in the foreground; Ctrl+C stops it
```

The daemon holds the terminal, so query it from a **second** terminal:

```bash
dotnet run --project src/LightDrop.Cli -- health    # version, identity, capabilities
dotnet run --project src/LightDrop.Cli -- peers     # nearby devices seen over mDNS
```

`health` reports `No LightDrop daemon is reachable` when nothing is listening — that is the normal
answer, not a crash.

### Environment variables

All three are development escape hatches. LightDrop needs none of them to run.

| Variable | Default | Purpose |
|---|---|---|
| `LIGHTDROP_HOST` | `127.0.0.1` | Kestrel listen address. Loopback by design — see below. |
| `LIGHTDROP_PORT` | `5533` | Kestrel listen port. |
| `LIGHTDROP_DATA_DIR` | `%APPDATA%\LightDrop` (Windows) / `~/Library/Application Support/LightDrop` (macOS) | Where `config.json` and `state.json` live. |

The HTTP endpoint binds to loopback on purpose: nothing is paired yet, so binding to the LAN would
expose an unauthenticated endpoint to every device on the network. Discovery does not need it —
peer metadata rides in mDNS TXT records.

### Two daemons on one machine

Discovery can be exercised without a second computer, but each daemon needs its **own port and its
own data directory**. Sharing `state.json` would give both the same device id, and each would
dismiss the other's announcements as its own.

```bash
# terminal 1
LIGHTDROP_PORT=5533 LIGHTDROP_DATA_DIR=/tmp/ld-a dotnet run --project src/LightDrop.Cli -- daemon

# terminal 2
LIGHTDROP_PORT=5534 LIGHTDROP_DATA_DIR=/tmp/ld-b dotnet run --project src/LightDrop.Cli -- daemon

# terminal 3
LIGHTDROP_PORT=5533 dotnet run --project src/LightDrop.Cli -- peers
```

On PowerShell, set the variables first — the inline `VAR=value cmd` form is Bash only:

```powershell
$env:LIGHTDROP_PORT = "5534"
$env:LIGHTDROP_DATA_DIR = "$env:TEMP\ld-b"
dotnet run --project src/LightDrop.Cli -- daemon
```

### Publish the single executable

`LightDrop.Cli` is the only executable in the solution; the daemon is a library it hosts
in-process. One command produces one self-contained, trimmed file with no .NET runtime
prerequisite:

```bash
# Windows
dotnet publish src/LightDrop.Cli -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:PublishTrimmed=true

# macOS (Apple Silicon)
dotnet publish src/LightDrop.Cli -c Release -r osx-arm64 --self-contained \
  -p:PublishSingleFile=true -p:PublishTrimmed=true
```

The binary lands in `src/LightDrop.Cli/bin/Release/net10.0/<rid>/publish/` as `lightdrop.exe`
(~19 MB on Windows) or `lightdrop`. Copy it anywhere and run it — that is the whole install:

```bash
./lightdrop daemon
```

macOS release binaries still need an executable bit and a Gatekeeper story; that is an open gap,
not a finished path.

## The page

`lightdrop ui` runs the daemon and opens a browser at <http://127.0.0.1:5533>, showing this
device's status and the machines it can see nearby. Ctrl+C stops it — closing the browser tab does
not, because the daemon has to keep running for this machine to stay visible to its peers.

If a daemon is already running it opens the page against that one and exits.

### Launching it without a terminal

**macOS** — build a double-clickable bundle:

```bash
./packaging/macos/make-app-bundle.sh path/to/lightdrop ~/Applications
```

**Windows** — add a Start Menu entry, then pin it to the taskbar:

```powershell
.\packaging\windows\create-shortcut.ps1 -Binary C:\tools\lightdrop.exe
```

Neither installs anything. They point at the executable wherever you already keep it.

## Goals

- Automatic device discovery
- Local network only
- Encrypted peer-to-peer connections
- Zero configuration
- Cross-platform (Windows & macOS)
- Single portable executable

## Planned

- Peer discovery
- Secure pairing
- File transfer
- Clipboard text
- Clipboard images
- Screenshots
- Explorer / Finder integration
- Claude Code image handoff
- Notifications
- Remote actions

## Philosophy

LightDrop is **not** another cloud storage service.

It is not Dropbox.

It is not Syncthing.

It is not a network drive.

It simply makes moving things between your own nearby computers effortless.

## Example

```bash
lightdrop peers

Desktop
MacBook Air
Work Laptop

lightdrop send screenshot.png "Work Laptop"
```

The file immediately appears on the destination machine.

## Design Principles

- Zero configuration
- Local-first
- Privacy by default
- Portable
- Small
- Developer-friendly

## Documentation

- Product Requirements Document: `docs/PRD.md`
- Architecture: `docs/Architecture.md`
- Protocol: `docs/Protocol.md`
- Roadmap: `docs/Roadmap.md`
- Decisions: `docs/DECISIONS.md`

## License

MIT
