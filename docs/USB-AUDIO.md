# USB audio: two tracks

**Status: experimental, isolated, nothing scheduled on the main line.**

This describes deliberate side tracks — giving a node a USB audio path so it
can reach hardware that already exists. They are recorded here rather than in
`HARDWARE.md` because they are explicitly *tests*: they may end in a verdict
of "no", and that is an acceptable outcome.

There are **two opposite arrangements**, and conflating them wasted a day of
analysis. They need different hardware, different software, and one of them
is enormously cheaper than the other.

| | Track A — node as USB **device** | Track B — node as USB **host** |
| --- | --- | --- |
| Who powers whom | the peripheral powers the node | the node powers the peripheral |
| What it reaches | powered speakers with a USB audio input | dongle DACs, headphone amps, USB mics, interfaces |
| Board | **a XIAO ESP32S3 we already own** | a new board with a spare console port |
| Wiring | a USB-C cable | a soldered USB-A receptacle and a bench supply |
| Driver | TinyUSB, bundled with ESP-IDF | `esp-uac2-host` v0.1.2 alpha |
| ESP-IDF | probably 5.3.1, unchanged | 5.4 — a bump affecting every node |
| Descriptors | **we write them** | we have to satisfy someone else's |
| Proven by | a working build, see prior art below | nobody yet |

**Track A is the one to try first**, and it was invisible until the prior art
below made it obvious. Track B is still worth having — it reaches an entirely
different class of hardware — but it is the expensive one and it can wait.

## Why either is worth doing

The existing Consumer output is an I²S DAC — a bare module giving line level,
right for feeding an amplifier and wrong for almost everything else. A USB
audio path opens the node to hardware that already exists, is already
shielded, and already solved its own analogue design.

Decision 8 frames alternative Consumers as additive: how a Consumer emits
audio is a hardware-profile property, not an architectural one. Nothing in
discovery, control, OTA or the RTP profile changes. Both tracks below are new
output stages bolted under an unchanged pipeline, and that is the whole
reason they are affordable to try.

---

# Track A — the node as a USB device

## What it is

The node presents itself to a powered speaker as a **USB audio source**, the
way a laptop or phone does. The speaker is the USB host. One USB-C cable
carries the audio and powers the ESP.

```
today  RTP L24 -> jitter buffer -> drift correction -> I2S -> PCM5102A -> amp
track A  RTP L24 -> jitter buffer -> drift correction -> TinyUSB UAC -> speaker
```

Everything left of the output stage is untouched. That is decision 8's claim,
and this is the cheapest possible test of it.

The prize is not a better DAC. It is:

> **Any powered speaker with a USB audio input becomes an OpenAudioLink
> Consumer, with no analogue design at all.**

No DAC module, no amplifier, no enclosure, no driver, no wiring, no soldered
I²S, no enclosure design, no separate supply. A node becomes a small box on
the end of a cable, plugged into a speaker somebody already owns, and the
path is digital end to end. Against a house full of Bluetooth speakers that
can each be fed by exactly one phone at a time, that is a much larger prize
than a headphone amplifier on a desk.

## Why it costs almost nothing

Every blocker recorded against track B dissolves here, and it is worth being
explicit about why rather than just cheerful:

- **VBUS is not our problem — it is the speaker's.** A USB host powers its
  peripheral, and in this arrangement we are the peripheral. No bench supply,
  no diode question, no soldering.
- **The CC question answers itself.** The XIAO's USB-C is wired as a device
  (Rd on CC). A speaker that supports both roles is a **dual-role port**: it
  toggles until it finds a match, sees our Rd, and settles on host. That is
  also why the same speaker works plugged into a Mac — the Mac presents Rp,
  so the speaker takes the device role instead. A plain C-to-C cable, and
  USB-C's role negotiation does the rest. **This is the thing that makes the
  whole track free**, and it only works because the speaker is dual-role.
- **The board is one we already have.** XIAO ESP32S3, two in service and
  the target the firmware already builds for. Native USB, no host shield.
- **TinyUSB ships with ESP-IDF.** No alpha third-party component. Whether the
  UAC device support we need is available at our pinned 5.3.1 is the one
  thing to confirm — it arrives as a managed component, so the component
  version may matter more than the IDF version. **Verify; do not assume.**
- **We write the descriptors.** Track B's two-clock and feedback-endpoint
  questions exist because we are trying to satisfy hardware somebody else
  designed. Here we declare the interface: 48 kHz, 24-bit, stereo, our wire
  format, unconverted.

## What it costs, honestly

- **The console goes.** TinyUSB device mode takes the native USB peripheral,
  so `CONFIG_ESP_CONSOLE_USB_SERIAL_JTAG=y` must go with it. On a XIAO — one
  USB-C port — that means **no USB flashing and no heartbeat console on that
  node**. Recovery is OTA, or a USB-UART adapter on the XIAO's UART pins.
  This is the real price of track A and it should be paid deliberately: do
  not do this to a node that is in service.
