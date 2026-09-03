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

Being temporary is why, as of firmware 0.31.0, it is **not a node of its
own**. A device used for ten minutes and then put in a drawer does not
justify a whole ESP32, enclosure and power feed, so the microphone shares
the turntable Producer's box: both converters wired at once, an `input`
setting in NVS saying which one is live, read at boot
(`protocol/CONTROL.md`, and the wiring in `docs/HARDWARE.md`). The two are
never wanted together — a sweep and a record playing are not simultaneous
events — and they need the node in opposite clock roles, which is what
makes it a boot-time choice rather than a switch.

That box then unplugs from the turntable, stands on the chair, and is the
measurement node for the length of a sweep. A permanently installed
microphone node is still worth building later, for the uses that are about
listening rather than calibrating; it would use the same pins.

**The ICS-43434 this proposal names is now the part in hand.** An earlier
delivery supplied a PDM board instead — `3V GND SEL CLK DAT`, a 1–3 MHz
pulse stream needing PDM RX mode — and that entry stood here for a while.
The board now on the bench has six pads with an `LRCL`, which is ordinary
I²S, and `oal_capture` already brings up exactly that: Philips slots, 32
bits, the ESP as clock master. No new capture path is needed.

`docs/HARDWARE.md` carries the wiring for the stand-alone node and for the
combined box, and one trap worth reading before soldering: the word select
wants D5, which is also where `OAL_ADC_MCLK_GPIO` points by default.

It is still **not a calibrated measurement microphone**, so Phase 1 should
be read as *relative* — this speaker against that one, this position
against another — rather than as absolute truth about the room. Room
modes, a boomy corner, the broad shape of a curve and an arrival time all
survive that caveat, and those are what Phase 1 is asking for. The 43434
simply starts from a better floor than the PDM part would have: 65 dB SNR,
flatter, 24 bits.

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

---

# What exists now

The proposal above is a year of intent. This section is the part that has
been built, and it differs from the proposal on one structural point.

## The measurement runs on the Hub, not on the node

The proposal has the receiver generate the sweep, record it, and analyse it
in PSRAM. It does not, and it should not. The Hub already produces streams
(radio, Spotify, a tone), already receives them (the recorder), has as much
memory and floating point as the analysis wants, and is the only place that
can look at the same measurement twice.

So the chain is:

```text
Hub --- sweep, RTP ------> speaker node ---> amplifier ---> speaker
                                                              |
                                                             room
                                                              |
Hub <-- capture, RTP ----- microphone node <--- ICS-43434 <----+
```

Both legs are ordinary OpenAudioLink streams at the ordinary profile. The
microphone node is a Producer with `input=mic`; the speaker node is a
Consumer like any other. Nothing about either is special, which is the
point — a measurement that needs a special mode measures the special mode.

This drops the PSRAM requirement for *measuring* as well. A node still
needs nothing but the ability to apply biquads, which is what the last
section of the proposal already argued for keeping separate.

**The two clocks are back, though.** The proposal's arrangement had the
sweep and the recording on one crystal. Here the speaker's playout clock,
the microphone's capture clock and the Hub's sender are three, and the
measurement crosses all of them. That is fine for a *frequency* response —
a few ppm of rate error over ten seconds is far below the resolution of
anything the analyser reports. It is not fine for absolute time, so the
impulse response's position is a latency figure with a clock-drift term in
it, not a calibrated one.

## The signal

`SweepSignal` in the Hub. A logarithmic sine sweep, and the analyser
divides by this exact definition rather than by a recording of it, so both
ends compute from the same code and nothing has to be agreed at run time.

| | |
|---|---|
| Band | 20 Hz – 20 kHz |
| Sweep | 8 s |
| Silence | 2 s |
| Cycle | 10 s, repeating |
| Amplitude | −6 dBFS peak |
| Fade in / out | 100 ms / 10 ms, raised cosine |

Each choice is load-bearing:

