# Decisions

Tradeoffs worth remembering. Newest last.

---

### 1. The CLI is the only executable; the daemon is a library

Single portable executable is a product requirement. `LightDrop.Daemon` is a class library using `FrameworkReference` on `Microsoft.AspNetCore.App`, exposing `Create`/`RunAsync` instead of a `Main`. `lightdrop daemon` hosts it in-process.

**Cost:** the CLI transitively depends on the ASP.NET Core shared framework. Acceptable — it ships in the same binary either way.

**Rejected:** two executables with the CLI spawning the daemon. Breaks the requirement and adds process supervision.

---

### 2. Logic in Core, file I/O in Daemon — with the split one notch from the obvious place

The natural reading (`IDeviceIdentityStore` in Core, `JsonDeviceIdentityStore` in infrastructure) puts get-or-create identity rules inside Daemon, where the only specified test project cannot reach them. Instead Core owns two dumb ports (`IConfigStore`, `IStateStore`) plus the logic over them.

**Consequence:** identity rules are unit-testable with in-memory fakes; Core still never touches the filesystem.

---

### 3. No `LightDrop.Infrastructure` project

Two adapter classes do not justify a fourth assembly. Extract when a second host needs them; the CLI never will, since it speaks HTTP.

---

### 4. `config.json` and `state.json` are separate files

Different owners. The user hand-edits config; the app writes state during pairing. Merged, a pairing write clobbers hand edits and races the user's editor.

**Consequence:** `trustedPeers` is state, not config, despite reading like configuration.

---

### 5. Config failures warn; state failures throw

A typo in a hand-edited config must not take down a background utility. A corrupt state file must not silently mint a new `deviceId` — that would invalidate every pairing on every other machine without telling anyone.

---

### 6. No ASP.NET configuration pipeline

LightDrop owns its config format. `IConfiguration` has no clean write-back story, and pairing must persist. Shipping `appsettings.json` next to a portable binary contradicts zero-config.

**Consequence:** defaults live in code; `IOptions<T>` is populated by hand.

---

### 7. Kestrel binds to loopback by default

There is no pairing or authentication yet. Binding to the LAN would expose an unauthenticated endpoint to every device on the network, including untrusted ones.

**Revisit at:** M1 (discovery) or M2 (pairing), whichever forces it. See the open question in the roadmap.

**Still true for the loopback app (2026-10-09).** The LAN gets a separate, pinned-only peer
listener instead — see #26. Nothing on the loopback app is reachable from the network.

---

### 8. Hand-rolled CLI dispatch

Two verbs. `ICliCommand` resolved from DI by name is a dictionary lookup with no dependency.

**Rejected:** `System.CommandLine` (still beta, history of API churn), `Spectre.Console.Cli` (a dependency against the "small" principle — reconsider when `send` needs progress bars).

---

### 9. Built-in `ILogger` with the JSON console formatter

Structured output with zero packages. `[LoggerMessage]` source generation keeps it allocation-free and trim-safe.

**Rejected:** Serilog. Three packages and trimming friction for sinks nothing needs yet.

---

### 10. Source-generated JSON and request delegates

`IsAotCompatible` turns trim violations into build errors — it caught `MapGet` reflecting over its handler immediately. Fixing it properly (`EnableRequestDelegateGenerator`) rather than suppressing kept trimming viable.

**Payoff:** 99 MB self-contained → 18.3 MB trimmed, zero trim warnings.

**Constraint:** every serializable type must be listed on `LightDropJsonContext`. A missing entry fails at runtime.

---

### 11. The command-protocol scaffold was built, then deleted

An envelope, result, dispatcher, handler interface, and registry were written before any handler existed. The justification was that `/health` capabilities must be derived rather than hand-maintained.

That argument is sound when handlers exist and **vacuous while the set is permanently empty** — nothing can drift from nothing. Meanwhile the envelope shape had been validated by nothing, and the later requirement to stream large payloads rather than buffer them is likely to reshape it.

