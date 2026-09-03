# Companion UI design

Status: **design agreed, not implemented.** Decided 2026-09-03, after M2 step 1
landed in `03811ab`.

LightDrop has a CLI and nothing to look at. This adds one small page, served by
the daemon that already runs, and two ways to launch it by double-clicking.

Deliberately minimal. This is a tool with one user; the page needs to be useful,
not complete.

## The four decisions

### 1. A companion to the CLI, not a replacement

The CLI stays the source of truth. The page covers what a terminal is bad at:
seeing who is nearby, running the pairing ceremony, knowing the daemon is alive.

**Rejected:** making the UI primary — every capability would need two front ends
and a real command API beneath them. **Rejected:** Explorer/Finder integration,
which is the right end state eventually but is the most OS-specific work and the
one option that cannot avoid an installer.

### 2. A web page served by the existing Kestrel

One `index.html` with inline CSS and JS, embedded in the binary and served by a
single endpoint. No `wwwroot`, no static-file middleware, no npm, no build step.

Kestrel already runs and already serves JSON on loopback. This adds no
dependency, no binary growth and no trim risk, and both platforms work the day it
is written — which is what makes "macOS later" free rather than a second project.

**Rejected:** Avalonia — correct if a polished native app is the product, but a
large dependency and a much larger binary bought against value that does not
exist until file transfer does (M3). **Rejected:** a Photino webview shell, worth
reconsidering at M3; it ships native libraries per RID and complicates the
single-file publish that currently works. Either could wrap this same HTML later,
so starting in the browser forecloses nothing.

### 3. `lightdrop ui` is `lightdrop daemon` plus a browser tab

Host the daemon in-process, open the default browser, stay in the foreground
until Ctrl+C. One branch: if the bind fails because the port is in use, assume it
is our own daemon, open the browser anyway, and exit 0.

That branch exists for the double-click case, where a bind failure would
otherwise mean nothing visible happens at all.

**Rejected:** probing `/health` and attaching to a running daemon — a second mode,
and probe-then-bind races when two invocations both find nothing. **Rejected:**
detecting a foreign process on the port; the cost of not doing so is a tab that
fails to load rather than a named error, which is fine for one user on a fixed
port. **Rejected:** a `--ui` flag on `daemon`, because the CLI dispatches verbs
through a dictionary and has no option parsing yet.

### 4. No tray icon

A Windows shortcut and a minimal macOS `.app` bundle instead.

A tray means two separate implementations. On Windows, `NotifyIcon` brings a
Windows-only target framework and sits badly with trimming, so doing it properly
means `Shell_NotifyIcon` through P/Invoke — a hidden window, a `WndProc`, a
message pump. On macOS it means `NSStatusItem`, so AppKit interop and a run loop
that wants the main thread. There is no small path to the second one.

All of it buys "the daemon is running", which the page says as it loads. A tray
earns its place at M3 alongside notifications — and a webview or Avalonia shell
would supply one for free by then, so hand-writing the Win32 version now is
likely throwaway work.

## Scope

**Phase A** — `lightdrop ui`; one page showing daemon status and discovered
peers; an origin check on non-GET requests; the two launchers.

Phase A adds **no new API**. The page polls `/health` and `/api/peers`, which
already exist. A trusted-peers list is not included because nothing can be
trusted until pairing works, so the list would always be empty.

**Phase B**, once M2 step 2 lands — the pairing ceremony in the page, the pairing
session endpoints beneath it, and `GET /api/trusted` to show the result.

**Out:** unpairing from the page (CLI only — the M2 design makes replacing a pin
deliberately explicit, and a button undoes that), tray icon, notifications,
drag-and-drop, sending files, any LAN-reachable HTTP, any change to Kestrel's
loopback binding.

**Done when:** `lightdrop ui` opens a page listing the same peers `lightdrop
peers` reports, the page says so rather than going stale when the daemon stops,
a cross-origin POST is rejected, and double-clicking the macOS bundle launches it.

## Design

### Where the pieces live

- **Core** — untouched.
- **Daemon** — an endpoint serving the page, the origin check, and the pairing
  session in phase B. All I/O stays here.
- **Cli** — `UiCommand`, following the existing `ICliCommand` shape.
- **packaging/** — the launcher scripts, not part of any project.

Every existing invariant holds: Kestrel stays loopback-bound, nothing under
`Discovery` reaches `IStateStore`, and no LAN-reachable HTTP is added.

### The page

Daemon status, and the discovered peers list, polled every two seconds. When
polling fails it says the daemon is unreachable rather than showing stale data.
No WebSocket — that is M4, and polling loopback costs nothing.

Phase B adds the pairing ceremony: a Pair button per peer, the six digits, and a
confirm.

### Origin checking

Every non-GET request is rejected unless its `Origin` — falling back to `Host` —
is the loopback address the daemon serves. Middleware ahead of routing, so it
covers requests no route serves. The CLI is unaffected: it sends no `Origin`, and
its `Host` is the loopback address already.

**This is the one thing not being cut for simplicity.** Loopback binding stops
the LAN but not the browser: without the check, any page the user happens to have
open could POST to the pairing endpoint. It is about twenty lines, and it ships in
phase A — before any endpoint needs it — precisely so no later endpoint has to
remember.

### The pairing session lives in the daemon

This answers the question the M2 design left open. The CLI owns the terminal, the
daemon owns the session; with a UI, the daemon is the only answer that does not
build the ceremony twice. `lightdrop pair` and the page drive the same session.

### Launchers

- **Windows** — a documented PowerShell one-liner creating a `.lnk` to
  `lightdrop ui`, pinnable to the taskbar. No product code.
- **macOS** — a script emitting `LightDrop.app`: `Info.plist` plus a shell script
  that `exec`s `lightdrop ui`. Double-clickable, present in the Dock.

Packaging artifacts, not application code. Neither needs an installer.

### Failure behaviour

| Case | Result |
|---|---|
| Port in use | Assume our daemon; open the browser; exit 0 |
| Port held by something else | Tab fails to load. Accepted cost of the row above |
| Browser will not open | Print the URL and keep running. Never fatal |
| Daemon stops while the page is open | The page says it is unreachable |

### Testing

- **Daemon** — `GET /` returns the page; a POST carrying a foreign `Origin` is
  rejected where the same request from loopback is not, asserted against any path
  since the check precedes routing. Every test passes a no-op discovery
  transport.
- **Cli** — the bind-or-open decision is separated from `Process.Start`, so the
  decision is testable and the browser launch is not.
- **Manual** — the launchers on both platforms. Double-clicking is not testable
  in CI.

### Documentation to update on implementation

`README` gains `lightdrop ui` and the launchers. `DECISIONS.md` records the
web-page choice, the deferred tray and the origin check. `Architecture.md` gains
the UI as a daemon client.

## Sequencing

1. **Phase A** — `UiCommand`, the page, the origin check, the launchers. Runs
   against today's daemon.
2. **M2 step 2** — TLS, the ephemeral listener, the pairing session.
3. **Phase B** — the pairing ceremony in the page and `lightdrop pair`, on the
   same session.

Phase A first because it runs against what already exists; the pairing UI cannot
be built before the session it drives.

## Open question for implementation

Whether `lightdrop ui` should keep running after the browser tab closes. The
design says yes, because the daemon is the point — but a user who expects closing
the window to stop the app may disagree.