- **Logarithmic, not linear.** Every octave gets the same number of
  seconds. Half the sweep is spent below 632 Hz — the geometric mean of the
  band — which is where rooms misbehave. A linear sweep is past 10 kHz by
  its halfway point and gives the bottom two octaves 0.2 % of its time.
- **The silence is not padding.** It has to outlast the room's
  reverberation, or the tail of one sweep lands on the head of the next and
  late reflections fold onto the direct sound. A domestic room decays in
  0.3–0.6 s; two seconds covers it several times over.
- **It repeats** because every complete cycle is an independent look at the
  same room, and averaging *k* of them lifts the signal by √k. That is what
  makes a −91 dBFS microphone in a −63 dBFS room a usable instrument.
- **The fade in is long** (100 ms, two cycles of 20 Hz) because switching
  a 20 Hz sine on at half scale is a step, and a step excites everything at
  once — it would arrive in the result as a second impulse response with no
  fixed relationship to the first. The analyser divides by this definition
  rather than by an ideal sweep, so the fade costs signal-to-noise rather
  than accuracy: by the time the sweep is at level it has reached 21.8 Hz,
  and that is the whole of the band paid for. The fade out is short because
  10 ms at 20 kHz is two hundred cycles.
- **−6 dBFS**, because a sweep that clips is a measurement of the clipping.

A sweep also separates the loudspeaker's harmonic distortion from its
linear response for free: because the frequency rises exponentially, the
*n*th harmonic arrives a fixed time *ahead* of the fundamental that
produced it, so distortion products land before the impulse response and a
window discards them. Noise gives neither this nor the signal-to-noise
ratio.

## One loudspeaker at a time

The sweep goes down one channel of the stream, chosen per run. This is not
a convenience: two speakers playing the same sweep arrive at the microphone
at different times, and their sum has deep cancellations that belong to the
*pair* — to the microphone's position between them — rather than to either
speaker. A correction fitted to that would make both of them worse.

Using the stream's own channels rather than a separate target means nothing
needs to be told to stop. Every node already knows which half of the stream
it plays (decision 10), so a left-channel sweep silences the right-hand
speaker by construction.

```
POST /api/stream/sweep   { "destinations": [...], "channel": "left" }
```

or the **Start measurement sweep** button in Hub streaming, which is beside
the test tone because they share a destination list and nothing else — a
tone answers "is this speaker working", a sweep answers "what does this
room do to it".

## How a measurement is taken

Put the microphone node at the listening position — where you listen, not
beside the speaker. Check its level in the gain dialog first: the sweep
should peak somewhere around −20 dBFS, loud enough to be well clear of the
room and far enough from full scale not to clip on a bass mode. Then, in
**Room measurement**, pick the speaker, its channel and the microphone, and
press Measure.

That is one action because the sequence behind it is where the mistakes
were, and every one of them has happened here:

- the recorder left on the pattern source, so the file holds the synthetic
  test pattern and not a room;
- the sweep sent to both speakers, so the curve describes their
  interference at the microphone rather than either speaker;
- the microphone node still set to capture from `line`, so the file is the
  ADC's silence;
- stopped before enough whole cycles had passed to average.

So the Hub does it: sweep first, microphone recording second — milliseconds
apart, so what is recorded is steady state — then both stopped after the
sweeps asked for, then the analysis. The two panels above still work on
their own and are still the right tools for anything that is not this.

**The microphone streams to the Hub alone.** This is the one measurement
where the speakers must not also be in its destination list: they are
already playing the sweep, and adding the microphone to them puts a
microphone and a loudspeaker in one room with a loop between them. (The
clap test wanted the opposite, which is why the recorder's own panel still
offers it.)

Six sweeps takes eighty seconds. Two more cycles are recorded than are
averaged: the recording starts at an arbitrary point in a cycle, so the
first aligned one may not begin until a cycle in, and the analyser then
discards the one after it while the receiver is still filling its buffer.

