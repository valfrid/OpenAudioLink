# OpenAudioLink Feature Proposal — Automatic Speaker & Room Calibration

**Status:** Backlog / feature proposal  
**Target:** ESP32-S3 receiver  
**Reference microphone:** ICS-43434 I2S MEMS  
**Audio path:** 48 kHz PCM

## Goal

Add an optional self-calibration function to an OpenAudioLink receiver. The receiver generates a known acoustic test signal, measures the speaker/room response with a microphone placed at the listening position, calculates a conservative correction profile, and applies it in the normal audio path.

The goal is useful consumer-level room/speaker correction rather than laboratory-grade acoustic measurement.

## Concept

```text
ESP32-S3 -> I2S DAC -> Amplifier -> Speaker -> Room
   ^                                      |
   |                                      v
   +------------- ICS-43434 <-------------+
```

The measurement therefore includes the real playback chain: DAC, amplifier, speaker, speaker placement and room.

## Measurement microphone

Initial prototype: **ICS-43434 digital I2S MEMS microphone**.

Advantages:

- Direct digital I2S connection to ESP32-S3
- 3.3 V operation
- 24-bit output
- Roughly 50 Hz–20 kHz useful range
- No analog microphone preamp/ADC required
- Cheap enough to use as an optional calibration accessory

Typical interface:

```text
ICS-43434          ESP32-S3
3V3  ------------> 3.3 V
GND  ------------> GND
SCK  <------------ I2S BCLK
WS   <------------ I2S LRCLK
SD   ------------> I2S DATA IN
SEL  ------------> GND / 3.3 V
```

Prefer a detachable microphone that can be placed near the normal listening position rather than permanently mounting it beside the speaker.

## Calibration measurement

Preferred eventual method: logarithmic sine sweep, approximately 50 Hz–20 kHz over 5–10 seconds.

A first prototype can use stepped test frequencies and FFT/amplitude analysis.

The ESP32-S3 records the microphone while playing the known signal and estimates the frequency response.

## Correction strategy

Do **not** blindly invert the measured response.

Use conservative correction:

- Correct broad peaks strongly
- Correct broad dips moderately
- Ignore deep/narrow room cancellation nulls
- Limit positive gain to approximately +3 to +4 dB
- Allow cuts of approximately -8 to -10 dB

This reduces room boom and obvious speaker-response errors without wasting amplifier power trying to fill acoustic nulls.

## DSP implementation

Insert room/speaker EQ into the receiver path:

```text
RTP/UDP
   |
Jitter buffer
   |
48 kHz PCM
   |
Speaker/Room EQ
   |
Receiver volume
   |
I2S DAC
```

Initial implementation should use **parametric biquad IIR filters**.

Example generated profile:

```text
72 Hz       -5.5 dB   Q 1.2
145 Hz      -3.0 dB   Q 0.8
620 Hz      +2.0 dB   Q 0.7
3.4 kHz     -2.5 dB   Q 1.5
High shelf  +1.5 dB
```

Target approximately **8 configurable biquads per channel**.

ESP-DSP provides suitable optimized FFT and biquad/IIR primitives for ESP32-S3. IIR EQ should add negligible latency compared with FIR/convolution correction.

## Separate speaker and room profiles

Keep two logical correction layers:

```text
Receiver profile
├── Speaker EQ   (fixed characteristics)
└── Room EQ      (measured room/placement)
```

This allows a speaker to retain its own correction if moved. Only the room profile needs recalibration.

## Acoustic delay / time alignment

Because the receiver knows when the test signal was generated and can detect its arrival at the microphone, calibration may also estimate acoustic delay.

Future use:

```text
Speaker A measured delay: 4.2 ms
Speaker B measured delay: 7.8 ms

Compensation:
Speaker A +3.6 ms
Speaker B +0.0 ms
```

This could provide automatic alignment of multiple speakers covering the same listening area.

Treat this as an extension after basic frequency correction works.

## Proposed Web UI

```text
ROOM CALIBRATION — Kitchen

Connect the calibration microphone and place it
at your normal listening position.

             [ Start calibration ]

        Frequency sweep playing...

             ████████████░░ 78%

        Measuring room response...

Result:
  ✓ Measurement completed
  ✓ Room correction created
  ✓ 5 EQ filters generated
  ✓ Acoustic delay: 3.4 ms

        [ Before / After ]

        [ Apply ]    [ Cancel ]
```

