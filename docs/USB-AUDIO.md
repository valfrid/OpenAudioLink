# USB audio: two tracks

**Status: experimental, isolated, nothing scheduled on the main line.**

Deliberate side tracks — giving a node a USB audio path so it can reach
hardware that already exists. Recorded here rather than in `HARDWARE.md`
because they are explicitly *tests*: they may end in a verdict of "no", and
that is an acceptable outcome.

There are **two opposite arrangements**, and getting them the wrong way round
cost a round of analysis in this document's own history (see "A correction"
below). They need different hardware and different software, and they serve
different roles.

| | Track A — node as USB **host** | Track B — node as USB **device** |
| --- | --- | --- |
| Role it serves | **Consumer** | **Producer** |
| What it reaches | powered speakers, dongle DACs, headphone amps, USB mics | TVs, consoles, phones, PCs |
| Direction | node plays *into* USB hardware | node is played *into* over USB |
| Driver | `esp-uac2-host` v0.1.2 alpha | TinyUSB, bundled with ESP-IDF |
| ESP-IDF | 5.4 — a bump affecting every node | probably 5.3.1, unchanged |
| Prior art | a working JBL build, and a published fork | none found |

**Track A is the one that matters**, because a Consumer is what this project
mostly needs and because it reaches every interesting class of hardware at
once. Track B is a genuine but smaller idea, and it is a *source*, not a
speaker.

## Why either is worth doing

The existing Consumer output is an I²S DAC — a bare module giving line level,
right for feeding an amplifier and wrong for almost everything else. A USB
audio path opens a node to hardware that already exists, is already shielded,
and already solved its own analogue design.

Decision 8 frames alternative Consumers as additive: how a Consumer emits
audio is a hardware-profile property, not an architectural one. Nothing in
discovery, control, OTA or the RTP profile changes. Both tracks are new
input or output stages bolted under an unchanged pipeline, and that is the
whole reason they are affordable to try.

## A correction, since it shaped this document twice

An earlier revision claimed the JBL build worked by making the ESP a USB
*device* that the speaker hosted. **That was wrong**, and the way it went
wrong is worth keeping.