```
POST /api/measurement/start  { "speaker": "...", "channel": "left",
                               "microphone": "...", "cycles": 6 }
GET  /api/measurement
POST /api/measurement/stop
```

## The analyser

What came back, divided by what went out. Everything else is the
bookkeeping that makes that division legitimate.

1. **Fold and average.** The signal repeats every cycle, so the recording
   is folded on the cycle and the cycles are averaged. Noise is
   uncorrelated between them and falls as the square root of their number;
   the sweep is not and does not. This is why the sweep repeats at all.
2. **Align, twice.** The recording starts whenever somebody pressed the
   button. Folding at the wrong phase *rotates* the response rather than
   delaying it, and no division undoes a rotation. The phase is found by
   looking for the silence — the gap has an edge, while a logarithmic
   sweep's autocorrelation peak is broad and dominated by its bottom
   octave. Then it is found again, properly, from the impulse response
   itself (see below).
3. **Divide, with a floor.** `H = Y·conj(X) / (|X|² + ε)`. Outside the
   swept band the sweep has no energy at all, and the unregularised
   quotient there is the microphone's own noise multiplied by an
   arbitrarily large number — a spectacular curve describing nothing. With
   the floor it goes quietly to zero, which is the honest answer for a band
   that was never excited.
4. **Window the impulse response.** Half a second, with 5 ms kept ahead of
   the peak. The pre-window is where the loudspeaker's harmonic distortion
   lands: with an exponential sweep the *n*th harmonic arrives a fixed time
   *ahead* of its fundamental, so a short pre-window is what separates
   distortion from response.
5. **Smooth and level.** A sixth of an octave averaged into each of 240
   logarithmically spaced points, then slid so that 200 Hz–2 kHz sits at
   0 dB. The absolute level would be a property of the microphone's
   sensitivity, which is not calibrated; the shape is the measurement.

### Why the alignment needs two passes

The first real measurement taken with this — a living room, eighty seconds,
six sweeps averaged at 24 dB above the noise — came back with the direct
sound landing 10.70 s into a 10.92 s buffer instead of at 0.25 s. That is
not "late": the transform is circular, so it means the direct sound arrived
0.22 s *before* the analysis window started. The alignment had reported the
sweep as arriving about half a second later than it did.

The cause is in the method, not in a mistake. Looking for the quiet part of
the cycle finds the gap, but **the room is still ringing when the gap
begins**. The quietest window is therefore not the one that starts where
the sweep ends — it is the one shifted past the reverberation. So the
search reports the arrival late by roughly the room's decay time, and every
test written until then used a room that stopped ringing in a few
milliseconds and so never showed it.

The impulse response has no such bias: it says exactly where the direct
sound is. So the fold is done a second time with the peak placed on the
margin where it belongs. It converges in one step, because the correction
is a measurement rather than an estimate — and the warning stays, for the
case where re-aligning does not fix it, which now means either the file is
not a recording of the sweep or the room rings for longer than the gap.

The regression test builds a room as a real impulse response — direct
arrival plus half a second of exponentially decaying noise, coloured by a
known peaking filter — and applies it by convolution, so the recording is
the periodic steady state the analyser assumes rather than an approximation
of it. With one pass it fails; with two it recovers the mode.

### Why zero-padding the transform is exact

Dividing spectra is a circular operation, and circular arithmetic wraps a
room's decay back onto the start of the sweep. It does not here, and the
silent gap is the reason: the response is shorter than the gap, so the
periodic response and the linear one are the same sequence, and padding to
a power of two for the transform changes nothing. That is the gap's second
job, and it is why the FFT can be a plain radix-2 one.

### What it reports besides the curve

- **Cycles averaged** — the measurement's confidence.
- **Signal to noise** — sweep against the quiet part of the cycle. Below
  about 20 dB the bottom of the curve is the room's noise floor rather than
  the room, and it says so.
