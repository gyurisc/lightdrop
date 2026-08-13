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

DNS-SD requires a port. The advertised port is unreachable (loopback bind), but port `0` is not the
DNS-SD "no service" signal — `Target="."` is — and zero breaks well-behaved clients for no security
gain. Documented as not an authorization boundary instead of encoded as one.

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
