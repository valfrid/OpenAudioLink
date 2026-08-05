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

*Settled by decision 13: the source resamples. 48 kHz is the only rate on
the wire.*

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
**Status:** implemented and verified on hardware, 2026-08-02 (firmware 0.8.3).

Both deployments, on two XIAO ESP32-S3 nodes and a Windows Hub:

- *House.* Both nodes elected the Hub, the turntable dropped a claim it was
  already holding, and the speaker was told `standby` — correct, because
  the Hub knows about rooms and nobody had pressed play.
- *Party.* With the Hub stopped, the turntable claimed the role within one
  announce interval, the speaker joined it, and the speaker appeared in the
  turntable's destination list. Nothing configured it: asking is what put
  it there.

Finding this took an evening, and almost none of it was the handshake. The
node-to-Hub direction had never been exercised in the project's life — the
Hub polls, pushes firmware and starts streams, all of it outbound — so a
VPN subnet route quietly NATing the local network had been latent
throughout and surfaced the moment something first needed to reach the Hub.
See `protocol/DISCOVERY.md`.

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

## 10. A Consumer's channel profile is a node setting, not a stream format

**Date:** 2026-08-01
**Status:** decided; nothing implemented. The setting belongs in provisioning;
the playout it controls waits on the DAC.

### Decision

The RTP profile is stereo and stays stereo. Every Consumer in a cast point
receives the same sequence numbers, timestamps, SSRC and samples, because
byte-identical replication is what lets several speakers stay in step.
Sending a different payload to different destinations would trade that
away for airtime.

Which of those two channels a node actually plays is decided at the node:

| Profile  | Plays                        | Nodes | Physical arrangement          |
| -------- | ---------------------------- | ----- | ----------------------------- |
| `stereo` | both, as they arrive         | 1     | stereo DAC, two PA, two boxes |
| `mono`   | (L+R)/2                      | 1     | one PA, one speaker           |
| `left`   | L only                       | 1     | one half of a pair            |
| `right`  | R only                       | 1     | the other half                |

### Prefer `stereo` to a `left`/`right` pair

A single node's two channels leave one DAC on one clock and cannot drift
from each other. Two nodes playing left and right are two crystals, and
drift between the halves of a stereo image is far more audible than drift
on a single speaker — the image wanders rather than the pitch.

So `left`/`right` is for speaker positions too far apart to cable to one
box. It is not the ordinary way to build stereo, and choosing it buys a
synchronisation problem that `stereo` does not have.

### Two things the implementation must get right

**Write the chosen signal into both I²S slots** for `mono`, `left` and
`right`. The DAC then carries it on both outputs, so one node drives one
speaker or two identical ones with no further profile, and no assembly can
wire the silent output by mistake.

**`mono` is (L+R)/2, not L+R.** Two channels near full scale sum past full
scale and clip. The halving costs 3 dB, which the amplifier makes back;
clipping is not recoverable.

### Consequences

- This is decision 8's shape again: how a Consumer emits audio is a profile
  property, not a role property. Nothing in discovery, control, OTA or the
  RTP profile changes.
- The setting is decided when a speaker is installed, so it belongs beside
  the role radios in the provisioning portal and in NVS next to the roles.
- Analog summing is ruled out. Both channels are present digitally, and
  tying two DAC outputs together makes them drive each other — resistors
  lose level and add noise, an op-amp stage adds a part, and neither buys
  anything over one line of arithmetic before the samples reach the DAC.
- A whole-cast-point mono profile, where every speaker in a room is mono
  and the Producer sends 480-byte payloads instead of 1440, is a real
  airtime saving and a different decision from this one. Recorded as an
  option, not planned: it is the kind of per-use-case profile that only
  earns its complexity once four consumers are measured.

---

## 11. Spotify Connect is the first provider source, and the Hub resamples