- **Peak level and clipped samples.** A clipped sweep measures the
  clipping, and the curve looks exactly like a real one. This is the first
  thing to read.
- **Where the direct sound landed.** It belongs on the alignment margin;
  far from it means the alignment locked onto the wrong edge and the curve
  is not a measurement of this room.

It is *not* a latency figure. The window is placed by looking for the
arrival, so the network and playout delay has already been taken out, and
the three clocks involved are not the same one.

```
POST /api/recordings/<file>/analyse?channel=0
GET  /api/recordings/<file>/response
```

The answer is written next to the recording as `<name>.response.json`. A
measurement is worth keeping: the recording is the evidence, the curve is
the reading of it, and a room measured today is what a correction fitted
next month has to be checked against.

## How the analyser is tested

Against rooms whose answer is written down in advance. A peaking filter of
known frequency, Q and gain is applied to the sweep, delayed, started at an
arbitrary offset and buried in noise; the analyser has to find that shape
and nothing else. The end-to-end test does it at the real sizes and the
shipped defaults — a 480 000-frame cycle padded to 2^19, through the
recorder's own 24-bit writer and the byte swap — and recovers a +9 dB mode
at 62 Hz and a −5 dB dip at 3.5 kHz to within 1.5 dB.

That is the only way to tell a fault in the analysis from a fault in a
room without a calibrated laboratory.

## Keeping measurements, and comparing them

One curve says what a room does. **Two say what changed**, and that is the
question almost every time:

| Compare | Answers |
|---|---|
| Left against right | which speaker, or whether it is the room |
| Before against after | whether a correction worked |
| Same speaker, microphone moved | whether a peak belongs to the room or to where you sat |

None of those can be read from a single measurement, and flipping between
two pictures is not the same as laying them over each other. So every
analysis is kept, with a note of what it is a measurement *of* — speaker,
channel, microphone, when — written at the moment it is set up rather than
reconstructed from a filename later. A free-text name goes beside it, which
is where "before" and "after" live: no device name can make that
distinction.

The note is a sidecar (`<recording>.context.json`) rather than part of the
analysis, so renaming a measurement never means recomputing one, and a
curve worked out before any of this existed still opens.

```
GET    /api/measurements                  every curve, with what it is of
POST   /api/recordings/<file>/label       name one
DELETE /api/recordings/<file>             throw one away, recording and all
```

Up to four are drawn at once, on one shared decibel scale — two curves on
different scales would be a comparison of nothing. Four is the limit
because the colours stop there: the palette is four fixed slots, checked
for colour-vision deficiency against both the light and the dark page
rather than picked by eye, and a fifth curve is a reason to untick one, not
to invent a colour nothing was validated for. Two of the four fall below
3:1 contrast on the light page, so colour never carries identity alone —
every curve is in the legend, named in the hover readout, and present in
the third-octave table under the chart.

## Where the analysis runs, and why it is not in a cloud

**On the Hub. Deterministically, and offline.** This was a real question —
an assistant reading the curves has been useful all through this feature —
so the reasoning is written down rather than left as a habit:

1. **Local-first is the project's first principle.** A calibration that
   needs a network service to finish is a feature that stops working when
   the internet does, and stops working permanently for anyone who clones
   this repository. Room correction has to work for a stranger with no
   account.
2. **It has to be deterministic.** These coefficients go into a
   loudspeaker's NVS and change what it sounds like. The same recording
   must produce the same filters every time, and "why is it different
   today" must have an answer. A language model is not a function.
3. **It has to be testable against known answers.** That is how the
   alignment bias was caught — a synthetic room with the answer written
   down in advance — and every rule in the fitter has such a test. No such
   test can be written around a network call.
4. **It is not hard.** Peak-picking a smoothed curve and fitting peaking
   biquads is arithmetic, not machine learning.
5. **It closes the loop**, which is the point: measure, fit, write,
   re-measure, see that it worked, on one machine.

### What an assistant is actually for here

