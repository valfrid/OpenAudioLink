# USB audio host: an experimental track

**Status: experimental, isolated, nothing scheduled on the main line.**

This describes a deliberate side track — making an ESP32-S3 node a USB
**host** so a USB Audio Class 2.0 device can be its output stage. It is
recorded here rather than in `HARDWARE.md` because it is the first piece of
work in this project that is explicitly a *test*: it may end in a verdict of
"no", and that is an acceptable outcome.

## Why it is worth doing at all

Not for the CX31993 dongle. The dongle is the excuse; the capability is the
point.

The existing Consumer output is an I²S DAC — a bare module giving line level,
which is right for feeding an amplifier and wrong for almost everything else.
A UAC host opens the node to a whole class of finished hardware that already
exists, is already shielded, already has a headphone amplifier, and already
solved its own analogue design:

- dongle DACs, from 120 SEK to whatever anybody wants to spend;
- desktop USB DACs and DAC/amp units;
- USB audio interfaces, which would make a **Producer** out of the same code
  in reverse;
- USB microphones, which is the awkward-to-source part of `LISTENING.md`.

That last group is the one that makes this more than a convenience. Decision 8
frames alternative Consumers as additive — new hardware profiles, not a new
architecture — and this is the profile that turns "buy a suitable module and
wire I²S" into "plug in something that already works".

Against that: it is a track that must not disturb anything currently working,
which is the whole reason it is isolated.

## What is already settled

From the descriptor dump in `HARDWARE.md` (the CX31993 + MAX97220 dongle,
VID `0x3302` PID `0x336A`), and from reading `esp-uac2-host`:

| Question | Answer |
| --- | --- |
| Does the device run at full speed? | Yes — Device Qualifier present |
| Is there a UAC 1.0 fallback? | No — `bcdADC 0x0200` at both speeds |
| Is 12 Mbit/s enough? | Yes — 288 of 576 reserved bytes per frame |
| Is our wire format available? | Yes — 48 kHz / 24-bit / stereo, unconverted |
| Does a host driver exist? | Yes — `esp-uac2-host` v0.1.2 |

None of that was known a week ago, and all of it had to be true before the
track was worth opening.

## The three open questions, in the order they can kill it

### 1. Electrical: can the board actually attach a USB-C device?

The proposal is to feed the ESP32-S3 board from a separate supply rather than
expecting it to source VBUS from its own regulator. That is correct as far as
*current* goes — 100 mA for this dongle is trivial once the board is not
trying to back-feed its own input — but it does not by itself finish the job,
because two things stand between a powered board and an attached dongle:

- **Does 5 V reach the connector's VBUS pin?** On boards wired as a USB
  *device*, VBUS usually enters through a Schottky diode into the 5 V rail.
  A diode conducts one way. Feeding the 5 V rail from a separate supply does
  not push current back out through that diode to the connector, so the
  dongle sees nothing. Worth confirming with a multimeter before believing
  either answer.
- **Do the CC pins say "host"?** A USB-C device presents Rd (5.1 kΩ) on CC;
  a host presents Rp. Development boards are wired as devices, and so is the
  dongle. Two devices facing each other never attach — neither side detects
  the other, regardless of what power is present.

**The clean way around both is to not use the USB-C connector at all.** The
S3's native USB is GPIO19 (D−) and GPIO20 (D+). Wiring those to a plain
**USB-A receptacle**, with 5 V from the separate supply and a common ground,
removes the problem instead of working around it: USB-A has no CC pins, so
there is nothing to negotiate, and VBUS is whatever is wired to it. A C-to-A
cable then reaches the dongle. This is the rig to build.

A self-powered USB hub between the two is the lazier variant — it powers the
dongle itself, so the ESP never sources anything — but it does not solve the
CC question, only the current one.

The console objection is unchanged and already has its answer: a board with
separate UART and USB-OTG ports keeps the heartbeat diagnostics and USB
flashing while the native port does host duty. On a single-port board this
experiment costs both.

### 2. Driver: two clocks where the driver documents one

The dongle exposes clock source `0x09` (speaker path) and `0x0A` (microphone
path). `esp-uac2-host` v0.1.2 states support for single-clock UAC2 devices.

