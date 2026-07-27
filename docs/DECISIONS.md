# Design Decisions

Decisions taken after Phase 1. `ARCHITECTURE.md` records the approved
Phase 1 design and is left as it was; where a decision here supersedes
it, that is stated.

---

## 1. The Hub is optional, not the centre

**Date:** 2026-07-27
**Status:** accepted
**Supersedes:** the Phase 1 framing of "Windows Hub as system centre"

### Decision

A complete OpenAudioLink system must be able to run with **no Hub
present**. The ESP layer takes coordinating roles: an Analog Source
discovers receivers and streams to them on its own, and receivers accept
streams without a Controller having arranged it.

The Hub remains valuable — it is the Provisioner, a Producer for
Windows-hosted sources, and the richest Controller UI — but nothing may
*depend* on it at run time. A turntable playing to three rooms must keep
working when the PC is switched off.

### Why

Phase 1 named the Hub the centre of the system. In practice that makes a
Windows PC a single point of failure for listening to a record, which is
the opposite of the local-first goal. The role model already allows this:
roles are logical, and Controller was only ever "normally implemented by
the Hub".

### Consequences

- The Analog Source firmware grows a discovery **registry** (it already
  announces; it must also listen and remember), a small web UI for
  choosing receivers, and persistence of its route across reboots.
- **Stream ownership needs arbitration without a central authority.**
  The chosen mechanism is a claim held by the *receiver*: a Producer
  claims a Consumer for a lease period and renews it; the Consumer
  refuses a second claimant while a lease is live, and frees itself when
  a lease expires. Ownership therefore lives with the device being
  contended for, which is the only party guaranteed to be present.
- The Hub uses exactly the same claim API as an ESP Controller. It gets
  no privileged path, which is what keeps the Hub-less case working
  rather than merely intended.
- Discovery stays symmetric: every device announces and every device may
  listen. The Hub is not special in discovery either.

### Not decided yet

How a user picks receivers with no Hub and no phone app — the Analog
Source's standalone web UI is the likely answer, but its scope is open.

---

## 2. Unicast first, multicast as a supported alternative

**Date:** 2026-07-27
**Status:** accepted

### Decision

A Producer replicates unicast RTP to each selected Consumer. Multicast
is a supported per-stream alternative, not a replacement, and not the
default.

Practical planning threshold: **about 4 receivers per Producer** over
Wi-Fi. Beyond that, prefer multicast or a wired Producer.

### Why

Wi-Fi multicast frames are unacknowledged, never retransmitted, and sent
at a low basic rate; they are also buffered against DTIM beacons, which
a station in power save can miss entirely — a well-known ESP32 failure
mode. Unicast gets link-layer acknowledgement and retries, so for a
handful of receivers it is materially more reliable despite costing more
airtime.

Multicast becomes the better choice with wired receivers, with more
receivers than a single ESP32 can replicate to, or on an access point
with reliable multicast handling. It is worth testing on capable
hardware rather than assuming.

### Consequences

- Cost is linear per receiver: roughly 2.37 Mbit/s and 200 packets/s at
  L24/48 kHz with 5 ms packets.
- An ESP32-S3 Analog Source replicating to several receivers is the
  tightest constraint in the system, and packets per second matter more
  than bandwidth. This needs measuring on real hardware, which can be
  done with a tone generator before any ADC exists.
- L16 is a legitimate reduction for an analog source: vinyl's noise
  floor sits well inside 16 bits, and it cuts roughly a third of the
  bandwidth while allowing longer packets within the MTU.