Not producing coefficients. **Judging measurements.** Everything an
assistant contributed to this feature was judgement about what a
measurement *means*: that the rise above 12 kHz is the microphone and must
not be corrected, that the 100 Hz peak appears on both loudspeakers so it
is not a loudspeaker, that the bottom octave is noise, that the alignment
was biased by reverberation. That happens a few times in a project, not
once per measurement.

And it is answered from data, not from screenshots — those lose the
precision, lose the metadata, and cannot be diffed against last month's.
Hence `GET /api/measurements/export`: one file with every curve, every
microphone position, both channels, the fitted corrections, and the device
and firmware versions that produced them. Two measurements come to about
27 kB.

The other place a conversation earns its keep is deciding the **policy**
once — the band, the limits, what may be touched at all. That is the
section below. It is encoded in code, not re-decided per room.

## Fitting a correction

`RoomCorrection.Fit` turns a curve into a few peaking biquads and a preamp.

**Do not invert the measurement.** That is the proposal's one instruction
about this stage and it is the whole design. A response contains things
that can be fixed — a mode that rings and adds ten decibels of boom at one
note — and things that cannot: a cancellation where a reflection arrives
out of phase, which no amount of power fills, because the extra power
cancels too. Inverting tries to fix both, wastes the amplifier on the
second, and makes every other seat worse.

| Rule | Default | Why |
|---|---|---|
| Band | 30–300 Hz | Above ~300 Hz the measurement is a property of where the microphone stood, not of the room; below 30 Hz it is the noise floor |
| Max boost | +3 dB | Boost costs excursion, and a deep dip is a cancellation that cannot be filled |
| Max cut | −9 dB | Cutting is nearly free, and peaks are what people hear as boom |
| Dip restraint | half | Correct broad peaks strongly, broad dips moderately |
| Max Q | 8 | Narrower than that and the filter is fitted to the tripod, not the room |
| Min deviation | 2 dB | Not worth a filter |
| Max filters | 6 | A long tail of small corrections is where this stops being conservative |

Everything it declines to do is listed in the profile's notes, so a person
can see what was left alone and why.

### Two details that were wrong first

**The baseline is the median, not the mean.** A correction aims at the
band's own level, and a mean is dragged up by exactly the thing being
removed: one +12 dB mode over a fifth of the band lifts the average by two
decibels, so the fitter aims two decibels high and under-cuts the mode by
that much. A median is indifferent to a feature occupying a minority of the
band — which is the definition of a mode worth correcting.

**Narrowness is judged on the unsmoothed curve.** Smoothing is what makes
the *size* of a correction a property of the room rather than of the
interference pattern — and it is also exactly what destroys the evidence
that something is *narrow*. A Q of 20 averaged over a third of an octave
looks like a Q of 4, and the rule that refuses narrow features then never
fires. So the size comes from the smoothed curve and the width from the raw
one.

### Headroom

**Definition.** Take the combined magnitude response of every filter in a
channel's vector, evaluate it at each measured frequency, and find its
largest positive value. The headroom is that, negated, rounded to the next
half decibel:

```
worst   = max over f of ( sum over filters of filter.MagnitudeDb(f) )
headroom = -ceil(worst * 2) / 2          ... always <= 0
```

So a vector with no boost gets 0 dB, and the number shown in a speaker's
panel is the amount of output given up to make room for whatever the
correction lifts.

**Why.** The ring holds full-scale audio and the volume stage only
attenuates, so a band boosted on material already mastered near full scale
goes over the top — and it presents as distortion on loud passages only,
which is the worst way to find out.

**Where it is applied is not a detail.** It goes inside the filter, on the
way out, before the sample is written back as an int32. That write is where
the clip happens. Attenuating after it scales a value whose peaks are
already gone: the headroom would be paid for in loudness and buy nothing.
The first version of this folded it into the volume multiply after the
filter, which was one multiply cheaper and did exactly that. A +12 dB boost
with 12 dB of headroom should come back at the level it went in; it came
back 9.3 dB light, and `test_eq.c` now measures that rather than looking
for samples sitting at the rail — which does *not* distinguish the two,
because the broken ordering scales the clipped values back down.