- **Clock ownership inverts.** See below. This is the interesting part.
- **Heat.** The prior art reports the XIAO running hot enough to want a
  heatsink, and that was AirPlay decode plus USB. Ours is RTP decode plus
  USB, in a small sealed box on a shelf. Worth measuring early.
- **The speaker may need a ritual.** The prior art's speaker enters USB audio
  mode only by holding its Play button while the cable is inserted, with an
  audible confirmation. Per power cycle, and manual. A Consumer that needs a
  human to hold a button every time the speaker is switched on is not
  set-and-forget, and that is a usability finding worth recording even if
  everything else works.
- **Descriptors must match exactly.** The prior art is explicit that sample
  rate, bit depth and channel count had to match what the speaker expected.
  Freedom to write our own descriptors is not freedom to pick anything.

## The clock question, which is the architectural one

As a USB device the node no longer owns its frame timing — the host's SOF
does. Decision 12's playout contract has the node owning its playout clock
and correcting drift against the Hub, and an I²S node does that by trimming
its bit clock through the APLL. There is no APLL in this path.

**The mechanism that replaces it is packet size.** A USB audio source uses an
isochronous **IN** endpoint, and on an IN endpoint the *device* decides how
many bytes to send each frame, up to `wMaxPacketSize`. So the node sends 48
samples in most frames and 47 or 49 occasionally, and the host consumes what
arrives. That is drift correction expressed as rate modulation instead of
clock trimming — the node keeps authority over *rate* even though the host
owns *frame timing*, and decision 12's contract survives in a different form.

This is spec-legal and it is how asynchronous USB audio sources work. Two
things are unknown and must be measured rather than assumed:

1. **Whether the speaker tolerates varying packet sizes gracefully**, or
   clicks. Real devices vary in how well they implement the case.
2. **How much buffering the speaker adds**, and whether it is constant. A
   fixed 30 ms is a number to compensate for; a varying one breaks multi-room
   sync against I²S nodes playing the same cast point.

Both fall out of the same multi-hour run that `LINK-MEASUREMENTS.md` run 22
used, and together they are the number this track exists to produce.

## The order to test in

1. **Confirm TinyUSB UAC device support at our pinned ESP-IDF.** Desk work,
   no hardware. If it needs a version bump, track A inherits track B's
   isolation problem and is worth much less.
2. **Enumerate on the laptop first.** Make the XIAO appear in Windows as a
   USB audio input device with our descriptors — 48 kHz, 24-bit, stereo.
   The laptop is a far better first host than a speaker with a button ritual,
   and Windows will say plainly whether the descriptors are well formed.
3. **Feed it the synthetic tone**, the one that validated the I²S path.
   Recorded on the laptop, so the output is inspectable rather than merely
   audible.
4. **Move to the speaker.** Cable, ritual, tone. First real go/no-go against
   hardware we do not control.
5. **Wire it to the real pipeline** — RTP in, USB out, replacing I²S in the
   Consumer profile.
6. **Modulate packet size** and confirm the speaker does not object.
7. **Measure**: multi-hour drift, and the speaker's added latency against an
   I²S node on the same cast point.
8. **Listen**, only after the numbers.

Steps 1–3 need no speaker at all, which makes this track testable before
deciding whether any particular speaker is worth buying.

---

# Track B — the node as a USB host

## What it reaches

The opposite direction, and a different class of hardware: dongle DACs,
desktop DAC/amp units, USB audio interfaces (which would make a **Producer**
out of the same code in reverse), and USB microphones — the awkward-to-source
part of `LISTENING.md`.

Track A cannot reach any of those, because all of them are USB *devices* and
something has to host them. So track B is not made redundant by track A; it
is made *second*.

## What is already settled

From the descriptor dump in `HARDWARE.md` — a CX31993 + MAX97220 dongle,
VID `0x3302` PID `0x336A` — and from reading `esp-uac2-host`:

| Question | Answer |
| --- | --- |
| Does the device run at full speed? | Yes — Device Qualifier present |
| Is there a UAC 1.0 fallback? | No — `bcdADC 0x0200` at both speeds |
| Is 12 Mbit/s enough? | Yes — 288 of 576 reserved bytes per frame |
| Is our wire format available? | Yes — 48 kHz / 24-bit / stereo, unconverted |
| Does a host driver exist? | Yes — `esp-uac2-host` v0.1.2 |

## The three open questions, in the order they can kill it

### 1. Electrical: can the board actually attach a USB-C device?

Feeding the ESP32-S3 board from a separate supply is correct as far as
*current* goes — 100 mA for this dongle is trivial once the board is not
trying to back-feed its own input — but two things stand between a powered
board and an attached dongle:

- **Does 5 V reach the connector's VBUS pin?** On boards wired as a USB
  *device*, VBUS usually enters through a Schottky diode into the 5 V rail.
  A diode conducts one way, so feeding that rail does not push current back
  out to the connector. Confirm with a multimeter.
- **Do the CC pins say "host"?** A USB-C device presents Rd; a host presents
  Rp. Development boards are wired as devices, and so is the dongle. Two
  devices facing each other never attach.