**Status:** implemented 2026-08-02, **verified end to end on hardware
2026-08-05** — Spotify Connect through librespot, resampled at the Hub,
out as RTP to a XIAO ESP32-S3 driving a PCM5102A into powered speakers,
for 46 minutes with six packets lost in half a million
(`LINK-MEASUREMENTS.md` run 17). See `CAST-POINTS.md` for the model and
`LIBRESPOT.md` for how it is run.

### The decision

A cast point's receiver is one **librespot** process per cast point, named
after it, owned by the Hub, and **the receiver drives the stream**: audio
starting to flow is what starts RTP to that cast point's speakers.

Spotify's 44.1 kHz is converted to the profile's 48 kHz **at the Hub**, by
an exact 147:160 polyphase FIR.

### Why librespot first

Three routes were weighed in `CAST-POINTS.md` — Spotify Connect, AirPlay 2
and UPnP — and the model is protocol-independent, so the question was only
which adapter to build against first. Spotify Connect wins on the one
criterion that matters here: it is the source this house actually plays
music from. Building a second adapter later is adding an adapter, not
changing the design.

Being a Cast target was never on the table. Google licenses senders, not
receivers; certified "Chromecast built-in" speakers run Google's own
firmware, and the applications that matter require device attestation, so
reverse-engineered receivers do not work with them. The user asked to keep
the *experience* — pick a room from a phone — and that survives losing the
protocol entirely.

### Why the binary is not shipped

Whether a particular reimplementation is licensed for a particular service
is the operator's decision. The Hub locates and manages a binary they
supplied. That keeps the licensing question where it belongs without making
anyone configure anything: install a binary, and the Hub does the rest. A
Hub without one logs a line and carries on.

### Why the Hub resamples, rather than the node

Two rates in one house means two drift problems instead of one. Doing it at
the Hub keeps a single clock domain across the whole system, and the CPU it
costs is about six million multiplies a second — nothing on a PC, and a
real burden on an ESP32-S3 that also has an I²S deadline to meet.

Doing it *well* was cheap enough not to compromise: the ratio is exact, so
this is a polyphase FIR rather than an interpolator chasing a drifting
phase. Measured against an ideal sine the worst error is about -110 dB and
the response is flat to 20 kHz. The resampler should not be the thing
anyone can hear, and at that margin it is not.

### Consequences

- The Hub becomes a Producer in ordinary use, not only for diagnostics.
  This makes the Hub-to-node hop — still unmeasured, and now the path that
  will carry almost all the audio — the most important gap in
  `LINK-MEASUREMENTS.md`.
- The Hub sends one stream at a time, so two cast points cannot play
  different music at once. One Spotify account already plays to one device
  at a time, so this only surfaces with two accounts.
- Stopping a Spotify-fed cast point from the Hub's page stops the sending,
  not the music: the receiver is still playing, so it resumes within a
  tick. Pausing belongs on the phone. That is a consequence of the receiver
  driving the stream, which is the right way round.
- Volume set on the phone reaches the speakers for free, because librespot
  applies it before the pipe. That was one of the two things
  `CAST-POINTS.md` named as needed for parity rather than mere function.
- A running stream's destination list had to become mutable, so a speaker
  that reboots mid-song rejoins without the rest of the room skipping —
  the same shape the firmware producer already carries.

---

## 12. NTP is not the tool for playout coordination

**Date:** 2026-08-05
**Status:** decided; nothing implemented, and nothing needs to be until two
separate nodes are asked to play one stereo image.

### Decision

No NTP client, on the Hub or on a node. When playout across several nodes
has to be coordinated, it is done with three mechanisms of our own, and
they are three separate problems that get confused because they all sound
like "time".

### Why not NTP

**The resolution is an order of magnitude short.** NTP on a LAN settles
within 1–10 ms. Two speakers making one stereo image need well under 1
ms — Snapcast and AirPlay 2 both target sub-millisecond, and AES67 reaches
for PTP because it wants sub-microsecond. A millisecond of error is a foot
of path difference, which moves the image across the room.

