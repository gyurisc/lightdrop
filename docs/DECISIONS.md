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

### 14. `.sln`, not `.slnx`

.NET 10 defaults to the newer `.slnx`. Classic format chosen for tooling compatibility. Worth revisiting — `.slnx` has no GUIDs and no merge conflicts.
