# Companion UI design

Status: **design agreed, not implemented.** Decided 2026-09-03, after M2 step 1
(`PairingCode`, `TrustedPeer.PublicKey`, `PairingService`) landed in `03811ab`.

LightDrop has a CLI and no visible surface. This adds one: a local web page served
by the daemon that already runs, plus two launchers so it can be started by
double-clicking rather than by typing.

## The four decisions

### 1. The UI is a companion, not the interface

The CLI stays the source of truth. The UI covers what a terminal is bad at —
seeing who is nearby, running the pairing ceremony, knowing the daemon is alive —
and nothing else.

**Rejected:** making the UI primary. Every capability would then need two front
ends and a real command API beneath them, for a tool with one user. **Rejected:**
setup-only, which builds something used twice per machine and then never again.
**Rejected:** Explorer/Finder shell integration — the nicest end state and the
right one eventually, but the most OS-specific work and the option that cannot
avoid an installer.

### 2. It is a web page served by the existing Kestrel

A single `index.html` with inline CSS and JS, compiled in as an embedded resource
and served by one endpoint. No `wwwroot`, no static-file middleware, no npm, no
build step.

Kestrel is already running and already serving JSON on loopback. This adds no
dependency, no binary growth, and no trim risk — the properties `CLAUDE.md`
protects survive untouched. Windows and macOS both work the day it is written,
which is what makes "macOS later" free rather than a second project.

**Rejected:** Avalonia. The correct answer if a polished native app is the
product; here it is a large dependency and a much larger binary bought against
value that does not exist until file transfer does (M3). **Rejected:** a Photino
webview shell — a real window for the same HTML, worth reconsidering at M3, but
it ships native libraries per RID and complicates the single-file publish that
currently works. Starting with the browser keeps that option open at no cost: the
page is the same either way.

### 3. `lightdrop ui` is `lightdrop daemon` plus a browser tab

One code path. Host the daemon in-process, open the default browser at the
loopback address, stay in the foreground until Ctrl+C. If the bind fails because
the port is in use, assume it is our own daemon, open the browser anyway, and
exit 0.

That single branch exists for the double-click case: from a shortcut or an
`.app` bundle, a bind failure would otherwise mean nothing visible happens at
all.

**Rejected:** probing `/health` first and attaching to a running daemon. It adds
a second mode, and probe-then-bind races when two invocations both find nothing.
**Rejected:** distinguishing a foreign process on port 5533. The cost of not
doing so is a browser tab that fails to load instead of a named error — fine for
one user on a fixed port.

**Rejected:** a `--ui` flag on `daemon`. The CLI dispatches verbs through a
dictionary and deliberately has no option parsing; `ICliCommand` names `send` as
the trigger for adding it, not this.

### 4. No tray icon in this phase

Launchers instead: a Windows shortcut and a minimal macOS `.app` bundle.

A tray icon means two separate implementations. On Windows, `NotifyIcon` brings a
Windows-only target framework and sits badly with trimming, so doing it properly
means `Shell_NotifyIcon` through P/Invoke — a hidden window, a `WndProc`, a
message pump, coexisting with the ASP.NET host's lifetime. On macOS it means
`NSStatusItem`, so AppKit interop and an `NSApplication` run loop that wants the
main thread. There is no small path to the second one.

All of that buys "the daemon is running", which the page states the moment it
loads. A tray earns its place at M3, when an arriving file needs a notification —
and both Photino and Avalonia supply a cross-platform tray and native
notifications for free, so hand-writing the Win32 version now is likely throwaway
work.

## Scope

**In:** `lightdrop ui`; the embedded page showing daemon status, discovered peers
and trusted peers; `GET /api/trusted`; an origin check on non-GET requests; the
two launchers.

**In, after M2 step 2 lands:** the pairing ceremony in the page, and the pairing
session endpoints beneath it.

**Out:** tray icon, native notifications, drag-and-drop, sending files, any
LAN-reachable HTTP, any change to Kestrel's loopback binding.

**Done when:** `lightdrop ui` opens a page listing the same peers `lightdrop
peers` reports, the page survives the daemon going away without going silently
stale, a cross-origin POST is rejected, and the macOS bundle launches it by
double-click.

## Design

### Where the pieces live

- **Core** — untouched. `PairingService` and `PairingCode` already landed.
- **Daemon** — a `UiEndpoints` group serving the page and `GET /api/trusted`;
  the origin check; pairing session endpoints in phase B. All I/O stays here.
- **Cli** — `UiCommand`, following the existing `ICliCommand` shape alongside
  `daemon`, `health` and `peers`.
