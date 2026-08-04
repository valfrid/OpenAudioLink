# OpenAudioLink Hardware Baseline

## Reference Receiver

```text
ESP32-S3
    -> I²S BCLK/LRCK/DATA
PCM5102A DAC
    -> 3.5 mm stereo line output
```

Expected board supply:

- DAC module VIN: normally 5 V
- I²S logic: 3.3 V
- no external MCLK normally required
- common ground with ESP32

Exact board pinout must be verified before wiring.

## Consumer output options

The PCM5102A above is the specified route and the one decision 2's
synchronisation goals rely on. Two alternatives are worth having written
down, because they suit rooms the reference does not. Decision 8 is the
general point: how a Consumer emits audio is a profile property, so these
are additive — new profiles, not a new architecture.

| | Output | Cost | Suits |
| --- | --- | --- | --- |
| PCM5102A | line level, needs an amplifier | ~28 SEK | a room someone sits and listens in |
| MAX98357A | 3 W to a driver, no amplifier | ~20 SEK | bathroom, kitchen, workshop, party |
| CX31993 USB dongle | headphone amp, shielded | ~120 SEK | desk or headphones — **blocked**, see below |

### MAX98357A — DAC and amplifier in one chip

Status: identified, not yet bought or tested.

An I²S DAC and a 3.2 W mono Class-D amplifier in a single part, so it
replaces the PCM5102A *and* whatever amplifier would have followed it. A
complete speaker node becomes an ESP32-S3, this board, and a driver — for
less than the DAC alone costs.

The breakout is the common purple clone of Adafruit's design, with pins
`LRC BCLK DIN GAIN SD GND Vin` and a screw terminal for the driver.

| Property | |
| --- | --- |
| Interface | I²S — BCLK, LRCLK, DIN. **No MCLK required** |
| Sample rates | 8–96 kHz; 48 kHz is native, so the RTP profile needs no conversion |
| Output | 3.2 W into 4 Ω at 10 % THD; roughly 2.5 W clean, 1.8 W into 8 Ω |
| Supply | 2.5–5.5 V |
| Gain | pin-selectable 3/6/9/12/15 dB, 9 dB with the pin floating |

Wiring to a XIAO ESP32S3:

| Board | XIAO |
| --- | --- |
| Vin | VUSB (5 V) |
| GND | GND |
| BCLK | D8 (GPIO7) |
| LRC | D10 (GPIO9) |
| DIN | D9 (GPIO8) |
| GAIN | leave floating |
| SD | leave alone |

Any free GPIO works — the S3 routes I²S through its matrix — but D6/D7
carry the UART the serial log comes out of, so they are the two to avoid.

**The SD pin does not need to be set, and that is by design.** It selects
what the chip plays, by voltage:

| SD_MODE | Output |
| --- | --- |
| below 0.16 V | shutdown |
| 0.16–0.77 V | (L+R)/2 |
| 0.77–1.4 V | Right |
| above 1.4 V | Left |

Decision 10 has the firmware write the chosen channel into *both* I²S
slots, so mono, left-only and right-only are identical on the wire and the
amplifier gets the same audio whichever mode it is strapped to. That
decision was written as "no assembly can wire the silent output by
mistake"; this part is exactly the case it protects against. Leave SD at
the board default and let the software profile decide.

The one case that does need it is a real stereo pair: two boards sharing
BCLK, LRCLK and DIN, one strapped Left and one Right, from a single I²S
peripheral. Clone boards do not always use Adafruit's resistor values
around SD, so **measure the pin against the table above** rather than
trusting a recipe written for a different board.

Two practical cautions:

- **Give the amplifier its own 5 V supply**, not the XIAO's USB
  passthrough. 3.2 W into 4 Ω draws over an amp at peaks and the rail will
  sag. Share the ground.
- **Keep it away from the antenna.** A Class-D switching stage beside a
  Wi-Fi radio is a real hazard, and this project has already spent enough
  evenings on radio problems (`LINK-MEASUREMENTS.md`). The XIAO's antenna
  is at one end of the board. If link quality drops when audio plays,
  this is why.

Refitting a dead powered speaker suits this well: the enclosure and driver
are the parts that are expensive and hard to make, and 2.5 W into a real
cabinet is far louder than 2.5 W into a bare driver.

### PCM5102A — wiring

The common GY-PCM5102A module. Header pins vary in order between clones,
so match by **name**, not position.

| Module | XIAO ESP32S3 | |
| --- | --- | --- |
| VIN | VUSB (5V) | the module regulates it down itself; 3V3 also works |
| GND | GND | |
| LCK / LRCK | D10 (GPIO9) | word select |
| DIN | D9 (GPIO8) | data |
| BCK | D8 (GPIO7) | bit clock |
| **SCK** | **the DAC's own GND pad** | see below |

Avoid D6/D7 — those carry the UART the serial log comes out of.

### Laying the two boards out

The header orders are fixed — `SCK BCK DIN LCK GND VIN` on the DAC,
`VUSB GND 3V3 D10 D9 D8 D7` on the XIAO — but which GPIO carries which
signal is not, because the ESP32-S3 routes I²S through its matrix. The
defaults above are chosen so the two boards wire with **no crossed
leads**.