**It answers the wrong question.** NTP tells a device what time it is.
Nothing here cares what time it is. What matters is *how far your clock is
from mine*, and being wrong together is harmless as long as everyone is
wrong by the same amount. We need a **common** clock, not a **correct**
one, and a common clock is much cheaper — it needs no reference, no
internet, and no server that is right about anything.

That also means a house with no internet, or a party deployment with no
router at all (decision 4), synchronises exactly as well as a connected
one. Depending on NTP would have quietly made that untrue.

### What gets built instead

1. **Offset measurement over our own channel.** NTP's four-timestamp
   arithmetic is sound; it is the daemon, the hierarchy and the discipline
   loop that are the wrong size. Send a request, note four timestamps, keep
   the sample with the shortest round trip, repeat often. On a quiet LAN
   this lands well inside the budget, and it rides the control channel that
   already exists.
2. **A playout contract.** The Producer says *frame N is played at epoch +
   N/48000 + delay*, and every Consumer holds frame N until its own
   estimate of that instant. RTP timestamps are already the frame index, so
   half of this is on the wire today. The missing standard piece is RTCP
   sender reports, which map an RTP timestamp to a wall clock — not
   implemented, and the natural place to put the epoch.
3. **Rate matching, separately.** Offset is a one-time alignment; drift is
   the two crystals diverging afterwards, and no amount of offset
   measurement fixes it. That is trimmed at the node by pulling the I²S
   clock a few parts per million through the APLL, steered by the ring's
   own fill level.

Keeping (1) and (3) apart matters. A servo that tries to correct both with
one control ends up chasing measurement noise with the sample clock, which
is audible.

### Two ways to do the rate matching, and Espressif ship the other one

Rate matching has two shapes, and they are worth naming because the second
is a component we could adopt rather than write:

- **Trim the clock.** Pull the I²S clock a few parts per million through
  the APLL until the ring stops drifting. Costs no CPU and touches no
  samples: at a matched rate the audio is still bit-exact.
- **Resample the audio instead.** Leave the clock alone and convert the
  incoming stream by the tiny ratio it is off by — 1.000004 and slowly
  varying. This is asynchronous sample rate conversion, and it is what you
  reach for when you cannot control the playback clock.

**`espressif/esp_asrc`** (registry v1.0.1; promoted to an official module in
ESP-GMF v1.0) does the second. Rates at integer multiples of 4000 or
11025 Hz up to 192 kHz, 16/24/32-bit signed interleaved plus 8-bit
unsigned, any channel count, and a hardware/software cooperative design —
it drives an ASRC peripheral on chips that have one and falls back to an
optimised software path on those that do not, selected by
`ESP_ASRC_PERF_TYPE_AUTO` / `HW_ONLY` / `SW_SPEED` / `SW_MEMORY`.

**The clock trim is still the right first choice here**, for two reasons
that are specific to this system rather than general:

- **We own the playback clock.** Both candidate DACs are slaves — the
  PCM5102A runs its PLL off the bit clock with SCK grounded, and the
  MAX98357A never had a master clock. The ESP32 generates the rate, so the
  cheap mechanism is available. ASRC exists for the case where it is not.
- **The cost lands on every consumer, continuously.** ASRC on the playout
  path is arithmetic on every sample for as long as music plays, competing
  with an I²S deadline, on an S3 that as far as I know has no ASRC
  peripheral — so the software path, whose CPU cost is unpublished. The
  APLL trim costs a register write per correction.

Three things to check before treating it as the fallback, none of which I
could read from here — `components.espressif.com` is unreachable from this
environment, so the above is from the registry summary and not from the
API docs:

1. **Whether the ratio can vary at runtime under an external error
   signal.** A drift servo needs to nudge it continuously from the ring's
   fill level. "Asynchronous" in some ASRC APIs only means the two rates
   need not share a clock, with the ratio fixed at open.