- **packaging/** — the launcher scripts. Not part of any project.

Every existing invariant holds: Kestrel stays loopback-bound, nothing under
`Discovery` reaches `IStateStore`, and no LAN-reachable HTTP is added.

### HTTP surface

Phase A, against the daemon as it exists today:

| Endpoint | Status |
|---|---|
| `GET /` | new — the embedded page |
| `GET /health` | exists |
| `GET /api/peers` | exists |
| `GET /api/trusted` | new — wraps `PairingService.ListAsync` |

Phase B, once the pairing session exists: `POST /api/pairing/start`,
`GET /api/pairing/session`, `POST /api/pairing/confirm`,
`POST /api/pairing/cancel`, `DELETE /api/trusted/{deviceId}`.

The page polls `/api/peers` and `/api/trusted` every two seconds. No WebSocket —
that is M4, and polling a loopback endpoint costs nothing.

### The pairing session lives in the daemon

This answers the open question the M2 design left: the CLI owns the terminal, the
daemon owns the session. With a UI, the daemon is the only answer that does not
mean building the ceremony twice. `lightdrop pair` and the page both drive the
same session over the same loopback endpoints.

### Origin checking

Every non-GET request is rejected unless its `Origin` (falling back to `Host`)
is the loopback address the daemon is serving.

Loopback binding stops the LAN; it does not stop the browser. Without this check,
any page the user happens to have open could `POST /api/pairing/confirm` and pair
the machine with an attacker already on the network. It is roughly twenty lines
now and a retrofit later, and it is the one part of this design that is not
negotiable.

It runs as middleware ahead of routing, so it covers every non-GET request
including ones no route serves. The CLI is unaffected: it sends no `Origin`, and
its `Host` is the loopback address the daemon is bound to.

Phase A has no non-GET endpoints, so the check ships before anything needs it —
deliberately, so no later endpoint has to remember.

### Launchers

- **Windows** — a documented PowerShell one-liner creating a `.lnk` to
  `lightdrop ui`, pinnable to the taskbar. No product code.
- **macOS** — a build script emitting `LightDrop.app`: `Info.plist` plus a shell
  script that `exec`s `lightdrop ui`. Double-clickable from Finder, present in the
  Dock. `LSUIElement` is left off so it behaves like a normal app.

Both are packaging artifacts, not application code, and neither requires an
installer.

### Failure behaviour

| Case | Result |
|---|---|
| Port in use | Assume our daemon; open the browser; exit 0 |
| Port held by something else | Browser tab fails to load. Accepted cost of the row above |
| Browser will not open | Print the URL and keep running. Never fatal |
| Daemon dies while the page is open | Polling fails; the page shows a disconnected banner rather than stale data |
| `config.json` unreadable | Unchanged — warn and use defaults |

### Testing

- **Daemon** — real HTTP as usual: `GET /` returns the page; `GET /api/trusted`
  reflects pinned peers; a POST carrying a foreign `Origin` is rejected where the
  same request from loopback is not — asserted against any path, since the check
  is middleware ahead of routing and phase A serves no POST route. Every test
  passes a no-op discovery transport.
- **Cli** — the bind-or-open decision is separated from `Process.Start` so the
  decision is testable and the browser launch is not.
- **Manual** — the launchers, on both platforms. Double-clicking cannot be
  tested in CI.

### Documentation to update on implementation

`README` gains `lightdrop ui` and the launchers. `Architecture.md` gains the UI
as a daemon client. `DECISIONS.md` records the web-page choice, the deferred
tray, and the origin check. The roadmap gains the UI as work parallel to M2.

## Sequencing

1. **Phase A** — `UiCommand`, the page, `GET /api/trusted`, the origin check,
   the launchers. Works against today's daemon.
2. **M2 step 2** — TLS, the ephemeral listener, the pairing session.
3. **Phase B** — the pairing ceremony in the page and `lightdrop pair`, both on
   the same session.

Phase A first because it runs against what exists and puts something on screen
immediately; the pairing UI cannot be built before the session it drives.

## Open questions for implementation

- Whether the page should offer to unpair. `DELETE /api/trusted/{deviceId}` is
  listed in phase B, but the M2 design deliberately makes replacing a pin
  explicit, and a button makes it one click. Possibly CLI-only.
- Whether `lightdrop ui` should keep running after the browser tab is closed. The
  design says yes, because the daemon is the point; a user who expects closing
  the window to stop the app may disagree.
- Whether the macOS bundle should eventually set `LSUIElement` and become a
  background app. That decision belongs with the tray, at M3.
