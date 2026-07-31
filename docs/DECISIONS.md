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

### Order of attack if a Producer runs out of capacity

1. **Keep the ESP32-S3 baseline.** Its second core matters here: I2S DMA
   servicing can run apart from Wi-Fi transmit bursts. The C6 is 2.4 GHz
   only despite being Wi-Fi 6, and the dual-band C5 is single-core and
   far less proven. No platform change without measurements demanding it.
2. **Multicast**, which stops sender cost scaling with receiver count.
3. **Router tuning** — a dedicated SSID on a clean channel removes most
   contention without isolating anything.

L16 and longer packets remain available at any point and are cheaper
than all three.

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

---

## 4. Two deployment modes: infrastructure and standalone

**Date:** 2026-07-27
**Status:** accepted (principle); standalone mode not yet implemented

### Decision

OpenAudioLink supports two ways of being deployed, and neither is a
degraded version of the other.

**Infrastructure** — the permanent installation. Devices join an existing
Wi-Fi network. A Hub may be present for Windows-hosted sources,
provisioning and a richer UI, but is not required.

**Standalone ("party mode")** — equipment carried to a venue with no
usable network. The Producer creates its own access point, receivers join
it, and the system works with nothing else present: no router, no Hub, no
internet. Fewer receivers, by nature of the setting.

### Why

This was the founding use case in a different shape: a self-contained
audio link that works wherever it is put down. It is also the strongest
test of the Hub-optional decision — if standalone mode works, no hidden
dependency on the Hub survived.

### Consequences

- The soft-AP already exists for provisioning; standalone mode reuses it
  as an operating mode rather than a setup mode.
- An ESP soft-AP realistically serves about four stations, which suits
  the setting.
- The Producer's radio does access-point duty *and* audio replication, so
  the per-receiver ceiling is lower than in infrastructure mode. Fewer
  speakers is not just typical here, it is a constraint.
- The AP's DHCP server assigns addresses, and discovery works unchanged
  because it is multicast on that subnet.

### The open question: how a receiver picks its network

A receiver configured for a home network must still join the party
network without a laptop present. The likely answer is a fallback chain,
the same shape as today's credentials-or-portal logic:

```text
configured network -> an OpenAudioLink access point -> own setup portal
```

This makes a receiver "follow" whichever OpenAudioLink Producer is
present when its home network is not, with no interaction. It needs a
well-known naming or beacon convention so a receiver can recognise an
OpenAudioLink AP rather than any open network, which is not yet designed.

---

## 5. One firmware image, role held in NVS

**Date:** 2026-07-27
**Status:** implemented 2026-07-30. Two XIAO ESP32S3 nodes run the same
image as producer and consumer; the ESP32-C3 target is removed.

### Decision

**ESP32-S3 is the baseline for every node**, receivers included. The
ESP32-C3 is temporary scaffolding for pre-integration work while the S3
audio boards are in transit, and its build target is **removed** once the
S3 hardware is in hand and verified.

A node runs **one firmware image**, with its role — receiver, analog
source, or both — read from NVS at boot rather than compiled in. Role and
hardware profile are written during provisioning, and default to receiver
when unset.

### Why

Roles are logical, not device-bound, so baking a role into a binary
contradicts the architecture. Beyond consistency it buys real things: OTA
cannot push the wrong role to a device, the Hub's firmware store holds
one image instead of several, and a node changes role by configuration
instead of a USB trip.

Staying on one chip is what keeps it to a *single* image for the whole
system. Mixed silicon would mean two images forever, since different
targets need different binaries regardless of how the role is chosen.

A C3 is technically adequate as a receiver — one incoming stream, jitter
buffer, I²S out, no replication. It is not adequate as a Producer feeding
several rooms, and a device that cannot take a role is an asymmetry to be
remembered forever. Where local status display is wanted, an external I²C
OLED fits any board and is far more readable than the 72×40 panel on the
C3 Super Mini.

### Consequences

- Provisioning writes role and hardware profile to NVS; discovery and
  `/status` report the stored role rather than a compile-time constant.
- The image must contain both roles' code, so it must fit the OTA slot
  with margin. ~~At the time of writing the app is ~979 KB in a 1 MB slot,
  built at `-Og`. Size optimisation and checking whether the OTA path
  drags in mbedTLS for HTTPS unused on a trusted LAN should both be done
  **before** merging the roles, not after the slot overflows.~~
  **Struck 2026-07-30.** That reasoning assumed the 1 MB slot of the
  built-in table on 4 MB flash. The XIAO has 8 MB, and a partition table
  sized to it gives 4032 KB slots — a 3.9x increase that makes shrinking
  the image to fit unnecessary. Neither `-Os` nor removing mbedTLS is
  warranted; both were work with a scheduled expiry date.