**Deleted** (~250 lines plus ~120 of tests). `HealthResponse.Capabilities` stays in the contract, returning `[]`. Rebuild at M3, sized to what `SendFile` actually needs.

**The direction is unchanged** — LightDrop remains command-oriented. Only the unused implementation is gone.

**Rebuilt at M3** (2026-10-09), sized to `SendFile` and nothing more — see #28.

---

### 12. `DeviceIdentityProvider` and `HealthService` are concrete classes

Each had one implementation and no test double. Interfaces bought nothing.

**Reason it mattered enough to change:** the next milestone adds a peer registry, and an unjustified `IPeerRegistry` would have been copied from the pattern sitting next to it.

---

### 13. `state.json` is created `0600` on Unix

The default umask produces `0644` — world-readable inside a `755` macOS home directory. The file holds device identity now and peer key material later.

**Note:** `FileStreamOptions.UnixCreateMode` throws on Windows rather than being ignored, so it is set behind an `OperatingSystem.IsWindows()` guard. Windows needs no equivalent; the user profile ACL already covers it.

---

### 14. Discovery is presence only; Kestrel stays on loopback

All peer metadata rides in mDNS TXT records, so M1 added **no LAN-reachable HTTP at all**.

This is not security theatre. mDNS is connectionless UDP: an observer can spoof TXT content but
cannot open a TCP session, cannot reach ASP.NET Core parsing code, and cannot hit an endpoint added
later by someone who forgot an authorization check. A LAN-bound Kestrel turns "a bug in a future
endpoint" into "the whole network can trigger it."

`GET /api/peers` is loopback-only, for the CLI. Pairing is where a peer-to-peer LAN endpoint gets
opened deliberately — that is when authenticated communication is actually needed.