Later UI can show measured response, proposed correction, estimated corrected response, generated EQ filters, speaker/room profiles and correction enable/disable.

## Hardware consideration

For ESP32-S3 receiver designs:

- Reserve suitable I2S/GPIO resources for optional microphone input
- Consider a small external calibration-microphone connector
- Use 3.3 V microphone interface
- Use ICS-43434 as initial reference device
- Normal receiver operation must not require the microphone

## Suggested development stages

### Phase 1 — Measurement experiment
- Connect ICS-43434
- Generate sweep/test tones
- Capture microphone samples
- Calculate/display measured response
- Compare against a known measurement if possible

### Phase 2 — Automatic EQ
- Generate conservative correction
- Convert correction to biquad parameters
- Process live 48 kHz PCM
- Enable/disable EQ
- Listening tests

### Phase 3 — Web UI
- Calibration wizard
- Measurement progress
- Frequency-response graph
- Before/after visualization
- Save room profiles

### Phase 4 — Advanced calibration
- Separate speaker/room profiles
- Acoustic delay measurement
- Multi-speaker time alignment
- Multiple listening-position measurements
- Investigate FIR correction only if it provides enough benefit

## Initial design direction

Use **ICS-43434 + ESP32-S3 + biquad IIR EQ** as the reference architecture for the first calibration experiment.

The feature remains optional and should not complicate the initial OpenAudioLink RTP/UDP audio transport implementation.

---

# Refined design

**Status: still a proposal, and a strong one.** Nothing here is
implemented; the hardware is on order. This section supersedes the shape
sketched above where the two differ.

## The measurement node stands where the listener does

Not beside the speaker. A microphone at the speaker measures the speaker;
a microphone where somebody's head goes measures the room, and the room is
what this feature exists to correct.

So the measurement node is a **temporary placement** — a chair, a tripod,
roughly between the ears of an assumed listener — not a permanent fixture.
It needs Wi-Fi where it stands, which is worth checking before wondering
why a sweep never arrived.

## One chip generates the sweep and captures it

This is the decision the rest follows from.

The measurement node **sends the sweep to the speaker as RTP** — it is an
ordinary Producer, with a new source beside `tone`, `pattern` and
`capture` — and captures the microphone on its own I²S at the same time.
One ESP32-S3 does both, so both sit on **one clock**, and the offset
between "the sweep started" and "the sound arrived" is a real measurement
rather than the difference between two free-running oscillators.

Splitting those across two nodes would give up absolute delay entirely.
Keeping them together also avoids running I²S down a long cable to a
distant microphone, which at a 3 MHz bit clock is not something to attempt
past about half a metre.

## What the delay actually measures, and why that is the right thing

The path being timed is not purely acoustic. It runs: sweep generated →
RTP across Wi-Fi → the consumer's jitter buffer → its DAC → the air → the
microphone. The playout buffer alone is around 200 ms, dwarfing every
acoustic distance in a house.

That is **correct for this purpose**. Aligning several speakers means
equalising total delay from "sample enters the system" to "sound reaches
the listener", and the buffer is part of that path for every one of them.
Measuring only the acoustic leg would align the wrong thing.

One caveat follows: the playout buffer is adaptive — it walks its fill back
towards a target rather than holding a fixed depth. So a delay measurement
is only meaningful once the buffer has settled, and repeat runs will differ
by whatever the buffer is doing. Measure after a stream has been running
long enough to converge, and treat a single reading as approximate.

## Aligning two speakers needs far less than this

The full cycle above exists to derive room correction. Measuring the
*offset between two speakers* is a much smaller problem, and worth
separating because it can ship long before any DSP does.

**Play the same signal to both at once and autocorrelate one capture.**
The recording contains two copies of the signal separated by exactly the
offset, so the autocorrelation has a peak at that lag. No reference clock,
no known send time, no deconvolution — the measurement is self-contained in
a single capture from a single microphone.

It needs a signal with a sharp autocorrelation peak: a click, an MLS
sequence, or the same sweep the rest of this document uses. A sine will not
do, because a periodic signal correlates with itself at every period.