2. **Software-path CPU on an S3**, measured, alongside playout.
3. **The licence.** `esp_audio_effects` is "Espressif Modified MIT" — must
   be used with Espressif products, redistribution for non-Espressif
   products prohibited, shipped prebuilt. Assume the same here until
   checked, with the same conclusion: opt-in behind Kconfig, never in the
   default build of an MIT-licensed project.

### What exists today, honestly

Nothing that synchronises. What exists is the instrumentation for it:

- `oal_playout`'s ring **absorbs** drift and **counts** it in both
  directions — `silence_frames` when it starves, `dropped_frames` when it
  overflows — and its own header says it does not correct it. Those two
  counters are the error signal a servo would use.
- RTP timestamps carry the absolute frame index, so packets are already
  labelled with what they mean rather than when they arrived.
- `esp_timer_get_time()` gives a microsecond monotonic clock on every node.
- The peer table and the Controller election give a place to put the epoch
  and something to agree with.

Right seams, no machinery. And this is better designed against a real pair
of speakers audibly wandering than in the abstract, so it stays unbuilt
until there is a pair to listen to.

### Consequences

- **The common case needs none of it.** Decision 10 already says a single
  node's two channels leave one DAC on one clock and cannot drift. One
  stereo node, or two MAX98357A on one node's I²S bus, is synchronised by
  construction. The demanding case is two nodes heard as one image, which
  decision 10 recommends against for exactly this reason.
- Several nodes playing the *same* mono content in different rooms are a
  much easier case: a few milliseconds apart in different rooms is
  inaudible, and only becomes audible where the rooms open into each other.
- Until this is built, a `left`/`right` pair is a thing the firmware will
  do and the system does not promise to keep in step.

---

## 13. One wire rate: 48 kHz, with the rate still carried

**Date:** 2026-08-05
**Status:** decided. Settles the open question left in decision 3.

### Decision

**48 kHz is the only rate on the wire.** Anything that is not 48 kHz is
resampled before it becomes RTP, at whichever end has the CPU — today
always the Hub (decision 11).

**The rate stays a field, not a constant, in the protocol.** SDP already
says `L24/48000/2`, and `AudioStreamFormat` already takes the rate as a
parameter. Nothing is hard-coded away; a second rate would be a capability
negotiation, not a wire-format break. What is being decided is that no
receiver is required to accept a second rate, and none will be offered one.

### What supporting 44.1 kHz would actually buy

Less than it first appears, because the obvious argument — *avoid
resampling, keep the path pure* — does not survive measurement. The Hub's
converter is an exact 147:160 polyphase FIR: worst-case error about
-110 dB, flat to 20 kHz. That is 30 dB below the noise floor of the 16-bit
master the music came from. **The resampler is not audible, so removing it
is not a fidelity gain.**

What is real:

- **Sources that cannot afford to resample.** Decision 3's internet-radio
  node is the case: an ESP32 already doing HTTP receive, Vorbis or AAC
  decode, packetisation and unicast replication, on one chip sharing one
  radio. 64 taps × 44 100 × 2 channels is around six million multiplies a
  second on top of that, with an I²S deadline to miss. A future S/PDIF or
  TOSLINK input from a CD player is the same shape and worse — the source
  is locked at 44.1 kHz and there is no PC anywhere in the path.
- **About 8 % less airtime.** 2.12 Mbit/s against 2.30. Marginal, and it
  is not why anyone would do it.

### What it would cost

**The packet arithmetic stops being round, and not by choice.** An
Ethernet frame leaves 1460 bytes for audio after IP, UDP and RTP headers,
which is 243 frames of L24 stereo. At 44.1 kHz a whole number of frames
needs a packet time that is a multiple of 10 ms, and 10 ms is 441 frames —
2646 bytes, which fragments. So **there is no integer-millisecond packet
time at 44.1 kHz that fits in an Ethernet frame.** The choices are a
fractional packet time (220 frames is 4.9887 ms — legal RTP, since the
timestamp counts frames, but every "5 ms", "200 packets per second" and
"60 ms of ring" in the code and the measurements becomes an
approximation), or fragmenting every audio packet. This is why AES67
mandates 48 kHz and does not require 44.1 at all; it is not an oversight in
the standard.

