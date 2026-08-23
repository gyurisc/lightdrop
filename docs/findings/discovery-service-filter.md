# Discovery surfaces non-LightDrop services as peers

**Found:** 2026-08-23, on a real two-machine network (Windows PC + Mac Mini).
**Status:** diagnosed, not fixed.
**Affects:** `src/LightDrop.Daemon/Discovery/MdnsPeerDiscoveryTransport.cs`

## Symptom

`lightdrop peers` on the Windows machine listed three peers where only one was real:

```
DEVICE         PLATFORM   PROTO  ID
Peer da8e0965  unknown    0      da8e0965
Pips-Mac-mini  macos      1      a5165a47
Peer 5ECE3D53  unknown    0      5ECE3D53
```

`da8e0965` is an LG webOS TV's Google Cast service. It is not a LightDrop device and never was.

## Cause

`ServiceDiscovery.ServiceInstanceDiscovered` fires for **every** mDNS service instance the
library observes on the link, not only for the service type that was queried. Browsing
`_lightdrop._tcp` does not narrow the event stream.

`MdnsPeerDiscoveryTransport.OnServiceInstanceDiscovered` never checks the instance name against
the LightDrop service domain. It requires only that the message carry a TXT record matching the
instance name, and hands that record to `PeerTxtRecord.TryParse`. A TXT record containing an
`id=` key is therefore sufficient to mint a peer — and `id` is a common DNS-SD key.

`PeerAnnouncement.TryCreate` behaves correctly given that input; it is not the defect. The
fallbacks it applies are what make these rows recognizable:

| Missing TXT key | Rendered as |
|---|---|
| `name` | `Peer <first 8 runes of id>` |
| `plat` | `unknown` |
| `pv` | `0` |

That triple — derived name, `unknown`, `0` — is the signature of a non-LightDrop service.

## Evidence

A throwaway probe using the same library, browsing **only** `_lightdrop._tcp`, printing every
`ServiceInstanceDiscovered` event and flagging those whose TXT record carries an `id=` key:

```
>>> ID  ae471fdc...._lightdrop._tcp.local     id=ae471fdc0c004589b6ab810fcf6c9324
        [LG] webOS TV OLED48B53LA._airplay._tcp.local
>>> ID  OLED48B53LA.DEUQLJP-da8e0965f8964be1afbb80e2a4fa288c._googlecast._tcp.local
          id=da8e0965f8964be1afbb80e2a4fa288c
--- 3 distinct service instances seen while browsing _lightdrop._tcp only ---
```

Two things this shows:

- Unrelated service types reach the handler. The AirPlay instance was delivered to a browse for
  `_lightdrop._tcp`.
- The AirPlay instance survives only by accident: its key is `deviceid`, not `id`, so
  `TryCreate` rejects it for want of an identifier. Nothing in the code declined it.

`5ECE3D53` did not announce during the probe window and was not identified. Same signature, so
almost certainly another appliance on the same link.

## Not affected

Cross-machine discovery works. `Pips-Mac-mini / macos / 1` is a correct row, and this is the
first confirmation of discovery between two real machines rather than two daemons on one — which
closes a gap listed in `CLAUDE.md`.

## Proposed fix

Filter on the service domain in both handlers:

```csharp
private static readonly DomainName ServiceDomain = new($"{ServiceName}.local");

// first statement of OnServiceInstanceDiscovered and OnServiceInstanceShutdown:
if (!e.ServiceInstanceName.IsSubdomainOf(ServiceDomain)) return;
```

`IsSubdomainOf` was verified against real instance names taken from the probe:

| Name | `IsSubdomainOf("_lightdrop._tcp.local")` |
|---|---|
| `ae471fdc…._lightdrop._tcp.local` | `True` |
| `OLED48B53LA-da8e0965…._googlecast._tcp.local` | `False` |
| `LGwebOSTV._airplay._tcp.local` | `False` |
| `_lightdrop._tcp.local` | `False` |

The shutdown handler is not currently exploitable — it derives a device id from `Labels[0]`, and
a foreign instance label will not match a registry key, so no false eviction occurs. Filter it
anyway; the guarantee should not rest on a key collision failing to happen.

## Open questions

1. **Test coverage.** The fix lands in `MdnsPeerDiscoveryTransport`, which `CLAUDE.md` marks as
   deliberately untested because multicast cannot run in CI. Extracting a testable predicate
   would pull Makaretu's `DomainName` into Core and break its platform independence. Recommended:
   keep the filter in the transport and treat it as hand-verified, consistent with the rest of
   that file.

2. **Self-announcement.** `OnServiceInstanceShutdown` skips `_localDeviceId`;
   `OnServiceInstanceDiscovered` has no equivalent check, so nothing in the code prevents a daemon
   ingesting its own announcement as a peer. Not observed on the test network — the local device
   does not appear in `lightdrop peers` — but whether that is structural or incidental was not
   established.

## Security note

This is a correctness bug, not a trust boundary failure. The discovery invariants held: no
foreign announcement reached `IStateStore`, the registry stayed bounded, and every value was
sanitized once at ingestion. The registry is capacity-bounded at 256 with least-recently-seen
eviction, so an attacker cannot grow it without limit — but on a busy network, unrelated services
consume slots that should belong to real peers.
