# M2 — Secure pairing design

Status: **design agreed, not implemented.** Decided 2026-08-16, immediately after
M1 was verified by hand between a Windows 11 desktop and a Mac Mini on macOS 15.7.4.

The roadmap calls M2 the one milestone that cannot be retrofitted, so the four
decisions below were made deliberately rather than inherited from what already
exists. Everything after them follows from them.

## The four decisions

### 1. The ceremony is compare-and-confirm

Both machines derive and display the same six-digit code. The user checks that
they match and confirms on each side.

**The code is never transmitted.** It is derived independently from both public
keys, so a man-in-the-middle — holding a different key toward each side —
produces different digits on the two screens. This is what Bluetooth numeric
comparison and Signal safety numbers do.

**Rejected:** entering a PIN typed from one machine into the other (more
friction, and the code becomes a secret that must be high-entropy and
single-use). **Rejected:** approve-only with no code — nothing binds the
approval to the actual key exchange, so an active attacker on the LAN sits in
the middle and both sides still see a plausible prompt.

### 2. The LAN is reachable only during an open pairing window

A listener binds to the LAN only while `lightdrop pair` is running, with a short
timeout, then closes. Outside that window there is no LAN-reachable socket at
all.

This keeps M1's property that discovery added nothing reachable — all peer
metadata rides in mDNS TXT records. "Reject every unpaired peer" is then enforced
by **absence of a listener**, not by a check that a future endpoint could forget.

**Known cost:** M3 must reopen this. Receiving a file needs a listener when no
human is at the keyboard. That is a deliberate deferral, not an oversight.

**Rejected:** binding permanently once a first peer is paired (M3 would inherit
it unchanged, but it exposes a socket to the whole network whenever the daemon
runs). **Rejected:** always binding — a fresh install with zero peers would
listen on the network before the user has done anything.

### 3. The device id stays a random GUID; the key is pinned separately

`deviceId` remains the random value already persisted on both machines.
`TrustedPeer` gains the peer's pinned public key alongside it.

```
id  = who you are      (random, stable, broadcast in mDNS)
key = proof you are them  (pinned at pairing, never broadcast)
```

Identity and key material stay decoupled, so a future key rotation does not
destroy the human-facing identity or invalidate every existing pairing. Zero
migration. A spoofed id in mDNS buys an attacker nothing, because TLS pinning
rejects them at the handshake.

This closes the open question in `DECISIONS.md` #19, which asked for an answer on
day one of this milestone rather than a default.

**Rejected:** making the id a key fingerprint. Self-certifying and elegant, but
key rotation becomes identity change, and both machines have already persisted
GUIDs that would need migrating.

### 4. Mutual TLS 1.3 with self-signed certificates, pinned at pairing

Each device generates a self-signed ECDSA P-256 certificate on first run,
alongside its identity. Pairing runs over TLS 1.3 with both sides presenting
certificates and deferring validation for the duration of the window. On mutual
confirm, each side pins the peer's SPKI.

Kestrel and `HttpClient` already do all of this. **What gets written is key
generation, code derivation, and pinning — nothing else.**

**Rejected:** raw ECDH + HKDF over a plain socket. It would mean hand-building
framing, nonces, transcript binding, replay defence and a key schedule — which is
"invent cryptography", vetoed by the product constraints. **Rejected:** a Noise
Protocol library. Right pattern on paper, but the .NET implementations are thin
and unmaintained, and `DECISIONS.md` #15 already accepts one abandoned-fork
dependency for mDNS. Taking that risk a second time, in the trust path, is a
worse trade.

## Blocking prerequisite: discovery captures no address

`MdnsPeerDiscoveryTransport` reads TXT and SRV records and **ignores A records**.
`PeerAnnouncement` and `DiscoveredPeer` carry a port and no address. There is
nothing to dial today.

M2 must add address capture before any pairing code can run. This widens the
untrusted-input surface M1 deliberately narrowed, so it needs care:

- The advertised A record is attacker-controlled like every other field. A
  hostile announcer could point it at a third party and make this machine open
  TLS connections to a host of its choosing.
- Prefer **the address the packet actually came from**; fall back to the claimed
  record.
- Validate it as a local-subnet address at the existing `PeerAnnouncement.TryCreate`
  chokepoint, never at the point of use.

## Scope

In: `lightdrop pair`, `lightdrop unpair`, `lightdrop peers --trusted`.

`unpair` is included deliberately. Without it, an unwanted pairing can only be
undone by hand-editing `state.json` — a file the docs explicitly say users are
not expected to touch.

Out: any authenticated command. `CLAUDE.md` says not to rebuild the command
scaffold until M3, and ~250 lines of it were already deleted once for existing
prematurely (`DECISIONS.md` #11).

**Done when:** two machines pair; a mismatched code aborts with nothing
persisted; an unpinned certificate is rejected at the TLS handshake.

## Design

### Where the pieces live

- **Core** — `PairingCode` (pure derivation), `TrustedPeer` gaining `PublicKey`,
  and a concrete `PairingService` holding the pin-or-reject rules. No interfaces:
  one implementation each, per "interfaces are for ports, not habit".
- **Daemon** — certificate generation and persistence, the ephemeral LAN
  listener, the loopback endpoints the CLI drives. All I/O stays here.
- **Cli** — `pair`, `unpair`, `--trusted` on the existing peers command.

The private key goes in `state.json`, which is already `0600` for exactly this
reason: `DECISIONS.md` #13 anticipated that it "holds device identity now and
peer key material later".

### The handshake

Both users run `lightdrop pair <name>`. Each daemon opens an ephemeral LAN
listener and dials the other, with **roles assigned deterministically by
comparing device ids** — lower id listens, higher id dials. That removes the race
where both connect at once.

```
code = SHA256("lightdrop-sas-v1" || spki_lower || spki_higher)
       first 4 bytes -> mod 10^6 -> 6 digits, zero-padded
```

Sorting the two SPKIs by byte order makes the derivation symmetric, so both sides
compute the same value independently with no extra round trip.

**The code is stable for a given device pair, not per session.** This is
deliberate — it is SSH-style key verification, and re-pairing shows the same
digits. TLS supplies the channel; the pinned SPKI carries trust forward.

### Failure behaviour

| Case | Result |
|---|---|
| Codes do not match, user answers no | Abort, nothing persisted, both sides log it |
| Window expires (60s) | Listener closes, session discarded |
| Peer not in the discovery registry | Error naming the peer |
| Already paired | **Refuse**, tell the user to `unpair` first |
| Second concurrent session | Busy |

Refusing to re-pair matters: silently replacing a pinned key is a downgrade path.
Replacement must be explicit.

### Testing

- **Core** — SAS vectors, order-independence, pin comparison, store add and
  remove, all against in-memory fakes.
- **Daemon** — real certificates, real TLS between two in-process daemons on
  loopback with separate data directories, real persistence. Every test passes a
  no-op discovery transport, or it opens real sockets.
- **Manual** — two real machines, as with M1. A mismatched code can only be
  forced by hand.

### Documentation to update on implementation

`Protocol.md` gains a pairing section. `DECISIONS.md` records the TLS choice and
the ephemeral window, and marks #19 closed by decision 3 above. The roadmap M2
section and `Architecture.md` follow.

## Open questions for implementation

- Certificate lifetime and what happens at expiry — a pinned SPKI outliving its
  certificate is fine, but the local cert still needs a renewal story.
- Whether `unpair` should be symmetric. Today it can only be local: the peer
  keeps its pin until it unpairs too. Probably acceptable, but state it in the
  docs rather than leaving it implied.
- Whether the confirmation prompt belongs in the CLI or the daemon. The CLI owns
  the terminal, but the daemon owns the session — the split needs deciding before
  code.