**Rotate the DAC 180° relative to the XIAO**, so `VIN` sits opposite
`VUSB`:

```
  XIAO ESP32S3              PCM5102A
  (USB up)                  (jack down)

  VUSB ●──────────────────● VIN
  GND  ●──────────────────● GND ──┐
  3V3  ●                  ● LCK   │
  D10  ●─────────────────╱        │
  D9   ●─────────────────╱ DIN    │
  D8   ●─────────────────╱ BCK    │
  D7   ●   (leave free)   ● SCK ──┘
```

Two straight power wires, then three parallel diagonals each stepping
down one row, because `3V3` on the XIAO has no counterpart on the DAC and
absorbs the offset.

**`SCK` is jumpered to the DAC's own `GND` pad**, four rows up on the
same header. It never enters the gap between the boards, so it cannot
cross anything — and it keeps the grounding decision physically next to
the pin it applies to.

The MAX98357A has a different header order again, so it wires with one
crossed lead at these defaults. That is a soldering inconvenience and
nothing more; change the GPIOs in `menuconfig` if it bothers you.

**Two things account for almost every "wired correctly, no sound".**

**SCK must be tied to GND.** It is the system-clock *input*, and grounding
it tells the PCM5102A to run its internal PLL off the bit clock instead.
Left floating, the DAC never locks and you get silence or noise. The ESP32
can output a master clock, but not needing one is the reason this part was
chosen.

**XSMT must be high.** It is an active-low soft mute, so a low or floating
XSMT is a hard mute that looks exactly like a dead DAC. Most modules pull
it up or have a jumper; confirm it rather than assume.

### The four configuration pins

FLT, DEMP, XSMT and FMT set the DAC's behaviour, and each appears twice on
the board: as a solder jumper on the back, and as a through-hole on the
left header. They want:

| Pin | Set to | Meaning |
| --- | --- | --- |
| FLT | low | normal-latency filter |
| DEMP | low | de-emphasis off |
| **XSMT** | **high** | **unmuted** |
| FMT | low | I²S format, which is what the ESP32 emits |

**By jumper.** The `1 2 3 4` on the front are `H1L` to `H4L` on the back,
in that order — 1 is FLT, 2 is DEMP, 3 is XSMT, 4 is FMT. Each block has
three pads: the middle one is the pin, bridged to the **H** side for 3.3 V
or the **L** side for ground. Never bridge both; that shorts the rail to
ground.

**By header, which is easier.** The left column carries
`FLT DEMP XSMT FMT A3V3 AGND ROUT AGND LROUT`, so the four pins and both
references sit on one header:

| From | To |
| --- | --- |
| XSMT (3rd) | **A3V3** (5th) — two rows apart |
| FLT (1st) | AGND |
| DEMP (2nd) | AGND |
| FMT (4th) | AGND |

`A3V3` is the board's own regulated rail and XSMT is a CMOS input drawing
microamps, so it is a perfectly good source for the logic high.

**Check before soldering.** These boards vary and many arrive already
configured. Continuity from each pin on the left header settles it in two
minutes: FLT, DEMP and FMT should read connected to ground, and XSMT to
A3V3 rather than to ground. Whatever is already right needs nothing.

If only one pin gets checked, make it XSMT. FMT high would at least be
*audible* — left-justified data misread as I²S sounds like loud noise —
whereas XSMT low is silence, and silence is the failure that looks like a
dead board, a dead wire, a dead DAC or a firmware fault all at once.

Keep the BCK wire short. At 48 kHz, stereo, 32-bit slots it runs at
3.072 MHz, which is not fast but is fast enough that a long unshielded
jumper next to a Wi-Fi antenna is asking for trouble.

### PCM1808 — wiring, and why it can wait

The ADC is the **Producer** side, and nothing is waiting on it: the Hub
already produces, from system audio and now from Spotify. The DAC is the
blocked path — it is what makes this project audible for the first time —
so wire that first and get one thing working before adding a second.

When it is time, the decision to make first is which end owns the clock:

- **PCM1808 as master.** The module's onboard oscillator drives its SCK,
  and the PCM1808 generates BCK and LRCK. The ESP32's I²S then runs as a
  slave. Fewer wires from the ESP, but ESP-IDF's I²S slave mode is the
  fussier of the two.
- **ESP32 as master.** The ESP supplies MCLK, BCK and LRCK; the PCM1808
  follows. The ESP32-S3 can route MCLK to any pin through its GPIO matrix,
  and it keeps both audio directions on the same clock — which is what
  decision 2's synchronisation goals want.

The `MD0`/`MD1` pins select the mode and the SCK-to-sample-rate ratio, and
the mapping differs between modules. Read the silkscreen and the module's
own datasheet before wiring those two; the rest (VCC 5V, GND, OUT, BCK,
LRC) is straightforward.

### CX31993 USB dongle

Status: recorded, blocked, nothing scheduled.