**Microphone placement is forgiving here, unlike for room response.** Sound
covers 34 cm per millisecond, and the offsets being corrected are tens of
milliseconds: half a metre of asymmetry is 1.5 ms of error against a 30 ms
quantity. Stand the microphone roughly between the two speakers and the
geometry is a rounding error. Absolute room measurements are not so kind,
which is why the rest of this proposal is careful about where the node
stands and this part need not be.

### There is now something to apply the answer to

Firmware 0.25.0 added `delayMs`, a per-node playout trim in NVS, set from
the Hub and applied live. That is the actuator this measurement was always
going to need, and it arrived for an unrelated reason: two speakers playing
one stream through different output stages do not come out together.

The gap is a property of the output stage. A USB dongle carries the host
driver's ring, 1 ms USB frames and its own internal buffering on top of the
playout; an I²S DAC carries four DMA descriptors. Measured by ear the
difference is tens of milliseconds — enough to be obvious in a room, and
this file previously assumed both were "about 20 ms".

Which suggests an order of work. Measure one CX31993 properly, once, and
the number becomes a sensible **default** for `output = usb` rather than
something every installation discovers by ear. Per-node trim then handles
what remains: a different dongle, a powered speaker with its own DSP, a
listening position that is not equidistant. Measure to find the constant,
trim to handle the variance.

## The measurement cycle

Started by a person, from the setup page, against one cast point.

1. The Hub tells the measurement node which speaker to sweep and on which
   channel.
2. The node streams the sweep to that speaker — **left only, then right
   only**, because the two are different boxes in different corners and a
   stereo pair's halves do not share a room response.
3. It captures the microphone throughout, and hands the capture to the Hub.
4. Repeated per channel and per frequency segment.
5. The Hub deconvolves against the sweep it knows was sent, derives the
   response, and proposes a conservative correction as biquads.
6. The page shows measured, proposed and predicted-corrected. A person
   presses Apply, and the filters are written to that speaker.

Nothing is applied automatically. The same reasoning as OTA: fetching is a
convenience, installing is a deliberate act.

## Why it is segmented, and how the hardware decides that

Segmentation is not tidiness. It is what makes the capture fit.

Ten seconds at 48 kHz in 32-bit stereo is about 2 MB. The XIAO has 8 MB of
PSRAM so it fits there, but the useful observation is that **the band that
matters most is the cheapest to capture**.

Room modes — the boom this feature exists to fix — live between roughly 30
and 200 Hz, and low frequencies need *long* sweeps because a 30 Hz
component takes many cycles to establish. They also need almost no
bandwidth: analysing to 500 Hz needs a 1 kHz sample rate, not 48 kHz.

So:

| Segment | Sweep | Captured at | Buffer |
| --- | --- | --- | --- |
| 20-200 Hz | long, 10 s+ | decimated to ~1 kHz | ~40 KB — internal RAM |
| 200 Hz-20 kHz | short, 2-3 s | 48 kHz | ~600 KB — PSRAM |

Decimating on the node before storing is what turns the most valuable
measurement into one that needs no PSRAM at all. It also means a future
board without PSRAM can still measure the low band, which is most of the
benefit.

## Filters are a property of the speaker

Exactly like `channel` — stereo, mono, left, right. Stored in the node's
NVS, set with `POST /config`, surviving reboots and Hub reinstalls, and
belonging to **this speaker in this room** so that moving it invalidates
its filters and nothing else's.

Held as **frequency, gain, Q and type** rather than as coefficients:

```json
{ "eq": { "left":  [ { "hz": 72,  "db": -5.5, "q": 1.2, "type": "peaking" } ],
          "right": [ { "hz": 145, "db": -3.0, "q": 0.8, "type": "peaking" } ] } }
```

Coefficients are sample-rate specific and formulation specific — stored
ones go quietly wrong if either changes. A frequency cannot. It is also the
form a person can read, edit and sanity-check, which the manual half of
"manual or assisted" depends on.

Eight per channel, both channels independent.

## Capability, not role

The announce already carries `capabilities` beside `roles`, and the
distinction has held: a role is what a node does in the audio graph, a
capability is what hardware it has.

A microphone is hardware — `capabilities: ["mic"]`. Sweeping and capturing
is an operation the Hub asks for, and the node performing it is a Producer
while it does so, which it already knows how to be. That avoids inventing a
fifth role and inventing an answer to whether it replaces the others.

## What this reuses

Almost everything, which is the point:

