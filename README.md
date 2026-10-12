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

**Early development, ready to try between two machines.** Devices discover each other on the
local network, pair once by comparing a six-digit code, and then send files to each other.
Discovery is verified between Windows and macOS; pairing and transfer between two Macs, not yet
with Windows at either end (`docs/Roadmap.md` M2/M3). Clipboard, screenshots and live progress are
not built.

```bash
lightdrop daemon                     # run the daemon (on both machines)
lightdrop health                     # ask it who it is
lightdrop peers                      # list nearby devices
lightdrop pair "Mac Mini"            # on both machines within a minute; compare the codes
lightdrop peers --trusted            # list paired devices
lightdrop send report.pdf "Mac Mini" # lands in the other machine's Downloads folder
lightdrop unpair "Mac Mini"
lightdrop autostart on               # macOS: run the daemon at login, no terminal needed
```

Discovered peers are **nearby strangers**: presence, not trust. Nothing is sent to them and nothing
is stored about them until both users pair. Received files go to `downloadFolder` in `config.json`,
else `~/Downloads`, and never overwrite: a second `report.pdf` arrives as `report (1).pdf`.

On first run, macOS asks for Local Network permission and Windows may show a Defender Firewall
prompt. Allow both. (On macOS 15.7.4 discovery kept working even with the permission denied, so
the requirement is not as absolute as it appears — but do not rely on that.) Networks that block
multicast — many corporate
and guest networks — prevent it entirely.

## Try it on two machines

1. **Get the binary onto both** — publish it (below) or `dotnet run` from a clone on each. On
   macOS, `chmod +x lightdrop`; if Gatekeeper refuses a copied binary,
   `xattr -d com.apple.quarantine lightdrop`.
2. **Run the daemon on both** and leave it running: `lightdrop daemon`. Allow the macOS Local
   Network prompt. Both machines need to be on the same network, one that allows multicast.
3. **Check they see each other**, from a second terminal on each: `lightdrop peers`. This can take
   a few seconds; if it stays empty, see *When multicast is blocked* in `docs/Protocol.md`.
4. **Pair**, on both, within a minute of each other: `lightdrop pair "<the other's name>"`. Both
   print a six-digit code. Answer `y` only if the two codes are the same.
   **Windows** shows a Defender Firewall prompt here, for the peer port: allow it on private
   networks or the other machine cannot connect.
5. **Send:** `lightdrop send ./photo.jpg "<the other's name>"`. The file lands in the receiver's
   `~/Downloads` (or `downloadFolder` from its `config.json`), never overwriting:
   a second `photo.jpg` arrives as `photo (1).jpg`.

`lightdrop peers --trusted` lists paired devices; `lightdrop unpair "<name>"` forgets one on this
side only. The daemon's terminal logs every pairing step and every file received, with the peer's
name — the first place to look when something fails.

Ports: the CLI talks to `127.0.0.1:5533`; paired machines connect to each other on **5534/tcp**.
If that is taken or blocked, set `LIGHTDROP_PEER_PORT` on that machine before starting the daemon
— the other side learns the new port from discovery.

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
| `LIGHTDROP_PEER_PORT` | listen port + 1 (`5534`) | The port paired devices connect to. |
| `LIGHTDROP_DATA_DIR` | `%APPDATA%\LightDrop` (Windows) / `~/Library/Application Support/LightDrop` (macOS) | Where `config.json` and `state.json` live. |

The HTTP endpoint the CLI talks to binds to loopback on purpose, and stays there. The only socket
on the LAN is a separate **peer listener** on `0.0.0.0:<peer port>`, mutual TLS, which exists only
once a device is paired or while `lightdrop pair` is running, and admits only pinned peers (anyone
during an open pairing window, where every route checks the caller's key for itself). A fresh
install with nothing paired has no LAN socket at all.

**Windows:** the first `lightdrop pair` shows a Defender Firewall prompt for the peer listener.
Allow it on private networks, or the other device cannot connect. If the peer port falls inside a
range Windows reserves for Hyper-V (common with WSL2 or Docker Desktop), pairing reports that it
could not listen; pick another port with `LIGHTDROP_PEER_PORT` on that machine
(`netsh interface ipv4 show excludedportrange protocol=tcp` lists the reserved ranges).

### Two daemons on one machine

Discovery can be exercised without a second computer, but each daemon needs its **own port and its
own data directory**. Sharing `state.json` would give both the same device id, and each would
dismiss the other's announcements as its own. Space the ports two apart: each daemon also takes
port + 1 for its peer listener. To send between them, give each a `config.json` with its own
`downloadFolder`, or both will receive into `~/Downloads`.

```bash
# terminal 1
LIGHTDROP_PORT=5533 LIGHTDROP_DATA_DIR=/tmp/ld-a dotnet run --project src/LightDrop.Cli -- daemon

# terminal 2
LIGHTDROP_PORT=5535 LIGHTDROP_DATA_DIR=/tmp/ld-b dotnet run --project src/LightDrop.Cli -- daemon

# terminal 3
LIGHTDROP_PORT=5533 dotnet run --project src/LightDrop.Cli -- peers
```

On PowerShell, set the variables first — the inline `VAR=value cmd` form is Bash only:

```powershell
$env:LIGHTDROP_PORT = "5535"
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
(about 20 MB) or `lightdrop` (about 20 MB). Copy it anywhere and run it — that is the whole install:

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

### Starting at login (macOS)

```bash
lightdrop autostart on     # run 'lightdrop daemon' at every login, and now
lightdrop autostart        # is it on, which binary, is it running
lightdrop autostart off    # stop it and remove it
```

`on` registers the binary you ran it from as a per-user LaunchAgent
(`~/Library/LaunchAgents/com.lightdrop.daemon.plist`), so publish to a folder that stays put first.
Output goes to `~/Library/Logs/lightdrop.log`. launchd restarts the daemon if it crashes, not after
a clean stop; if a daemon is already running in a terminal, the background one takes over within
about ten seconds of that one stopping. macOS shows a "Background Items Added" notice, and the
item appears under System Settings > General > Login Items.

Started by launchd, the daemon needs **its own** Local Network permission; from a terminal it
borrowed Terminal's. If `lightdrop peers` stays empty after `autostart on`, check that `lightdrop`
is allowed under System Settings > Privacy & Security > Local Network, then restart it with
`lightdrop autostart on`: a daemon that started before the permission existed stays deaf until it
restarts.

Not on Windows yet: `lightdrop.exe` is a console program, so the usual logon hooks would open a
console window at every login (`docs/DECISIONS.md` #33).

## Goals

- Automatic device discovery
- Local network only
- Encrypted peer-to-peer connections
- Zero configuration
- Cross-platform (Windows & macOS)
- Single portable executable

## Done

- Peer discovery
- Secure pairing
- File transfer (one file at a time, from the CLI)

## Planned

- Progress, and pairing from the page
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