- Removing the C3 target is a deliberate step with a trigger — S3
  hardware verified — not a gradual drift. Until then both targets build
  in CI. **Done 2026-07-30**: two XIAO ESP32S3 boards flashed,
  provisioned, discovered and holding distinct roles.

---

## 6. Seeed XIAO ESP32S3 is the preferred node hardware

**Date:** 2026-07-27
**Status:** accepted; two boards in hand and running since 2026-07-30

### Decision

The **Seeed XIAO ESP32S3** becomes the preferred board for all nodes,
receivers and sources alike. The generic ESP32-S3 Super Mini stays
supported as a secondary profile; the ESP32-C3 remains temporary
scaffolding per decision 5.

### Why

- **External U.FL antenna, and no PCB antenna at all.** This removes the
  enclosure antenna keep-out constraint outright rather than requiring
  design around it, and there is no antenna-selection resistor or GPIO
  switch to get wrong because there is nothing to select.
- **Dual-core 240 MHz Xtensa LX7**, which is the property decision 2
  relies on: I²S servicing can run apart from Wi-Fi transmit bursts.
- **A real vendor with published schematics, pinouts and mechanical
  models.** Anonymous Super Mini boards vary between batches, which
  undermines both hardware profiles and enclosure design. Seeed's
  published dimensions feed the enclosure work directly.
- **Integrated Li-Po charging**, which turns the standalone "party mode"
  of decision 4 from a mains-tethered idea into a portable one.

### Consequences

- **The antenna is mandatory.** With no PCB fallback the board is
  effectively deaf without it, and transmitting into an open connector is
  bad for the power amplifier. Assembly and enclosure design must treat
  the antenna as a required part, not an accessory.
- New hardware profiles, since pin mapping differs from the Super Mini:
  `xiao-esp32s3-pcm5102a` and `xiao-esp32s3-pcm1808`.
- 11 exposed GPIOs — enough for either role (three I²S pins, plus two for
  an optional I²C display) but with less spare than the Super Mini.
- Larger flash than the `FH4R2` Super Mini eases the single-image
  partition pressure from decision 5. ~~**Confirm flash and PSRAM on
  arrival**; XIAO variants differ, and the product listing does not state
  them.~~ **Confirmed 2026-07-30** from the flashing log: flash ID
  `0xC84017` — GigaDevice, capacity byte `0x17` = 2^23 = **8 MB** — and
  **8 MB embedded PSRAM**. Both double the Super Mini's, and the flash
  figure is what decision 5's struck consequence rests on. PSRAM is not
  enabled in the build yet; nothing needs it until buffering does.
- The enclosure gains an antenna mount — a U.FL pigtail to an SMA
  bulkhead, or a retention point for the supplied flexible antenna — and
  loses the keep-out zone.

---

## 7. Several Hub instances are legitimate; per-instance roles come later

**Date:** 2026-07-27
**Status:** accepted

### Decision

More than one Hub may run on a network, and that is a normal deployment
rather than a mistake. The expected shape is a **server instance** doing
Controller and Provisioner work as a Windows service, plus **desktop
instances** acting as Producers for the audio playing on those PCs.

Every instance currently offers every role it is capable of. Choosing
which roles an instance takes — at startup or at runtime — is
**deferred**; simplicity now is worth more than the configurability.

### Why

It falls out of the role model: roles are logical and a machine may
implement several. It is also forced by a real constraint — capture
cannot run in a Windows service, so any PC whose audio should be streamed
needs its own Hub process in a logged-in session, regardless of what the
server is doing.

### Consequences

- Each instance has its own identity, persisted in its data directory, so
  they are distinguishable in discovery and the device list.
- Several Hubs therefore appear as devices to each other. Harmless, but
  the list is longer than a newcomer expects.
- There is no notion of *the* Controller: whichever UI you use issues the
  command. Fine for a single operator; it will need thought if two people
  can act at once, which is the same arbitration question decision 1
  answers for stream ownership.
- Nothing prevents two instances streaming to the same receiver today.
  The receiver-held claim from decision 1 is what will resolve that, and
  it applies to Hubs exactly as it applies to ESP Producers.

---

## 8. How a Consumer emits audio is a profile property, not a role property

**Date:** 2026-07-30
**Status:** noted; nothing scheduled. Recorded so the options are not
re-derived later, and so the obstacles are known before anything is bought.