- **RTP** carries the sweep. A new source name beside `tone` and `capture`.
- **The Producer role** is what the measurement node already is while
  sweeping.
- **`POST /config`** already writes per-speaker properties to NVS.
- **The Hub** already orchestrates who sends to whom, and is the only
  machine here with the memory and the floating point for deconvolution.
- **The setup page** is where a deliberate, occasional, technical operation
  belongs — beside firmware and roles, not in the switchboard.

The genuinely new parts are the sweep source, the microphone capture, an
upload path for the recording, the analysis on the Hub, and the biquads in
the playout path. Five pieces, each small, on top of a system that already
does the hard part.

# Notes against this system

Written when the proposal was filed. Nothing here is a change to the
proposal — it is what the rest of the project already implies about it, so
that the first hour of Phase 1 is not spent rediscovering it.

## It fits where it says it does

Putting EQ in the receiver, ahead of volume, is the same reasoning as
decision 10: the stream stays byte-identical for every consumer, because
that identity is what keeps several speakers in step, and what a node does
with the samples afterwards is its own business. A room correction is a
property of a speaker in a room, exactly like the stereo/mono/left/right
channel profile — so it belongs in the same place, is stored the same way,
and survives a Hub reinstall for the same reason.

That also settles a question the proposal leaves open: correction is per
**node**, not per cast point. Two speakers in one room need different
corrections, and a speaker moved to another room needs a new one.

## The I²S budget decides which nodes can do this

The ESP32-S3 has two I²S peripherals. A Consumer uses one for the DAC, so
the microphone takes the other, and that works. A node that is *also* a
Producer capturing a turntable has already spent both.

So a calibration microphone belongs on Consumer nodes, and a node cannot
be a vinyl Producer and a self-calibrating speaker at the same time. Worth
knowing before designing a connector onto a board meant to do both.

The ICS-43434 is an I²S slave, which is the quiet advantage in this choice:
the ESP32 masters both it and the DAC, so the sweep and the recording share
one clock. Acoustic delay measurement is only meaningful because of that —
`HARDWARE.md` describes what happens when a node has two clock domains, and
this design avoids it by construction.

## PSRAM is a requirement, not a nicety

A 10-second sweep at 48 kHz is 480 000 samples: about 2 MB captured as
32-bit, before any FFT working space. That does not fit in the S3's
internal RAM. The XIAO ESP32S3 has 8 MB of PSRAM so the reference board is
fine, but any future profile without PSRAM cannot run the measurement —
even though it could happily *apply* a profile measured elsewhere.

Measuring and applying are worth keeping separate for that reason alone.

## The correction band and the microphone's corner overlap

The ICS-43434 is useful from roughly 50 Hz, and the proposal's own worked
example starts at 72 Hz. Room modes — the boom this feature exists to fix —
mostly live between 30 and 120 Hz. So the most valuable octave sits right
where the microphone is least trustworthy, and MEMS parts carry a couple of
decibels of unit-to-unit sensitivity tolerance on top.

This is not a reason to choose a different microphone; it is a reason to
treat measurements below about 60 Hz as advisory, and to be sure the
conservative-correction limits apply hardest there. "Consumer-level, not
laboratory" is the right target, and this is where the difference lives.

## Headroom, because +4 dB of EQ has to come from somewhere

Volume is applied in `playout_task` as a Q16 gain after a chunk is taken
from the ring, and the ring holds full-scale audio. Boosting a band by
+3 to +4 dB on material already near full scale overflows before volume
gets a chance to bring it back down.

So an EQ stage needs its own pre-attenuation — the usual answer is a fixed
cut equal to the largest positive gain in the profile, applied before the
biquads — and the profile generator has to know that the cut is the price
of every boost. This is not expensive, and it is unpleasant to discover by
ear later: it presents as distortion on loud passages only.

## Cost, roughly

Eight biquads on two channels at 48 kHz is about 800 000 biquad operations
per second. With ESP-DSP's optimised kernels that is low single-digit
percent of one core at 240 MHz — genuinely negligible next to the Wi-Fi
stack. The measurement pass is the expensive part, and it happens once,
while nothing is playing.

## Where it sits

After the open reliability work, not before it. `ROADMAP.md` lists this
under DSP. The proposal already says the microphone must never be required
for normal operation, which is the right constraint and the one that keeps
this from delaying anything else.