**The clean way around both is to not use the USB-C connector at all.** The
S3's native USB is GPIO19 (D−) and GPIO20 (D+). Wiring those to a plain
**USB-A receptacle**, with 5 V from the separate supply and a common ground,
removes the problem instead of working around it: USB-A has no CC pins, so
there is nothing to negotiate.

**The adapter in the dongle's box completes it.** The unit ships with a
USB-C-receptacle-to-USB-A-plug adapter, and such an adapter is required by
spec to present **Rp** (56 kΩ to VBUS, the "default USB power" advertisement)
on its CC pin — precisely the signal the dongle needs to attach as a device.
So: A receptacle on the ESP → included adapter → dongle, nothing to buy.

It does not change what the device *reports*. The adapter is passive on
D+/D−; VID/PID, descriptors, UAC version and speed capability are properties
of the dongle either way. And it supplies no power.

A wrinkle worth knowing rather than worrying about: USB-IF prohibits
C-receptacle-to-A-plug adapters, and they are sold by the million anyway.
If enumeration fails, the adapter is a cheap thing to substitute before
suspecting firmware.

The console objection is unchanged: a board with separate UART and USB-OTG
ports keeps the heartbeat diagnostics and USB flashing while the native port
does host duty. On a single-port board this experiment costs both.

### 2. Driver: two clocks where the driver documents one

The dongle exposes clock source `0x09` (speaker path) and `0x0A` (microphone
path). `esp-uac2-host` v0.1.2 states support for single-clock UAC2 devices.
If only playback is used, only `0x09` should matter — but the driver must
select and program the clock belonging to the interface it streams to, and it
was written against devices where there was only one to pick.

### 3. Clocking: there is no feedback endpoint

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

## The order to test in

1. **Multimeter on the board.** Is there 5 V at the connector's VBUS pin with
   the separate supply attached? Answers question 1 before any soldering.
2. **Build the USB-A rig.** GPIO19/GPIO20 to a USB-A receptacle, 5 V and
   ground from the separate supply, and the dongle's own C-to-A adapter to
   reach it. Nothing running on the ESP yet, and nothing to buy.
3. **Enumerate, and nothing more.** IDF 5.4, the USB host stack, no audio:
   does the dongle attach, and do its descriptors come back matching what
   Windows reported? First real go/no-go.
4. **Open the stream.** Interface 1, alternate 2 (24-bit), 48 kHz, clock
   `0x09`. Answers question 2.
5. **Play a tone**, the synthetic source that validated the I²S path.
6. **Measure the drift.** Free-run for hours against the Hub's clock, the way
   run 22 in `LINK-MEASUREMENTS.md` was run. Answers question 3.
7. **Listen**, only after the number.

---

# How both tracks stay isolated

Track B's driver wants **ESP-IDF 5.4** where this project builds on 5.3.1,
and track A may or may not need a version move of its own.

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
4. **One node, and not one in service.** Track A costs a node its USB console,
   so this rule has teeth: the two XIAO nodes in service stay on released
   firmware and keep their flashing port.
5. **A negative result is a deliverable.** If the answer is no, it gets
   written down here with the measurement that produced it, the way the
   `--volume-ctrl fixed` dead end is written down in decision 14.

# Prior art

- **A DIY AirPlay adapter for a JBL Charge 6**, r/esp32, supplied 2026-08-18
  and read from the author's own description rather than the thread, which is
  unreachable from this build environment. A XIAO ESP32S3 chosen for its
  native USB, plugged into the speaker with a USB-C cable, presenting as a USB
  audio source. Built on `rbouteiller/airplay-esp32` with the decoded-PCM path
  redirected from the I²S driver into a TinyUSB UAC stream. Reported: the
  descriptors had to match the speaker exactly; the board runs hot; audio
  hiccups traced to Wi-Fi throughput. **This is the entire basis of track A**,
  and the reason track A was not obvious sooner is that every earlier source
  discussed hosting a DAC rather than being one.
- <https://github.com/rbouteiller/airplay-esp32> — AirPlay receiver for
  ESP32/S3/C5, ESP-IDF 5.x, I²S out to a PCM5102A, with a PTP-synchronised
  timing engine that holds early frames and drops late ones. Not our protocol,
  but the closest published relative of decision 12's playout contract.
- <https://github.com/Averyy/esp-uac2-host> — the UAC 2.0 **host** driver
  behind track B. v0.1.2, single-clock devices, wants ESP-IDF 5.4.
- <https://github.com/wasdwasd0105/airplay-esp32-usb> — a fork of the above
  AirPlay firmware adding **USB host** support: commits reference a runtime
  FIFO carve, per-device sample rate, and an `esp32s3-usbhost` sdkconfig,
  tested against headsets. Despite sitting in the same conversation as track
  A's build, this is track B's shape, and it is the closest thing to a
  worked example of it.

A note on Wi-Fi, since the prior art blames it for hiccups: run 22 in
`LINK-MEASUREMENTS.md` is 6h49m at zero loss, and the RSSI curve explains
why. Whatever else goes wrong here, that is a problem this project has
already measured its way out of.
