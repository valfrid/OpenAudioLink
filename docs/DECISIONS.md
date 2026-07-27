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

This is a return rather than a new direction. The founding use case was a
wireless link from a vinyl rig to party speakers, with no PC anywhere in
it. The Hub was added later to solve a specific problem — Spotify Connect
cannot run on an ESP — and it grew into the system's centre by accident
of description rather than by design. Windows earns its place by hosting
sources the ESP platform cannot, not by being required.

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

---

## 3. Where a source lives is a capability question, not a hierarchy

**Date:** 2026-07-27
**Status:** accepted (principle); specific sources not yet scheduled

### Decision

A source belongs on whichever platform can host it. Nothing about being
a Producer implies a Hub, and nothing about needing a PC makes the PC
central.

Current split:

| Source | Platform | Why |
| ------ | -------- | --- |
| Analog line-in (vinyl, TV, mixer) | ESP32 + ADC | Needs only capture; no PC justified |
| Spotify Connect | Windows | Cannot run on ESP32 — the reason the Hub exists |
| Windows system audio | Windows | Is the PC's own output by definition |
| Internet radio | Windows (preferred) | Technically feasible on ESP32, but see the load and sample-rate notes below |
| DAB/DAB+ | ESP32 + tuner module | Terrestrial radio needs RF hardware, not a PC |

Internet radio is **not scheduled**, and when it arrives the Windows Hub
is the intended host. An ESP32 implementation stays possible but is not
the plan: decoding competes with replication on one chip and one radio,
and a PC already has the headroom that makes stream buffering robust.

### Internet radio and DAB are future Producer roles

Both slot into the existing role model without architectural change: a
node fetches or tunes, decodes to PCM, packetises, and sends RTP. The
receiver never learns the difference, which is the point of keeping
Consumers simple.

They are not the same engineering problem, despite both being "digital
radio":

- **Internet radio** is pure software — HTTP/Icecast fetch plus MP3 or
  AAC decode. Well proven on ESP32, and ESP32-S3 with PSRAM suits it
  because buffering against network stalls is what makes it robust.
- **DAB/DAB+** is terrestrial and needs an RF tuner module (Si468x or a
  similar DAB receiver) delivering PCM over I²S. From the ESP's side it
  then resembles the Analog Source: audio arrives, gets packetised, gets
  sent.

### Two things to weigh before committing to internet radio on ESP32

**Load.** Decoding is far heavier than analog capture, which is only DMA
in. An internet-radio node does HTTP receive, decode, packetise and then
replicate to every receiver — all on one chip, competing for the same
Wi-Fi radio. Unicast replication cost applies on top of decode cost, so
this is the source most likely to need multicast or a lower receiver
count.

**Sample rate.** Broadcast streams are commonly 44.1 kHz while the
reference format is 48 kHz. Either the source resamples (costly on an
ESP32) or the stream advertises 44.1 kHz and receivers reconfigure I²S
per stream. The protocol already carries the rate, so both are legal;
the choice is about where the cost lands. This is worth settling before
implementation rather than during it.
