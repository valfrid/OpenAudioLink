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