The project author's own write-up says the ESP "can act as a USB device" and
"present itself as a USB audio source" — but *source of audio* is not *USB
source role*, and the same write-up links a fork whose commits are entirely
about USB **host** support (`esp32s3-usbhost`, "tested and supported more
headsets"), and describes the speaker being put into the mode it uses when
plugged into a Mac. A speaker that appears to a Mac as an external sound card
is a USB **device**, and something has to host it.

So: **the speaker is the device, the node is the host.** Prose in a project
write-up lost to a fork's commit log and a check of what the hardware
actually does.

---

# Track A — the node as a USB host

## It works. 2026-08-19, first attempt.

An ESP32-S3 hosted the CX31993 dongle and a 1 kHz tone came out of it. No
soldering, no bench supply, and — on the second run — **no adapters**: the
dongle plugged straight into the XIAO's USB-C socket, with a USB-serial
adapter carrying power and the console.

`firmware/uacprobe` and its README are the record of how; this is what the
run settled.

**The two open questions are answered, and both the better way.**

- **Two clock sources did not stop it.** The dongle reports clocks `0x09`
  and `0x0A` where `esp-uac2-host` documents single-clock devices, and the
  driver selected `0x09` — the playback clock — without help. Its runtime
  state reads `Clock source ID: 9`, the rate set to 48 000 Hz and read back
  as 48 000 Hz, `clock valid: yes`. **The component's documentation is more
  conservative than its behaviour.**
- **Power reached the dongle through the board's own connector**, so the
  XIAO's `5V`/`VUSB` pin does reach the USB-C socket's VBUS. The silkscreen
  was telling the truth, and the diode this document worried about is not in
  that path.
- **And the CC question was never a question on this board.** Confirmed by
  running the dongle plugged *directly* into the XIAO's socket, with no
  adapters at all. Both ends present Rd, which normally means neither
  attaches — but CC only decides whether a port *switches VBUS on*, and the
  XIAO's VBUS is not switched by anything. It is wired to the 5 V rail. Feed
  that pin and the socket is live whatever CC says; the dongle sees VBUS and
  enumerates. **The analysis below is not wrong, it is about ports that have
  a vote, and this one does not.** A board with a real CC controller, or a
  device that waits for Rp before attaching, still needs it.

**The format is ours, unconverted.** Interface 1 alternate 2 was selected:
2 channels, 24-bit in 3-byte slots, endpoint `0x01`, `wMaxPacketSize 576`,
`bInterval 1`. The driver's own packet size came out at **288 bytes** —
exactly the 48 000 × 3 × 2 per millisecond predicted from the Windows dump,
in half the reserved packet.

**And the rate is right.** The write loop is paced by the device's
consumption. Over a fifteen-second run it moved 673 344 frames in 14.028 s —
**48 000.0 Hz**, to the resolution the counter can express, with no drift
visible at that scale. Fifteen seconds is not a measurement of drift; it is
a measurement of there being nothing obviously wrong.

**Confirmed, having been read off Windows first:** VID `3302` PID `336A`,
`TTGK Technology Co.,Ltd`, `CX31993+MAX97220 PRO`, UAC 2.0 with
`bcdADC 0200`, and **no feedback endpoint anywhere** — every endpoint
`Sync`. The drift story in this document stands as written.

**Three things worth knowing that only the hardware could say.**

1. **The parser truncation is real.** `AS interface limit reached (4),
   skipping iface 2 alt 2` — `UAC2_MAX_AS_INTERFACES` is 4 and this device
   has five alternates carrying endpoints. What was dropped is the
   **microphone's 24-bit** setting. Harmless for playback and not harmless
   for `LISTENING.md`, which wants that microphone.
2. **The feature unit's controls are not where they look.** Feature Unit 2
   reports `mute_ch_map=0x1 volume_ch_map=0x6`: mute on the master channel,
   volume on channels 1 and 2 and **not** on master. The descriptor summary
   prints `volume=no` for exactly that reason. Anything writing volume to
   channel 0 will write to a control that is not there.
3. **It is audible at its own default**, muted at nothing, with no volume
   written. `uacprobe` therefore reads those controls and deliberately
   leaves them alone.

## Five minutes, and what it does and does not prove

A 302-second run, uninterrupted: 14 529 408 frames, no write failure, no
transfer error, no stream restart, no disconnect. As a soak test that is a
real result and it is the first one this track has.

As a *drift* measurement it needs reading carefully.

**Per-second deltas are worthless here.** Every interval reads 48 096 frames
in 1 002 ms — 48 000.0000 Hz, exactly, every time. That is not precision, it
is quantisation: writes go out in 96-frame chunks, so one second cannot
resolve anything finer than 96 frames.

**Over the whole run it comes out 288 frames short of ideal**, which is
−20 ppm against a floor of about 7 ppm for a 288-second baseline. Marginally
above the noise, and small enough that one or two hiccups would account for
it entirely.

**But the number to distrust is the whole approach.** The SOF that paces the
device and the `esp_timer` this is measured against **both come from the
ESP's own crystal**. Measuring one against the other is measuring a clock
against itself, and it cannot see the node drifting away from the Hub.

What it *can* see is the device failing to follow our SOF — and it did not.
That is worth stating as the finding it is:

> **The dongle introduces no new clock domain.** A synchronous endpoint with
> no feedback behaved exactly as advertised: it consumed at the rate we
> clocked it, for five minutes, within the resolution of the instrument.
> Decision 12's clock authority survives — the node still owns its timing.

That is a better outcome than decision 8 assumed, and it shrinks the problem
rather than solving it. **The remaining question is not authority but
correction.** An I²S node follows the Hub by trimming its bit clock through
the APLL. A USB host node would have to steer its SOF instead, and whether
an S3 can do that — or how finely — is unknown and is now the open item.

**What is still unmeasured.** Drift against the Hub, which needs the Hub in
the picture. Hours rather than minutes. And whether anything audible happens
at the moment a correction would be applied. Those decide whether this
becomes a hardware profile or stays a demo.

## What it reaches

One capability, four classes of hardware:

- **Powered speakers with a USB audio input.** The big one, below.
- **Dongle DACs and headphone amps** — a shielded, finished output stage
  where the PCM5102A is a bare module. The CX31993 unit in `HARDWARE.md`.
- **USB audio interfaces**, which would make a **Producer** out of the same
  code in reverse.
- **USB microphones** — the awkward-to-source part of `LISTENING.md`.

## The use case that justifies the track

> **Any powered speaker with a USB audio input becomes an OpenAudioLink
> Consumer, with no analogue design at all.**

No DAC module, no amplifier, no enclosure, no driver, no soldered I²S. A node
becomes a small box on the end of a cable, plugged into a speaker somebody
already owns, and the path is digital end to end: RTP L24 → USB → the
speaker's own converter. Against a house full of Bluetooth speakers that can
each be fed by exactly one phone at a time, that is a much larger prize than
a headphone amplifier on a desk.

The dongle case, by contrast, is 120 SEK to improve on a 28 SEK PCM5102A —
marginal, and always was. The dongle's value now is as a **known quantity to
develop against**: its descriptors are dumped and recorded, so it is the
device to get working first, not the device worth having.

```
today     RTP L24 -> jitter buffer -> drift correction -> I2S -> PCM5102A
track A   RTP L24 -> jitter buffer -> drift correction -> UAC host -> speaker
```

Everything left of the output stage is untouched. That is decision 8's claim,
and this is a direct test of it.

## Power, which is less of a problem than it looked

A USB host normally powers its peripheral, and that is the shape of the
problem for a bus-powered dongle: 100 mA the ESP board must source, through
a connector wired to receive power rather than send it.

**A battery speaker probably inverts this, and USB-C is why.** Data role and
power role are independent in USB-C — a device can be the data *peripheral*
while being the power *source*. A speaker that also works as a power bank is
built to source VBUS, so the likely arrangement is:

| | Data role | Power role |
| --- | --- | --- |
| Speaker | device (UFP) | **source** |
| Node | **host** (DFP) | sink |

If that holds, the speaker powers the node over the same single cable, and
the whole VBUS problem below simply does not arise for that case. **This is
inference from how USB-C separates the two roles, not something measured**,
and it is the cheapest thing on the test list to check: plug it in and see
whether the ESP powers up.

The dongle case still needs the rig below, because a bus-powered dongle
sources nothing.

## The electrical question, for anything that does not power itself

- **Does 5 V reach the connector's VBUS pin?** On boards wired as a USB
  *device*, VBUS usually enters through a Schottky diode into the 5 V rail.
  A diode conducts one way, so feeding that rail from a separate supply does
  not push current back out to the connector. Confirm with a multimeter.
- **Do the CC pins say "host"?** A USB-C device presents Rd; a host presents
  Rp. Development boards are wired as devices, and so is the dongle. Two
  devices facing each other never attach. (A speaker that toggles roles may
  resolve this by itself — hence the free test above.)

**The clean way around both is to not use the USB-C connector at all.** The
S3's native USB is GPIO19 (D−) and GPIO20 (D+). Wiring those to a plain
**USB-A receptacle**, with 5 V from a separate supply and a common ground,
removes the problem rather than working around it: USB-A has no CC pins, so
there is nothing to negotiate.

**The adapter in the dongle's box completes it.** The unit ships with a
USB-C-receptacle-to-USB-A-plug adapter, and such an adapter is required by
spec to present **Rp** (56 kΩ to VBUS, the "default USB power" advertisement)
on its CC pin — precisely the signal the dongle needs to attach as a device.
So: A receptacle on the ESP → included adapter → dongle, nothing to buy.

It does not change what the device *reports* — the adapter is passive on
D+/D−, so VID/PID, descriptors, UAC version and speed capability are
properties of the dongle either way — and it supplies no power. A wrinkle
worth knowing rather than worrying about: USB-IF prohibits
C-receptacle-to-A-plug adapters and they are sold by the million anyway. If
enumeration fails, the adapter is a cheap thing to substitute before
suspecting firmware.

**The console objection stands.** A board with separate UART and USB-OTG
ports keeps the heartbeat diagnostics and USB flashing while the native port
does host duty. On a single-port board this experiment costs both, so do not
do it to a node that is in service.

## What the dongle's descriptors already settled

From the dump in `HARDWARE.md` — CX31993 + MAX97220, VID `0x3302` PID
`0x336A` — and from reading `esp-uac2-host`:

| Question | Answer |
| --- | --- |
| Does the device run at full speed? | Yes — Device Qualifier present |
| Is there a UAC 1.0 fallback? | No — `bcdADC 0x0200` at both speeds |
| Is 12 Mbit/s enough? | Yes — 288 of 576 reserved bytes per frame |
| Is our wire format available? | Yes — 48 kHz / 24-bit / stereo, unconverted |
| Does a host driver exist? | Yes — `esp-uac2-host` v0.1.2 |

## The two open questions

### Two clocks where the driver documents one

The dongle exposes clock source `0x09` (speaker path) and `0x0A` (microphone
path). `esp-uac2-host` v0.1.2 states support for single-clock UAC2 devices.
If only playback is used, only `0x09` should matter — but the driver must
select and program the clock belonging to the interface it streams to, and it
was written against devices where there was only one to pick.

### The dongle has no feedback endpoint

`HARDWARE.md` credits `esp-uac2-host` with resolving drift the way USB audio
normally does — an asynchronous device reports its true rate through a
feedback endpoint and the host adapts. **This dongle has no feedback
endpoint.** Every audio endpoint is `bmAttributes 0x0D`: isochronous, SyncType
*Synchronous*. It slaves itself to the host's SOF and expects one packet per
frame.

For a Consumer that is arguably the better arrangement — decision 12 has the
node owning its timing, and a synchronous device follows rather than fights
it. But drift correction then runs through the S3's SOF generator rather than
an APLL-trimmed bit clock, and how finely an S3 can steer SOF is unknown.

**A speaker will probably differ**, and in the direction that suits the
driver: a battery unit with its own crystal is a good candidate for
asynchronous operation with a feedback endpoint, which is exactly the case
`esp-uac2-host` was built for. Two devices, two clocking stories, and both
have to be handled before this is a profile rather than a demo.

## The order to test in

1. **Plug a XIAO into the speaker with a plain C-to-C cable.** No firmware
   change, nothing built. Does the ESP power up? That single observation
   tests the power-role inference above and costs a cable. The prior art
   reports doing exactly this, so it should work — confirm it rather than
   inherit it.
2. **Dump the descriptors of every USB audio device in the house.** Free, no
   ESP, USB Device Tree Viewer on the laptop. The dongle is done; a speaker
   is the one that matters. Full-speed support, UAC version, and synchronous
   versus asynchronous decide how much of the rest applies.
3. **Multimeter on the board**, for the dongle case only: is there 5 V at the
   connector's VBUS pin with a separate supply attached?
4. **Build the USB-A rig** if step 3 says no — GPIO19/GPIO20 to a USB-A
   receptacle, 5 V and ground from the separate supply, reached by the
   dongle's own C-to-A adapter. Nothing to buy.
5. **Enumerate, and nothing more.** IDF 5.4, the host stack, no audio: does
   the device attach, and do its descriptors match what Windows reported?
   First real go/no-go.
6. **Open the stream.** For the dongle: 48 kHz, 24-bit, stereo. Answers the
   two-clock question.
7. **Play a tone.**

**Steps 5–7 are built.** `firmware/uacprobe` is a standalone application
that does exactly those three and nothing else — no Wi-Fi, no RTP, no
`oal_*` components, ESP-IDF 5.4, `esp-uac2-host` pinned to a commit. It
prints the UAC version, the clock count, the sync type and feedback endpoint
of every alternate setting, and whether 48 kHz / 24-bit / stereo is offered,
then plays a 1 kHz sine and counts frames once a second. Its README covers
the wiring, the flashing procedure and how to read the output. Flashing over
the board's USB-C connector still works in download mode; **the console has
to move to UART**, because USB-Serial/JTAG and USB-OTG share one PHY on the
S3 and host mode takes it.
8. **Measure the drift.** Free-run for hours against the Hub's clock, the way
   run 22 in `LINK-MEASUREMENTS.md` was run — plus, for a speaker, its added
   latency against an I²S node on the same cast point. A fixed offset is a
   number to compensate for; a varying one breaks multi-room sync.
9. **Listen**, only after the numbers.

Steps 1–2 need no firmware at all and can be done in an evening.

## A usability finding to expect

The prior art's speaker enters USB audio mode only by holding its Play button
while the cable is inserted, with an audible confirmation and the Bluetooth
indicator going dark. Per power cycle, and manual.

A Consumer that needs a human to hold a button every time the speaker is
switched on is not set-and-forget, and that is worth recording as a real
limitation of the speaker case even if everything electrical works. It may be
avoidable by leaving the speaker permanently powered; that is a thing to
find out rather than assume.

---

# Track B — the node as a USB device

The opposite arrangement, and it is a **Producer**, not a Consumer. The node
presents itself to a computer, phone, TV or console as a USB sound card;
whatever is plugged into it plays into OpenAudioLink and out to the house.

This is a real idea — TV audio into multi-room is a use case the current
Producer set does not cover, and it needs no ADC, no PCM1808, and no analogue
stage. It is also clearly second: the Hub already captures Windows audio via
WASAPI, so the PC case is covered, and the remaining cases are TVs and
consoles.

What is known:

- **TinyUSB ships with ESP-IDF**, so there is no alpha third-party component
  and probably no version bump. Whether the UAC device support arrives at our
  pinned 5.3.1 is the one thing to confirm — it comes as a managed component,
  so the component version may matter more than the IDF version.
- **We write the descriptors**, so the two-clock and feedback-endpoint
  questions above do not arise. We declare 48 kHz, 24-bit, stereo — our wire
  format, unconverted.
- **The console goes**, same as track A on a single-port board.
- **Clock ownership inverts**, and this is the interesting part. As a device
  the node does not own frame timing; the attached host's SOF does. But on an
  isochronous **IN** endpoint the *device* chooses how many bytes to send each
  frame, so the node sends 48 samples in most frames and 47 or 49 occasionally
  to track the Hub. Drift correction becomes rate modulation instead of clock
  trimming, and decision 12's contract survives in a different form.

Nothing here has prior art found so far, which is part of why it is second.

---

# How both tracks stay isolated

Track A's driver wants **ESP-IDF 5.4** where this project builds on 5.3.1,
and track B may need a component move of its own.

An IDF bump is not a local change. Both `.github/workflows/ci.yml` and
`.github/workflows/release.yml` pin `esp_idf_version: v5.3.1`, and they build
the firmware every node in the house runs. A bump landing on the main line
rebuilds the working Consumer, Producer and provisioning firmware on a
toolchain nothing has been tested against, to enable a feature no node uses.

So:

1. **Its own branch**, and it does not merge until it has a verdict.
2. **The IDF pin does not move on the main line.** If an experiment succeeds,
   bumping IDF becomes its own piece of work with its own regression pass on
   real hardware — not a side effect of this.
3. **No OTA channel mixing.** Experimental images do not go into the Hub's
   firmware store next to images meant for speakers people use. A brick here
   should cost a cable, not an evening.
4. **One node, and not one in service.** Both tracks can cost a node its USB
   console, so this rule has teeth: the two XIAO nodes in service stay on
   released firmware and keep their flashing port.
5. **A negative result is a deliverable.** If the answer is no, it gets
   written down here with the measurement that produced it, the way the
   `--volume-ctrl fixed` dead end is written down in decision 14.

# Prior art

- **A DIY AirPlay adapter for a JBL Charge 6**, r/esp32, supplied 2026-08-18
  and read from the author's own description rather than the thread, which is
  unreachable from this build environment. A XIAO ESP32S3 chosen for its
  native USB, plugged into the speaker with a USB-C cable, built on
  `rbouteiller/airplay-esp32` with the decoded-PCM path redirected away from
  the I²S driver into a USB audio stream. Reported: the descriptors had to
  match what the speaker expected exactly; the board runs hot enough to want
  a heatsink; audio hiccups traced to Wi-Fi throughput. The write-up's own
  account of which end is host is inconsistent — see "A correction" above.
- <https://github.com/wasdwasd0105/airplay-esp32-usb> — a fork of that
  AirPlay firmware adding **USB host** support: commits reference a runtime
  FIFO carve, per-device sample rate, and an `esp32s3-usbhost` sdkconfig,
  tested against headsets. This is track A's shape and the closest thing to a
  worked example of it on an ESP32-S3.
- <https://github.com/Averyy/esp-uac2-host> — the UAC 2.0 **host** driver
  behind track A. v0.1.2, single-clock devices, wants ESP-IDF 5.4.
- <https://github.com/rbouteiller/airplay-esp32> — AirPlay receiver for
  ESP32/S3/C5, ESP-IDF 5.x, I²S out to a PCM5102A, with a PTP-synchronised
  timing engine that holds early frames and drops late ones. Not our protocol,
  but the closest published relative of decision 12's playout contract.

A note on Wi-Fi, since the prior art blames it for hiccups: run 22 in
`LINK-MEASUREMENTS.md` is 6h49m at zero loss, and the RSSI curve explains
why. Whatever else goes wrong here, that is a problem this project has
already measured its way out of.