### Decision

`ARCHITECTURE.md` section 2 lists "I²S DAC output" among the Consumer's
responsibilities. That is the *current* profile's output, not a property
of the role. A Consumer is defined by receiving RTP, buffering it,
correcting drift and playing it; how the samples finally leave the device
belongs to the hardware profile.

Nothing in discovery, control, OTA or the RTP profile changes if a
Consumer emits over something other than I²S. Alternative Consumers are
therefore additive: new profiles, not a new architecture.

Three are worth remembering.

### A. USB Audio Class DAC on an ESP32-S3 in host mode

A USB-C dongle DAC (for example a CX31993 unit, ~120 SEK) gives a
shielded, finished output stage with a headphone amplifier, where the
PCM5102A gives line level from a bare module. Attractive for a desk or
headphone node; irrelevant for speakers going into an amplifier anyway.

The ESP must be the USB **host** and speak UAC. Espressif publishes a
`usb_host_uac` component, so the software is not from scratch. The
obstacles are hardware and are worth stating plainly:

- **VBUS.** A host powers its peripheral. A board whose USB-C is wired as
  a device port cannot source 5 V to a dongle. This is the blocker to
  solve first, and it is a board question, not a software one.
- **The console.** `CONFIG_ESP_CONSOLE_USB_SERIAL_JTAG=y` shares the
  native USB peripheral. On a single-port board, switching it to OTG host
  costs the heartbeat diagnostics and USB flashing at the same time.
  **A board with separate UART and USB-OTG ports removes this objection
  entirely**, which is what makes the experiment reasonable at all.
- **Full speed only.** The S3's USB-OTG runs at 12 Mbit/s. Bandwidth is
  not the issue — 48 kHz/24-bit stereo is 2.3 Mbit/s — but a dongle whose
  headline is 384 kHz/32-bit reaches that over high-speed UAC 2.0, which
  the S3 cannot do. Whether a given dongle also offers a full-speed
  interface at 48 kHz is a buy-and-test question; listings do not say.
- **Drift correction gets harder.** With I²S the ESP owns the bit clock
  and can trim it through the APLL. A USB DAC owns its own clock, so
  correction becomes varying how many samples go out per USB frame —
  resampling in software, audibly worse if done crudely, and CPU better
  spent elsewhere.

An **ESP32-S3-DevKitC-1** style board with two USB-C ports (one bridged
UART, one native USB) and an N16R8 module is the shape that fits: the
console stays on the UART port while the native port does host duty, and
16 MB flash with 8 MB PSRAM is generous next to the XIAO. Against it,
decision 6's objection to anonymous boards still applies to clones —
though the DevKitC-1 reference design is published by Espressif, so the
layout is knowable in a way a Super Mini's is not. **VBUS sourcing must
still be confirmed against the actual board**, since that is where clones
deviate and where the whole idea succeeds or fails.

### B. A PC as a Consumer

Already true and already proven: GStreamer decoding the L24 stream was how
the audio path was validated in the first place. A USB DAC on a PC is just
another endpoint. What is missing is not capability but a first-class
Consumer application — something that announces itself, holds a claim, and
appears in the device list rather than being a manually started pipeline.

### C. A Raspberry Pi as a Consumer

The unglamorous option that avoids every obstacle in A. Linux does UAC
host properly, VBUS is not a question, and there is CPU to spare for
resampling. More expensive and more power-hungry per room than an ESP32,
so it suits one good room rather than every room.

### Consequences

- The profile list grows a dimension: today profiles vary by board and
  audio chip, and a USB profile would vary by output *transport* too.
  `IDENTITY.md` does not need changing for that, but a name like
  `esp32s3-uac` should read as "output is USB", not as a chip.
- None of this is on the path to the first working system. The PCM5102A
  boards are the specified route, cost about 28 SEK, and give the clean
  clock correction that decision 2's synchronisation goals rely on.
- If A is ever attempted, VBUS is the first thing to test and the point at
  which to abandon it cheaply.

## 9. Controller is a small role; the Hub is a device that hosts it

**Date:** 2026-07-31
**Status:** decided; the peer table in firmware is the first piece of work.

### Decision

"Hub" and "Controller" have been used interchangeably and they are not the
same thing.

**Controller** is a role with a small job: know which devices are present,
decide who sends to whom, and start and stop streams. That is all of it.

**Hub** is a deployment — a PC that holds the Controller role *and* a pile
of services that are not the Controller: the operator interface, digital
sources and cast points, the firmware store and OTA, persistence across
restarts, and the link measurement tools.

