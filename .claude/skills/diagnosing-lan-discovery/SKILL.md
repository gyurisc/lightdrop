---
name: diagnosing-lan-discovery
description: Use when devices on a local network cannot see each other, when a peer list is empty or contains devices that should not be there, when discovery works in one direction only, or when mDNS/Bonjour/DNS-SD/multicast behaves differently between two machines.
---

# Diagnosing LAN Service Discovery

## Overview

Local discovery fails silently. There is no error, no status code, and no timeout —
only an empty list that looks identical whether the network is blocking multicast,
the peer is absent, a permission is denied, or browsing simply has not converged yet.

**Core principle:** an empty result is not evidence of anything until you know which
layer it came from. Establish what each observer can actually see before you believe
what it does not show.

**RELATED:** `superpowers:systematic-debugging` — this is the network-specific case of
validating an observer before trusting a negative result.

## The observation ladder

Work outward. Each rung answers a different question, and skipping one produces a
confident wrong answer.

| Rung | Question it answers | How |
|---|---|---|
| 1. The wire | Is the peer announcing at all? | `tcpdump -i <if> -n port 5353`, or the system browser below |
| 2. The system stack | Does the OS resolver see the service? | macOS `dns-sd -B _svc._tcp`, `dns-sd -Z _svc._tcp local`; Linux `avahi-browse -a` |
| 3. Your process | Does *your* socket receive it? | Your app's own logs and peer list |
| 4. The far machine | Does the peer see you? | Run the equivalent query there |

Rungs 3 and 4 are the ones people skip. Discovery is two independent directions:
receiving and advertising. **Verify both.** A machine that browses correctly and
advertises nothing looks fine locally and is invisible to everyone else.

## What each observer cannot tell you

**The system browser is not a proxy for your process.**
`dns-sd` and `avahi-browse` query the OS mDNS daemon. Your application, if it opens
its own multicast socket, is a different receiver with different permissions. Either
can work while the other fails.

- The system daemon may be **exempt from permissions your process is subject to** —
  macOS Local Network permission is per-application; a system daemon is not prompted.
  "It works in `dns-sd`" says nothing about your app.
- The system daemon may **not report another local process's advertisement**
  reliably. If it lists nothing for your own host, that is not proof your host is
  silent. Confirm from a second machine instead.

**Never trust a negative from an observer you have not validated.** Before concluding
from silence, make the tool show you something you know is present.

## Convergence is not failure

Browsing is periodic. A daemon started seconds ago has legitimately heard nothing.
Observed spread on one working pair: **12s in one run, 96s in another**, same code,
same network, with the peer advertising unchanged throughout.

- Check twice, minutes apart, before diagnosing anything.
- If the tool reports "none found" plus firewall advice, treat the advice as
  unproven — it cannot distinguish *blocked* from *not yet*.
- Product fix: report discovery state and a start timestamp, never a bare empty list.

## Peers that should not be there

Browsing one service type does **not** guarantee only that type is delivered. mDNS
libraries commonly raise an event for every instance on the link. If the code does not
compare the instance name against the service domain, foreign services are ingested.

Signature of a foreign row: fields derived or defaulted because the keys are absent —
a name synthesized from an identifier, `unknown` platform, version `0`. A TXT key as
generic as `id` is enough; televisions, printers and speakers all advertise one.

Filter on the fully qualified service domain, and additionally require a key that only
your protocol emits — the second check is testable where the transport is not.

## Isolating which side is broken

Use a **control**: a second instance you know the state of.

- Two daemons on one machine, separate data directories and ports, exercise real
  multicast without a second computer. Keep loopback in any interface filter for this.
- When something breaks after a change, run the **previous build alongside** the new
  one. If both go quiet at the same moment, the network changed, not the code.
- After a network change (Wi-Fi switch, VPN up/down), a running process may hold stale
  socket state. Compare a freshly started instance against the long-running one before
  blaming either.

## Common mistakes

| Mistake | What actually happened |
|---|---|
| "The system browser doesn't list us, so we aren't advertising" | It is a poor observer of the local host. The far machine saw us fine. |
| "Empty after 30s, so it's blocked" | Convergence took 96s. |
| "Works in `dns-sd`, so permissions are fine" | The daemon is exempt; the app process was not. |
| "A peer appeared, so discovery is correct" | It was a TV. Nothing checked the service type. |
| "It broke after my change" | The remote daemon had stopped. A control on the old build proved it. |

## Before claiming a fix

Discovery is bidirectional and multicast cannot run in CI. Verify **both directions on
two real machines**, and state plainly which direction you did not test.
