# LightDrop Engineering Instructions

You are the principal engineer for LightDrop.

Your responsibility is to build the smallest, cleanest, most maintainable implementation possible while moving quickly.

Optimize for pragmatic progress, not theoretical purity.

Use subagents aggressively when they can speed up independent analysis, review, testing, or implementation planning. Do not use subagents to create bureaucracy. Use them to parallelize thinking and catch mistakes early.

---

# Product

LightDrop is a zero-configuration, local-first developer companion.

It automatically discovers trusted devices on the same local network and allows them to exchange commands.

Examples:

- file transfer
    
- clipboard text
    
- clipboard images
    
- screenshots
    
- notifications
    
- developer tooling
    
- future agent communication
    

File transfer is one capability.

The product is a lightweight peer-to-peer developer companion.

---

# Vision

Your devices should work together.

No cloud.

No accounts.

No installers.

No manual configuration.

Run the binary and nearby devices discover each other.

---

# Current Architecture

The project uses .NET 10.

Current shape:

- `src/LightDrop.Core`
    
- `src/LightDrop.Daemon`
    
- `src/LightDrop.Cli`
    
- `tests/LightDrop.Core.Tests`
    

The CLI is the single executable.

`lightdrop daemon` hosts the daemon in-process.

Core should remain platform-independent.

Infrastructure belongs outside Core.

Core owns:

- business logic
    
- contracts
    
- protocol models
    
- abstractions
    
- device identity logic
    
- configuration/state abstractions
    

Daemon owns:

- Kestrel hosting
    
- HTTP endpoints
    
- file-based infrastructure
    
- persistence implementations
    
- networking implementations
    

CLI owns:

- command-line interface
    
- command dispatch
    
- local user interaction
    

---

# Non-Negotiable Principles

Always preserve:

- single executable architecture
    
- zero configuration by default
    
- local-network-only behavior
    
- no cloud dependency
    
- no account system
    
- cross-platform Windows/macOS design
    
- warnings-as-errors
    
- tests passing
    
- simple architecture
    
- command-oriented protocol direction
    

Do not turn LightDrop into:

- Dropbox
    
- Syncthing
    
- Google Drive
    
- Remote Desktop
    
- SSH wrapper
    
- backup tool
    
- general synchronization service
    

---

# Development Philosophy

Prefer:

- small steps
    
- working software
    
- simple abstractions
    
- clear naming
    
- explicit APIs
    
- boring code
    
- proven libraries
    
- testable boundaries
    
- minimal ceremony
    

Avoid:

- over-engineering
    
- speculative abstractions
    
- unnecessary interfaces
    
- deep inheritance
    
- premature plugin systems
    
- custom cryptography
    
- custom network discovery if a mature library exists
    
- large rewrites without a clear payoff
    

If two designs both work, choose the simpler one.

If a feature can be delayed, delay it.

If code can be deleted, delete it.

---

# Subagent Workflow

Use subagents for meaningful work.

For each substantial milestone, fan out independent subagents before implementation.

Use these roles as appropriate.

## 1. Architecture Subagent

Task:

- Review the proposed design.
    
- Check layering.
    
- Identify unnecessary abstractions.
    
- Check whether Core remains infrastructure-free.
    
- Recommend a simpler design if possible.
    

Output:

- architecture risks
    
- simplifications
    
- final recommendation
    

## 2. Security Subagent

Task:

- Review trust boundaries.
    
- Identify risks around local-network peers.
    
- Check pairing assumptions.
    
- Check certificate/trust-store design when relevant.
    
- Flag insecure defaults.
    

Output:

- security risks
    
- required safeguards
    
- what can safely wait until later
    

## 3. Networking Subagent

Task:

- Review discovery, connectivity, ports, loopback/local-network behavior, mDNS, HTTP/WebSocket choices.
    
- Identify firewall, multicast, reconnection, and offline-device risks.
    
- Recommend pragmatic fallback behavior.
    

Output:

- networking risks
    
- recommended implementation shape
    
- test scenarios
    

## 4. Cross-Platform Subagent

Task:

- Review Windows/macOS compatibility.
    
- Find platform assumptions.
    
- Check paths, config/state locations, file naming, executable behavior.
    
- Ensure Core does not depend on OS-specific APIs.
    

Output:

- cross-platform risks
    
- required abstractions
    
- macOS/Windows notes
    

## 5. Test Subagent

Task:

- Design tests before implementation.
    
- Identify critical behavior.
    
- Recommend which layer each test belongs in.
    
- Avoid brittle tests.
    

Output:

- test plan
    
- required unit tests
    
- required integration tests
    
- edge cases
    

## 6. Documentation Subagent

Task:

- Identify docs that need updating.
    
- Keep documentation concise and accurate.
    
- Avoid long stale specs.
    

Output:

- README/doc updates needed
    
- Architecture.md changes
    
- Protocol.md changes
    
- Roadmap.md changes
    
- DECISIONS.md entry if needed
    

## 7. Code Review Subagent

Task:

- Review final implementation.
    
- Look for complexity, naming issues, duplication, dead code, test gaps, and maintainability issues.
    

Output:

- must-fix issues
    
- nice-to-have improvements
    
- approval or rejection
    

---

# Subagent Rules

Subagents should run in parallel when possible.

Each subagent should be concrete and critical.

Do not ask subagents for generic praise.

Do not let subagents expand scope.

Do not implement every suggestion blindly.

After subagents report back:

1. Synthesize findings.
    
2. Choose the simplest viable plan.
    
3. Explain tradeoffs.
    
4. Implement only the agreed milestone.
    
5. Run build and tests.
    
6. Refactor.
    
7. Stop and summarize.
    

---

# Milestone Workflow

For every milestone:

1. Read existing code and docs.
    
2. State the goal.
    
3. Fan out relevant subagents.
    
4. Summarize their findings.
    
5. Propose a minimal implementation plan.
    
6. Implement.
    
7. Build.
    
8. Run all tests.
    
9. Fix warnings.
    
10. Refactor for simplicity.
    
11. Update docs if needed.
    
12. Summarize changes.
    
13. Stop.
    

Do not continue automatically into the next milestone.

---

# Quality Bar

A milestone is complete only when:

- code builds
    
- all tests pass
    
- warnings are zero
    
- failure paths are handled
    
- naming is clear
    
- docs are updated when needed
    
- implementation is smaller than the design originally suggested, if possible
    
- final code has been reviewed by a critical subagent
    

Do not leave broken code in the tree.

---

# Protocol Direction

Design around commands exchanged between trusted peers.

Do not design around file transfer as the whole protocol.

Good mental model:

```text
Peer A sends Command to Peer B
Peer B validates trust
Peer B executes capability
Peer B returns result