**That happened on 2026-10-09** (#26): discovery itself still opens nothing, but a paired daemon,
or one in a pairing window, runs a mutual-TLS peer listener on the LAN.

---

### 15. mDNS library: `Makaretu.Dns.Multicast.New`

The .NET mDNS ecosystem is thin. `Tmds.MDns` and `Zeroconf` are **browse-only** and cannot
advertise, which disqualifies both. The only maintained option that does both is a community fork
of a project whose original author stopped releasing in 2019.

**Accepted with eyes open.** Do not hand-roll mDNS. If this fork dies, the realistic options are
forking it ourselves or using platform APIs (`dns-sd` on macOS), not switching packages.

---

### 16. Trim suppression for `Common.Logging`

The mDNS library depends transitively on `Common.Logging`, a .NET Standard 1.3 shim with no trim
annotations. It **broke the trimmed publish outright** (IL2104 → NETSDK1144).

`NoWarn IL2104` suppresses only the per-assembly rollup, so IL2026/IL3050 stay fatal for our own
code, and the affected assemblies are rooted so nothing inside them is removed. Verified at
runtime: the trimmed binary performs real discovery.

**Cost:** 18.3 MB → 18.8 MB. Cheap for the capability.

---

### 17. Advertise the real SRV port, not 0

DNS-SD requires a port. Port `0` is not the DNS-SD "no service" signal — `Target="."` is — and
zero breaks well-behaved clients for no security gain. Documented as not an authorization boundary
instead of encoded as one.

**Since 2026-10-09 it is the peer listener's port** (#26), so it finally means where the service is
reachable. Still not an authorization boundary: the TLS pin is.

---

### 18. DNS-SD instance name is the device id

Two machines can share a device name; identifiers cannot realistically collide. This avoids the
library's DNS-SD name-conflict probing entirely, and avoids making a human-readable name the
browsable label. Cost: `dns-sd -B` shows a GUID rather than a friendly name. Accepted.

---

### 19. The broadcast device id is a correlation token

The identifier is stable and announced on every network the machine joins, so a passive observer
can correlate the device across locations. Inherent to mDNS discovery, and the same tradeoff
AirDrop and network printers make — but stated rather than shipped silently.

Not a security defect: knowing the identifier does not help forge a pairing, which will pin to key
material. **Open question for pairing:** whether the device id stays an independent random GUID or
becomes a key fingerprint. Answer it deliberately on day one of that milestone rather than
defaulting to what already exists.

---

### 20. `.sln`, not `.slnx`

.NET 10 defaults to the newer `.slnx`. Classic format chosen for tooling compatibility. Worth revisiting — `.slnx` has no GUIDs and no merge conflicts.

---

### 21. macOS stores data in `~/Library/Application Support`, not `~/.config`

`LightDropDirectories` documented the opposite: that .NET applies the Linux XDG mapping to macOS,
and that following the cross-platform CLI convention made `~/.config` the right home. **Both halves
were wrong.** `Environment.SpecialFolder.ApplicationData` resolves to
`~/Library/Application Support` on macOS, confirmed on 15.7.4 by finding `state.json` there while a
hand-written `config.json` sat unread in `~/.config/LightDrop`.

This was user-facing: the docs told Mac users to hand-edit a file the app never reads, and
`config.json` failures are deliberately silent, so nothing reported the mistake.

**Behaviour kept, documentation fixed.** The platform-native location is where a Mac user's tooling
expects application data. The convention argument does not outweigh being native, and honouring it
would mean overriding the platform with extra code for no user benefit.

**Cost of finding it:** nothing in CI could have. Only running the daemon on a real Mac and looking
at the filesystem surfaced it — which is the argument for the manual checklist in the roadmap.

---

### 22. Discovery ingested other people's mDNS services

Browsing `_lightdrop._tcp` does not mean only that service type is delivered: the library raises a
discovery event for every instance it sees on the link, and `OnServiceInstanceDiscovered` never
checked the instance name. Any TXT record carrying an `id` key reached the parser — and `id` is a
common key. A Google Cast television was minted as a peer and listed with a derived name, `unknown`
platform and protocol `0`; an AirPlay instance was rejected only by luck, because its key is
`deviceid` rather than `id`.

Fixed in **two independent places**, deliberately:

- **The transport** now rejects any instance that is not a subdomain of `_lightdrop._tcp.local`.
  This is the correct primary fix — foreign services should not be looked at — but it sits in the
  one file that cannot be unit tested.
- **`PeerTxtRecord.TryParse`** now requires a `pv` key. Every LightDrop record carries one, so its
  absence means the record is not ours. This is pure Core logic with a regression test, and it
  holds whatever the transport delivers.

The duplication is the point: the untestable fix is the better one, so it is backed by a testable
one that fails loudly if the first is ever undone.

**Found by running two machines and reading the peer list**, not by a test. The same class of bug
would have shipped invisibly.

---

### 23. A discovered peer's address is observed, not claimed

M2 needs an address to dial, which is the first time discovery captures something that steers this
machine rather than merely informing it. Two decisions followed.

**Prefer the packet source over the A record.** An announcer can put any address in an A record,
including a third party's. The source address is what the network observed.

**Reject anything outside the local ranges, at ingestion.** Private, link-local and loopback IPv4
only; a public address is refused outright, so a hostile announcement cannot point pairing at an
arbitrary internet host. An announcement with no usable address is rejected rather than listed —
presence with nowhere to go could only mislead.

The check is a range check, not a route check. Verifying an address really belongs to one of this
machine's subnets needs interface enumeration, which Core is not allowed to do, and it would buy
little: the residual risk is a peer naming another machine on the same link, which can announce for
itself regardless.

---

### 24. The UI is a web page, and there is no tray icon

LightDrop needed something to look at. The cheap answer and the expensive one differ by a large
dependency and roughly triple the binary, so it was worth deciding rather than drifting.

**A page served by the Kestrel already running.** One `index.html` embedded in the assembly, served
by one endpoint, polling the health and peers endpoints that already exist. It adds no dependency,
no size and no trim risk, and both platforms work the day it is written — which is what makes
"macOS later" free rather than a second project. Avalonia is the right answer if a polished native
app is the product; here it buys polish against value that does not exist until file transfer does.
A webview shell would wrap this same HTML, so starting in the browser forecloses nothing.

**No tray icon.** It is two separate implementations. On Windows `NotifyIcon` brings a Windows-only
target framework and sits badly with trimming, so doing it properly means `Shell_NotifyIcon` through
P/Invoke, with a hidden window and a message pump. On macOS it means `NSStatusItem`, so AppKit
interop and a run loop that wants the main thread — there is no small path to the second one. All of
it buys "the daemon is running", which the page says as it loads. A shortcut and a minimal `.app`
bundle cover launching it, and a tray earns its place at M3 alongside notifications, when a webview
or Avalonia shell would supply one for free.

**An origin check shipped before anything needed it.** Loopback binding keeps other devices out and
does nothing about the browser on this machine: any page the user has open can post to 127.0.0.1.
The check validates `Host` on every request, reads included — DNS rebinding lets an
attacker-controlled name resolve to 127.0.0.1, and such a request looks local while carrying the
attacker's `Host`, which is the one thing a hostile page cannot forge to match. `Origin` is checked
in addition for writes, since `Host` alone would let any page on this machine post to the daemon.
Phase A has no write endpoint and its only read is `/health`, so the check guards little today —
that is the point. A check added alongside the first write endpoint, or the first endpoint that
returns something worth stealing, is a check the second one has to remember. (An earlier version of
this check exempted all reads from `Host` validation on the theory that a local page could already
learn anything a `GET` exposes; that reasoning did not hold once `Host` itself was the attack
surface, and it was rejected during final review before Phase A shipped.)

---

### 25. A Windows-only key round trip landed in Core, and stays there

`DeviceKeyProvider.MakeUsableByTls` calls `OperatingSystem.IsWindows()` and does a PKCS#12 export
and reload — platform-conditional behaviour, in `LightDrop.Core`, whose stated rule is platform
independence. That looks like the same mistake `JsonStateStore`'s `UnixCreateMode` guard was built
to avoid making twice.

**It stays, on purpose.** The `UnixCreateMode` precedent is about *where* platform behaviour is
allowed to live: I/O is the Daemon's job, so a guard over a file-creation flag belongs there. This
is not I/O — it is a transformation of key material the certificate is built from, and `DeviceKeyPair`
already lives in Core because pairing's trust decision (`PinnedCertificate`, `PairingService`) has
to reason about that key material without depending on ASP.NET Core or the filesystem. Moving the
round trip to the Daemon would either split key handling across two projects for one method, or
force `DeviceKeyProvider` itself into the Daemon — dragging `IStateStore`'s consumer, and the
get-or-create logic this file exists to keep testable, along with it.

**What actually justifies staying in Core**: `OperatingSystem.IsWindows()` compiles and runs
correctly on every target — it is not a Windows-only API surface, just a Windows-only code path —
and `X509CertificateLoader.LoadPkcs12` is available on all three platforms LightDrop ships with no
AOT or trim risk. Nothing here reaches the filesystem, the network, or an OS-specific handle; the
only difference from the constant-folded branches Core already tolerates is that this one is a
runtime check rather than a compile-time one.

**What would not be excused by this precedent**: a `P/Invoke` call, a `RuntimeInformation` switch
selecting between OS-specific libraries, or anything that touches a file, socket, or handle.
Those belong in the Daemon regardless of how small they look, on the same reasoning that put
`UnixCreateMode` there. Citing decision 25 to justify one of those in Core is the drift this entry
exists to head off.

---

### 26. One pinned-only LAN listener, separate from the loopback app

Reopens decision 2 of the 2026-08-16 pairing spec (LAN reachable only during a pairing window),
because receiving a file needs a listener when nobody is at the keyboard. Decided 2026-10-09.

A **separate `WebApplication`** on `0.0.0.0:<peerPort>`, mutual TLS, started only when a peer is
pinned or a window opens — a fresh install still has no LAN socket. The TLS handshake is the gate:
pinned SPKIs only, re-read per connection so an unpair applies to the next one. While a window is
open the handshake must admit any key (it is the only carrier of a new peer's key), so every route
also checks the caller's key itself. Separate rather than a second binding because the loopback
app's routes and its origin check then simply do not exist on the LAN side; `RequireHost` would
have scoped routes by the attacker-written `Host` header, not the real port.

**Cost:** a listening socket on the LAN whenever anything is paired, and a Windows firewall prompt.

---

### 27. The pairing code commits before it reveals

The 2026-08-16 spec derived the six digits from the two static keys alone, "stable for a given
device pair". That is broken against an active attacker on the LAN: having seen both honest keys
(the listener presents its certificate to anyone; the dialer presents its own to whoever answers
the dial), it generates key pairs until one toward each side yields equal digits. A birthday search
over 10^6 values — measured at about 1,700 keys and 0.4 s. Both screens then agree, both humans say
yes, and both pin the attacker.

**Fix, adopted rather than invented:** Bluetooth Secure Simple Pairing's numeric comparison. The
listener commits to a random nonce in its hello answer, the dialer reveals its nonce, the listener
reveals its own, and the dialer checks it against the commitment. The code hashes both keys and both
nonces, so neither side's randomness can be chosen after seeing the other's: one blind try at
one-in-a-million. Costs one extra round trip (`/pair/reveal`) and the "same digits every time"
property, which was the vulnerability.

**Not a protocol-version bump:** pairing had never shipped, so version 1 is defined as this.

---

### 28. The command scaffold, rebuilt for one handler — and who may call `/api/send`

The scaffold #11 deleted, rebuilt for `SendFile` alone: `POST /commands/{name}` on the peer
listener, metadata as base64url JSON in a `LightDrop-Metadata` header, the **body as the raw
stream**. No envelope type, no envelope id, no separate byte channel: one request carries both,
which is all a file needs, and nothing is ever base64-encoded or buffered. Handlers are a DI
collection looked up by name; capabilities are projected from that set in one place
(`CommandServices.AddCommandHandlers`), where a test adds a second handler to prove it.

The pinned-peer check is a filter on the **whole `/commands` group**, so a handler added later
cannot forget it, and the dispatcher refuses a request the filter did not vouch for — a missing
filter fails closed instead of writing a stranger's file first. When a second handler wants a reply
stream or a persistent session (M4), that is the moment to grow this, not before.

**Accepted risk:** `/api/send` lets any process running as this user send any file this user can
read to a device this user already paired. Browsers are kept out by the loopback origin check. The
alternative — a secret the CLI presents — would live in a file that same process could read, so it
would cost complexity and buy nothing. Revisit if LightDrop ever runs as a different user from the
CLI calling it, or if a peer can be paired without both humans present.

---

### 29. Peer dials never use a proxy, and the dial is bounded

`SocketsHttpHandler` obeys the system proxy, PAC files and `HTTPS_PROXY` by default, and Windows'
"bypass for local addresses" covers dotless names only, not `192.168.x.y`. A peer dialed through a
proxy becomes a `CONNECT` to a third party: usually a failed send blamed on the peer, otherwise a
LAN transfer relayed off the LAN. Every handler `PairingTls` builds sets `UseProxy = false`.

The same handlers bound **connect plus TLS handshake at 10 seconds**, and nothing else. A peer that
slept or left Wi-Fi stays in the registry for up to 180 s, and a firewall dropping the port answers
nothing, so without it `lightdrop send` sat silent for the OS connect timeout (75 s on macOS) — or
for ever against something that accepts TCP and never speaks TLS. The transfer itself keeps no
timeout, because a multi-gigabyte file legitimately takes minutes; Ctrl+C is the bound.

---

### 30. The CLI owns the prompt; the daemon owns the pairing session

Settles the 2026-08-16 spec's open question. The session lives in the daemon (`PairingCoordinator`
over Core's `PairingSession`) because the daemon holds the key, the listener and the dial; a session
is never persisted, so it is discarded on expiry or restart. The terminal belongs
to the CLI, which starts a session over loopback (`POST /api/pairing`), polls it, prints the code,
asks `[y/N]` and posts the answer. No loopback request blocks on the other device, and Ctrl+C is a
`DELETE /api/pairing`.

**Cost:** polling, at human speed, on loopback. **Bonus:** the companion UI can drive the same
four routes later without the daemon learning anything new.

---

### 31. Received files default to `~/Downloads`, and `downloadFolder` is re-read per file

Zero configuration means the default must be right without a `config.json`: `home/Downloads` on
both platforms, created if missing. Deliberately not the Windows known-folder API, which can be
redirected — right in the common case, and `downloadFolder` covers the rest. `~` and relative
values resolve against home, never the daemon's working directory, which nobody editing the file
can know. Re-read for every file, so an edit applies without a restart.

Never overwrite, never half a file: stream to a hidden temp file in the same folder, reserve
`name (n).ext` with an atomic create-if-absent, then move over the reservation (`DownloadFolder`).

---

### 32. Discovery re-asks every minute, and asks for unicast answers

Found on the first two-machine test (2026-10-12, Mac Mini on Ethernet, MacBook Air on Wi-Fi). Two
bugs stacked. The daemon queried only at start (the library re-queries on new interfaces, and on
macOS not even that) and sent no periodic announcements, so every peer aged out of the 180-second
registry window three minutes after start, on any network. And the router delivered the Air's
multicast to the Mini but none of the Mini's to the Air — not even macOS's own Bonjour — while
unicast in both directions worked.

Fix (`MdnsPeerDiscoveryTransport.Refresh`): every 60 seconds, ask with the QU bit set so peers
answer the asker by unicast, *and* announce by multicast. The Air hears the Mini through unicast
answers; the Mini, whose questions never reach the Air, hears the Air through its announcements.
A test pins the interval to at most a third of `PeerRegistry.StaleAfter`.

**Cost:** a daemon no longer learns of peers from answers to someone else's query, and
re-announcing every minute is more than RFC 6762 asks for (it announces at start and lets queries
refresh caches). One query and two announcement packets a minute per daemon is negligible on a
home LAN.
**Not done:** a manual `peer add <address>` fallback for networks that drop multicast in *both*
directions. Add it when such a network shows up.

---

### 33. `lightdrop autostart` is a LaunchAgent, opt-in, and macOS-only for now

Asked for after the first two-machine test: a Mac should receive files without a terminal left
open. `autostart on` writes `~/Library/LaunchAgents/com.lightdrop.daemon.plist` for the binary it
was run from and loads it with `launchctl bootstrap gui/<uid>`; `off` boots it out and deletes it.
A LaunchAgent, not a LaunchDaemon: it runs as the user whose Downloads, Application Support and
Local Network permission LightDrop uses, and needs no admin rights. It stays compatible with "no
installation" because nothing is registered unless the user asks, and `off` removes all of it.

`KeepAlive` is `SuccessfulExit = false`: restart after a crash or a failed bind, never after a clean
stop. A daemon already running in a terminal makes the background one fail its bind and retry every
ten seconds (launchd's throttle) until it can take over — a few log lines, accepted. The log file
(`~/Library/Logs/lightdrop.log`) is not rotated.

**Windows is deferred, not forgotten.** The Run key and Task Scheduler both start
`lightdrop.exe daemon` in a visible console window at every logon, because it is a console-subsystem
program. Fixing that properly means the daemon detaching from or never creating a console, and
writing its log somewhere — its own piece of work, and untestable here without a Windows machine.
Until then the command says so and points at Task Scheduler.