The separation is what makes the small system answerable. A party system —
one analog Producer and a few Consumers, no PC anywhere — needs a
Controller. It does not need a Hub.

**In a system with no Hub, the Controller is hosted on the Producer.**

Two reasons, and the second is the structural one. The Producer is the
only node guaranteed to exist: Consumers are interchangeable and may come
and go, but there is exactly one turntable. And when Controller and
Producer are the same node, "tell the Producer where to send" stops being
a network call and becomes a function call — the control plane of the
party system collapses to nothing, so the part that could fail over Wi-Fi
does not exist.

### The default that makes it need no configuration

**A Controller with no configuration streams to every Consumer it can
see.**

Plug in the turntable node, power on the speakers, and music plays
everywhere. Beyond the Wi-Fi provisioning that already exists — which
already asks which roles the node holds — there is nothing to set up.

This layers correctly rather than being a special case. A Hub is a
Controller that has *more information*: rooms, cast points, groups. It
overrides the default with a better answer. Same role, richer policy, and
the minimal Controller is simply the one whose only cast point is
"everything".

### How a Consumer joins

The Consumer initiates and the Controller decides.

A Consumer that has finished booting finds the Controller and reports that
it is ready. The Controller answers. That is the whole handshake, and the
Consumer's behaviour is identical in every deployment — it never needs to
know whether the Controller is a turntable or a PC.

What differs is the answer:

- party: the Controller is the Producer itself, and it answers by adding
  the Consumer to the stream. Music plays.
- house: the Controller is the Hub, which knows about rooms and knows
  nobody has pressed play, so it answers "stand by".

This is the same layering as the default cast point. There is always
exactly one Controller; the Hub is simply the one with enough information
to give a better answer.

Having the Consumer initiate rather than the Producer scan matters for a
concrete reason: a scanning Producer can start sending to a node that is
still booting, and the packets are lost before anything is wrong. A
Consumer speaks when it is actually ready.

A Consumer that finds no Controller waits and retries; announcements are
periodic and a Producer claims the role within seconds. A Consumer that
sees two during a transition applies the same precedence rule below,
computed locally.

**Joining adds a destination; it does not start a second stream.** Two
speakers in a room must play identical samples at identical times, so
every Consumer receives one stream — the same sequence numbers,
timestamps and SSRC, replicated byte-identically, which the Producer
already does. Two independent streams with separate timestamp bases would
make synchronisation far harder for no gain. The consequence is that the
Producer must accept a destination-list change while running, which it
currently cannot: the list is fixed when the stream starts. A late joiner
then simply starts at whatever point the stream has reached, which RTP and
the receiver's probation already handle.

### Who holds it, without an election

Roles are already in NVS and provisioning already offers them, so the
explicit answer is that the operator chooses. For the system to show
initiative, one rule is added on top:

A node holding Producer that has seen no Controller announcement for a few
seconds **claims** Controller. Announcements already carry `roles`, so
this needs no new message and no election protocol. Precedence settles
conflicts: a Hub outranks a node, and between two nodes the lower device
id wins. Deterministic, and computable by each node alone.

The behaviour that follows is the point. The party system works with
nothing configured. Carry that same turntable node into the house where
the Hub is running, and it sees a Controller that outranks it, yields, and
becomes an ordinary Producer.

### What has to be built

Firmware discovery today announces every interval and replies to probes,
but the receive path checks `is_probe()` and discards everything else. **A
node currently has no idea any other node exists.** A node-hosted
Controller needs a small peer table — id, address, roles, last seen — built
from the announcements already on the wire. A fixed array of sixteen is
more than a party system will hold.

### Consequences

- The Producer needs a destination list that can change while a stream is
  running. Today it is copied at start and never revisited, so incremental
  joining cannot work until that changes.
- Two Controllers can briefly coexist while precedence resolves, which
  means two streams could reach one Consumer. The same rule cast points
  need applies here: a Consumer plays one stream, accepting the first
  SSRC and ignoring others until it goes quiet. `oal_rtp_stats` already
  tracks SSRC, so the information is present.
- "Stream to everyone" is right for a party and wrong for a house. It is
  safe only because a Hub, when present, replaces it.
- A partitioned network could leave two Controllers running. For systems
  of this size that is accepted rather than solved.
- The Controller role stops implying a PC. Nothing in discovery, control
  or the RTP profile changes — this is a statement about where existing
  responsibilities are hosted, not a new mechanism.