If only playback is used, only `0x09` should matter, and this may be nothing.
"Should" is doing real work in that sentence — the driver has to select and
program the clock belonging to the interface it is streaming to, and it was
written against devices where there was only one to pick.

### 3. Clocking: there is no feedback endpoint

This is the one that changes the design rather than merely risking it.

`HARDWARE.md` credits `esp-uac2-host` with resolving the drift question the
way USB audio normally does — an asynchronous device reports its true rate
through a feedback endpoint and the host adapts. **This dongle has no
feedback endpoint.** Every audio endpoint is `bmAttributes 0x0D`:
isochronous, SyncType *Synchronous*. It slaves itself to the host's SOF
timing and expects exactly one packet per frame.

For a Consumer that is arguably the better arrangement. Decision 12's playout
contract has the node owning its own timing and correcting drift against the
Hub; a synchronous device does not fight that, it follows it. But it means
the drift path is the *host's* SOF generator rather than an APLL-trimmed bit
clock, and how finely an S3 can steer SOF is unknown. This needs measuring,
not assuming, and it is the interesting part of the whole experiment.

## How the track stays isolated

The reason this is a separate track and not a branch of convenience:
**`esp-uac2-host` wants ESP-IDF 5.4 and this project builds on 5.3.1.**

An IDF bump is not a local change. Both `.github/workflows/ci.yml` and
`.github/workflows/release.yml` pin `esp_idf_version: v5.3.1`, and every node
in the house runs firmware built by them. A bump landing on the main line
would rebuild the working Consumer, Producer and provisioning firmware on a
toolchain nothing has been tested against — to enable a feature no node uses.

So the rules for this track:

1. **Its own branch**, and it does not merge until it has a verdict.
2. **The IDF pin does not move on the main line.** If the experiment
   succeeds, bumping IDF becomes its own piece of work with its own
   regression pass on real hardware — not a side effect of this.
3. **No OTA channel mixing.** Experimental images do not go into the Hub's
   firmware store next to images meant for the speakers people use. A brick
   here should cost a cable, not an evening.
4. **One node, and not one that is in service.** The two XIAO nodes stay on
   released firmware.
5. **A negative result is a deliverable.** If the answer is no, the answer
   gets written down here with the measurement that produced it, the same way
   the `--volume-ctrl fixed` dead end is written down in decision 14.

## The order to test in

Each step is cheap relative to the one after it, and each can fail in a way
that ends the track without wasting the next one's effort.

1. **Multimeter on the board.** Is there 5 V at the connector's VBUS pin with
   the separate supply attached? Answers question 1 before any soldering.
2. **Build the USB-A rig.** GPIO19/GPIO20 to a USB-A receptacle, 5 V and
   ground from the separate supply. Nothing running on the ESP yet.
3. **Enumerate, and nothing more.** IDF 5.4, the USB host stack, no audio:
   does the dongle attach, and do its descriptors come back matching what
   Windows reported? This is the first real go/no-go.
4. **Open the stream.** Interface 1, alternate 2 (24-bit), 48 kHz, clock
   `0x09`. Answers question 2.
5. **Play a tone.** The same synthetic source that validated the I²S path.
   Continuity first, quality second.
6. **Measure the drift.** Free-run for hours against the Hub's clock, the way
   run 22 in `LINK-MEASUREMENTS.md` was run. This answers question 3, and it
   is the number the whole track exists to produce.
7. **Listen.** Only after the number.

## What promotes it, and what ends it

**Promoted** if it enumerates, plays, and holds sync over a multi-hour run
within the tolerance decision 12 already demands of an I²S Consumer — at
which point the IDF bump becomes a real proposal with evidence behind it, and
`HARDWARE.md` gains a second Consumer profile.

**Ended** if the board cannot attach a device at all, if the driver cannot
drive a two-clock device and fixing it is beyond a patch, or if the drift
without a feedback endpoint needs software resampling to stay in tolerance.
That last one is the honest failure mode: decision 8 already calls software
resampling "audibly worse if done crudely, and CPU better spent elsewhere",
and this project has an I²S path that does not need it.