Decision 8 appendix A covers this in full and the analysis is not repeated
here. The short version: it gives a shielded, finished output stage with a
headphone amplifier, and it requires the ESP32-S3 to be a USB **host**,
which needs a board that can source VBUS and a spare port for the console.
That is a board question, not a software one, and it is unsolved. Drift
correction also gets harder, because a USB DAC owns its own clock where
I²S lets the ESP trim its own through the APLL.

## Reference Analog Source

```text
3.5 mm stereo line input
    -> PCM1808 ADC with onboard oscillator
    -> I²S DATA/BCLK/LRCK
ESP32-S3
```

Selected module characteristics:

- 24-bit stereo ADC
- 48 kHz and 96 kHz modes
- onboard audio oscillator
- master/slave selectable
- own power regulation
- 3.5 mm input
- approximately 40 x 50 mm

For OpenAudioLink, the initial target is 24-bit, 48 kHz, stereo.

## Approximate component cost

- ESP32-S3 Super Mini: about 50 SEK
- PCM5102A DAC module: about 28 SEK
- MAX98357A amplifier module: about 20 SEK
- PCM1808 ADC module: about 75 SEK
- CX31993 USB-C dongle DAC: about 120 SEK

Approximate node cost before enclosure, supply and connectors:

- Receiver, line out to an amplifier: about 78 SEK
- Receiver, driving a speaker directly: about 70 SEK
- Analog Source: about 125 SEK

The self-amplified node is the cheaper of the two receivers, which is
worth noticing: it removes a part rather than adding one.

## Temporary development hardware

ESP32-C3 boards already available may be used for early software development.

Use them for control, network and RTP experiments, while keeping the audio abstraction portable to ESP32-S3.

## Initial hardware tests

### DAC test

- generate a 1 kHz sine wave
- output 24-bit/48 kHz I²S
- verify both channels
- measure noise and clipping
- test USB-powered and cleaner external 5 V supply

### MAX98357A test

The same sine, plus the two things that are specific to an amplifier
sharing a board with a radio:

- confirm sound with SD and GAIN both left unconnected, which is the
  claim that the board needs no soldering decisions
- set each of the four channel profiles in turn and confirm all four are
  audible and identical, which is what decision 10 promises when the
  chosen channel goes into both I²S slots
- watch RSSI and the RTP counters while playing at volume, on the same
  node, to see whether the switching stage costs anything on the air
- run from USB and from a separate 5 V supply, and listen for the
  difference at high volume

### ADC test

- capture line input at 24-bit/48 kHz
- verify master/slave configuration
- confirm actual BCLK and LRCK
- measure silence noise floor
- test channel balance
- determine clipping input level

### End-to-end test

- ADC node captures audio
- RTP/UDP transport
- receiver node plays audio
- measure latency, packet loss behaviour and drift

## Bringing up the DAC

Firmware 0.9.0 adds the playout path: a Consumer starts an I²S output at
boot, buffers what arrives and feeds the DAC from it. Pins come from
`idf.py menuconfig` under **OpenAudioLink Test Node**, defaulting to the
table above.

The first sound this project makes should be the Hub's test tone:

1. Wire the DAC, headphones or an amplifier on its output, and flash 0.9.0.
2. The log says `I2S out on BCLK=7 WS=9 DOUT=8, 48000 Hz, stereo, 20 ms
   buffer`. If it does not, audio never started and the reason is on the
   line after it — the node still receives and still counts, so this is
   survivable rather than fatal.
3. From the Hub, send a test tone to the node: **Stream → test tone**, or
   `POST /api/stream/test-tone` with the node as the destination.
4. `GET /stream` on the node now carries a `playout` object beside the
   reception statistics.

Read those two together, because they answer the same question from
different sides:

| What you see | What it means |
| --- | --- |
| `playing: true`, `silenceFrames` steady | Working. A click would be loss on the air, and `stats` says so |
| `silenceFrames` climbing steadily | The ring is running dry — raise `OAL_PLAYOUT_MS`, or the network is losing packets |
| `droppedFrames` climbing steadily | The ring is overfilling, which means the sender's clock is faster than this DAC's |
| `running: false` | I²S never started; the pins are wrong or in use |
| Silence, everything else healthy | XSMT, or `SCK` not grounded. See the wiring notes above |

**A click and a dropout are different faults**, and this is the pairing
that tells them apart: loss on the air shows in `stats`, a ring that ran
dry shows in `silenceFrames`, and drift shows as one of them climbing
slowly while the other stays at zero.

Expect about **40 ms** from the Hub to the speaker: 20 ms of playout
buffer and 20 ms of DMA. Both are visible in the log line and adjustable.

### What is deliberately not solved yet

**Drift.** The sender's 48 kHz and the DAC's differ by a few parts per
million, so the ring slowly fills or empties over hours. Both ends are
handled — full drops the oldest, empty plays silence — and both are
counted, so the effect is visible rather than mysterious. Correcting it by
trimming the ESP32's APLL belongs with decision 2's multi-speaker
synchronisation, and doing it now would be tuning something nothing yet
depends on.

**Concealment.** A lost packet is 5 ms of silence, not an interpolation.
That is audible on a sustained tone and nearly inaudible on music, and it
is worth measuring before deciding it needs fixing.