**Two clock domains in one house.** Decision 12 has one synchronisation
problem to solve. Two rates means solving it twice, with a separate APLL
trim state and a separate set of constants per rate, and a node that
switches rate mid-session throws away its ring and re-locks its I²S clock —
a click, and a fresh drift transient, every time the source changes.

**A failure mode the user can build.** Two speakers in one room, one of
which only does 48 kHz, is a room that cannot play a 44.1 source together.
Either discovery grows a per-node rate capability and cast points learn to
refuse mixed sets, or someone hears it and has no idea why.

**The clock is not free on the ESP32 either.** 48 kHz divides exactly from
the chip's audio PLL; 44.1 kHz generally does not, which is why ESP-IDF's
I²S documentation points at the APLL for it. A 44.1 stream therefore lands
the node on the same clock resource decision 12 wants for drift trimming.
The residual error is worth measuring before anyone assumes it is small.

**The test matrix doubles.** Link measurements, loss shape, the pattern
source and its host tests, playout ring sizing, probation windows — all of
it keys off 240 frames and 48 000 today.

### The judgement

One rate, resample at the edge. The cost of the second rate is paid by
every node, in the part of the system that is hardest to get right, to save
CPU on one class of source that does not exist yet — and to remove a
conversion nobody can hear.

If a fixed-44.1 source with nowhere to resample does eventually appear, the
cheap way in is not a second house-wide rate. It is either that node
resampling with a shorter filter (a worse converter is still inaudible next
to a Vorbis stream), or permitting a non-48 rate **only on a cast point
with exactly one consumer** — which is precisely the case where a second
clock domain synchronises with nothing and costs nothing.

### If a node ever does have to resample

Espressif ship one, so this would not start from nothing:

**`espressif/esp_audio_effects`** — component registry, v1.2.1 as read;
source under `espressif/esp-adf-libs`. Its **rate conversion** module covers
4–192 kHz plus integer multiples of 4000 and 11025 Hz, in s16, s24 and s32,
interleaved or planar, on ESP32 through S3, C2/C3/C5/C6 and P4. So
44 100 → 48 000 is in range, and s24 matches our wire format without an
intermediate conversion.

Two things to check before leaning on it, because both decide the question
rather than colour it:

- **No published CPU figures**, which is the only number that matters here.
  Decision 3's internet-radio node is CPU-bound before the resampler
  exists; "Espressif ship a resampler" is not evidence it fits alongside a
  Vorbis decode and an I²S deadline. Measure on hardware, against a real
  decode, before designing around it.
- **The licence is not open source.** It is an "Espressif Modified MIT":
  the software *must be used in conjunction with Espressif products*, and
  redistribution for use with non-Espressif products is prohibited. Shipped
  as a prebuilt library, so there is nothing to read and nothing to
  host-test — where our own `RationalResampler` was validated on a PC
  before any hardware saw it.

That makes it the same shape as decision 11's librespot question: fine to
depend on for firmware that by definition only runs on an ESP32, but it is
a non-free dependency and should arrive as an opt-in component behind a
Kconfig flag, not quietly in the default build of an MIT-licensed project.

### Consequences

- Decision 3's open question is closed: an internet-radio node resamples,
  or hands its stream to something that can. It does not advertise 44.1.
- `AudioStreamFormat.Validate()` already rejects 44.1 kHz at 5 ms, for the
  whole-frames reason above. That is the decision enforced by accident, and
  it can stay.
- The firmware's compile-time `OAL_RTP_SAMPLE_RATE` and
  `OAL_RTP_FRAMES_PER_PACKET` are correct as constants and should stay
  constants. `oal_playout` already takes its rate from configuration, which
  is enough seam for a future experiment.
- Spotify lossless, if it ever arrives, is 44.1 kHz and gets the same
  treatment as Spotify lossy: converted at the Hub, still bit-transparent
  to well below audibility.