Placing it at the filter's output rather than its input is exactly
equivalent, a biquad being linear; only the size of the intermediate values
differs, and those are floats with room to spare.

**It applies only when the correction does.** It was unconditional at
first, so turning the correction off left the speaker quieter by the
headroom — which would have made every A/B comparison a level difference
rather than a tonal one, and the switch exists precisely so that comparison
is honest.

The attenuation covers **the worst the filters do together at any one
frequency**, not the sum of their gains. The sum is a bound and a bad one:
on the right-hand loudspeaker measured here, four boosts at 38, 46, 82 and
254 Hz summed to 9.2 dB, which would have thrown away most of the
loudspeaker's output to protect against an overlap that does not exist.
Filters an octave apart barely reach each other. The combined magnitude
response is what the audio actually sees, and its peak took that same
profile to 6 dB.

```
POST /api/recordings/<file>/correction    fit one, and say what it would do
GET  /api/measurements/export             everything, for a second opinion
```

Fitting sends nothing to a loudspeaker. It says what a correction *would*
be and what it is expected to achieve, so it can be looked at — and refused
— before anything is applied. The predicted curve is drawn beside the
measurement it was fitted to.

## Applying it on the node

Four settings in NVS, and the shape of them is the feature.

| Key | Holds |
|---|---|
| `eq_l`, `eq_r` | one vector per **output** channel, as readable text |
| `eq_on` | whether to run it — the coefficients are kept either way |
| `eq_pre` | headroom for the boosts, tenths of a dB, negative |

**The stored parameter is the design, not the coefficients.**

```
104.0/3.78/-9.0 151.2/5.01/-4.8 220.2/7.02/-3.5
 ^     ^    ^
 Hz    Q    dB
```

Storing `b0..a2` would be smaller and faster to load and would make the
setting unreadable. Nobody can look at `0.9976, -1.9952, 0.9976, -1.9952,
0.9952` and say what it does, let alone nudge it. Being able to read what a
loudspeaker is doing and change it by hand is the whole point of the
format, so the node derives the coefficients at boot instead. Eight bands
are allowed where the Hub's fitter stops at six, leaving two for a person
adding one.

**One vector per output channel**, because a correction belongs to a
loudspeaker rather than to a stream: a stereo node drives two speakers
standing in two different corners and they do not measure the same. Nothing
requires the two to match.

**The switch is not a convenience.** Without it, comparing corrected
against uncorrected means deleting a profile and measuring again to get it
back — so nobody would ever check, and whether a correction actually helped
is the one thing worth checking. It applies at once rather than at the next
boot, because a correction that needed a reboot to audition would never be
auditioned.

**The preamp is one value for the node**, not one per channel. It is a
broadband gain, so different values left and right would move the stereo
image sideways; the Hub works out what each channel needs and sends the
deeper of the two. It is folded into the existing volume gain — one
multiply either way — and applied *before* the filters, so their headroom
does not depend on how loud somebody has the speaker.

The fence around a band (10–20 kHz, Q 0.1–20, ±15 dB) is deliberately wider
than anything the fitter will produce. Hand tuning is a supported use, and
the limits exist to stop a typing slip destroying a tweeter rather than to
enforce the fitting policy — that lives on the Hub where it can be
explained. So a wild value is clamped, while something that is not three
numbers is refused outright: a half-understood vector applied to a
loudspeaker is worse than none.

### Single precision, measured rather than assumed

The correction band is 30–300 Hz, which at 48 kHz puts a biquad's poles
within a thousandth of the unit circle — where single precision runs out
and a filter quietly stops being the filter that was designed. The
ESP32-S3's floating-point unit is single precision only, so `double` is
software emulation and far too slow for eight sections at two hundred
thousand samples a second. The question was not which is nicer but whether
the fast one is good enough.

Transposed direct form II, checked by running sines through it and
comparing against a `double` reference: **within 0.15 dB everywhere from
30 Hz to 300**, including at the skirts where a misplaced pole shows most.
A biquad that has run out of precision does not fail — it just stops
matching what the Hub predicted — so this is measured in `test_eq.c` rather
than argued about.

The chains belong to the playout task. A change arrives as a staged request
and is adopted between chunks, because a filter's state is the last two
samples it saw and swapping coefficients underneath a half-filtered chunk
rings for as long as the filter decays.

```
POST /config {"eqLeft":"104.0/3.78/-9.0", "eqRight":"...",
              "eqEnabled":true, "eqPreampDb":-2.0}
```

## Writing it to a loudspeaker

```
POST /api/devices/<id>/correction          {"left": "<recording>", "right": "<recording>"}
POST /api/devices/<id>/correction/enabled  {"enabled": false}
```

The request names **measurements, not coefficients**. The Hub fits both
sides, works out the one thing that cannot be decided per channel, and
writes the pair in a single request — so a node is never left running one
channel's correction against the other's, which would be audible as the
stereo image walking sideways and would be nobody's fault in particular.

**The headroom is the deeper of the two channels' needs.** Each channel
needs at least its own; the node gets the larger, and the channel that
needed less is simply quieter by the same amount as its partner, which is
what keeps the image where it was.

Either side may be omitted — a mono node has one loudspeaker, and only one
may have been measured. An omitted side is *cleared* rather than left
alone: a node carrying last week's correction on one channel and this
week's on the other is the worst of both.

## The whole loop

1. **Measure** each loudspeaker, one at a time. Name them.
2. **Fit** — the predicted curve is drawn beside the measurement, and the
   notes say what the fitter declined to touch and why. Refuse it here if
   it looks wrong; nothing has been sent.
3. **Write** it to the speaker.
4. **Measure again with the correction on**, name it "after", and put the
   two curves on one chart.

Step 4 is the point. Everything before it is a prediction — the deviation
figures come from applying the fitted filters' magnitude response to the
measurement they were fitted to, which is arithmetic rather than evidence.
The second measurement is the evidence, and the switch is what makes it
cheap to get: turn the correction off, measure, turn it on, measure, and
compare on the same chart without ever losing the profile.

### Seeing it, and changing it by hand

Expand a speaker in the device table: **Room correction** shows whether it
is on, the headroom, and both vectors in editable fields with a **Save**
beside each. Enter saves too, but a keystroke nobody can see is not a
control — the button is.

```
POST /api/devices/<id>/eq  {"left": "104.0/3.78/-9.0 60/1.5/-3"}
```

That is the whole reason the stored form is readable triples rather than
coefficients — a format nobody can edit is just a slower way of storing
`b0..a2`. Only the side named is sent: echoing the other one back as it was
last read would race a change made elsewhere in the two seconds since.

The vector is validated by the *node*, not by the Hub. It is the node's
fence, its clamps and its parser that decide what a vector is, and a second
opinion on the Hub would be one more place for the two to disagree. What
comes back in `/status` is the normalised form of what was typed, so
`104/3.8/-9` is stored and shown as `104.0/3.80/-9.0`.

The device table rebuilds every two seconds and pauses while one of these
controls is in use, or an edit would be thrown away mid-keystroke. Focus
alone is not enough to detect that: clicking Save blurs the field first,
and a rebuild landing in that gap replaces the button mid-click so the
press goes nowhere.

A node on firmware older than 0.46.0 says so rather than showing an empty
box.

### Populating the vectors

From measurements that already exist. Fitting reads the stored analysis, so
any room measured before the correction stage existed can be fitted and
written without measuring again. A fresh measurement is needed only to
*check* the result — which is the interesting half.
