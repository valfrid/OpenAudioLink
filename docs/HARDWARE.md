# OpenAudioLink Hardware Baseline

> Photographs of all of this, assembled and working, are in
> [`hardware-photos/`](hardware-photos/). Worth a look before ordering
> anything — a node turns out to be two small boards and a wire, and the
> pictures answer "is that really all of it" faster than a parts list does.

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
| CX31993 USB dongle | headphone amp, shielded | ~120 SEK | desk or headphones — **hosted and playing**, see below |

### Which to put in a fixed room

**The PCM5102A, and for three reasons that only became clear once both were
running.**

**Latency.** The I²S path holds about 20 ms after the ring — four DMA
descriptors. The USB path holds about 100 ms, in the UAC host driver's own
buffer, before the dongle's. Five times as much, and it is the whole of the
difference `delayMs` was added to correct.

**Two matched nodes need no offset between them.** The 50 ms trim measured
by ear existed because one node was USB and the other I²S. Two PCM5102A
nodes have the same output stage and therefore the same latency, so their
`delayMs` should simply be *equal*. If two identical DACs still sound out of
step, that is a finding worth chasing rather than trimming away — nothing in
the design accounts for it.

**It leaves the USB port free**, which is the port wired Ethernet needs
(see "Accessory: wired Ethernet"). A dongle node can never be a wired node;
there is one socket. For a speaker that lives in one room — the case where
Ethernet is plausible and worth the most — that forecloses the better
network before it is tried.

What the dongle is still right for is a desk or a pair of headphones, where
its amplifier and shielding are the point, latency against another speaker
does not arise, and nothing was going to be wired anyway.

*Not* a clock argument. It is tempting to assume a USB dongle brings its own
timing, and this one does not: `USB-AUDIO.md` records it measured as a
synchronous endpoint with no feedback, following the host's SOF, which
descends from the node's own crystal. Decision 12's clock authority holds
either way.

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

### The UART, when the console cannot use USB

D6 and D7 are the two pads furthest from the USB connector, one on each
side of the board — easy to find, easy to confuse with each other.

| XIAO | GPIO | Function | Wire it to the adapter's |
| --- | --- | --- | --- |
| D6 | GPIO43 | U0**TX**D — the ESP talks | **RX** |
| D7 | GPIO44 | U0**RX**D — the ESP listens | **TX** |

Crossed, as always: the direction in the name is the direction relative to
whichever board the label is printed on. Share a ground, set the adapter to
**3.3 V logic**, and use 115 200 baud.

**To read a log, D6 and GND are enough.** The TX side only matters for
typing into a console, which nothing here has.

This normally does not come up, because the console runs over the native USB
port. It matters when USB is doing something else, and on this hardware that
is no longer only an experiment: **a Consumer with `output` set to `usb`
puts USB-OTG into host mode**, and the S3 shares one PHY between
USB-Serial/JTAG and USB-OTG. The console then has nowhere to go but here.

So a dongle node is a node you read over UART, or not at all. Worth knowing
before troubleshooting one that has gone quiet — the absent console is the
dongle working, not the node dead.

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

The alternative — carrying ground across from the XIAO's `GND` — was
tried on the first build and is worse for a reason that is not obvious
until it happens: the wire has to arc over the whole board, and landing
one hole short puts it on `D7` instead. That looks like a finished
joint, shorts a GPIO to ground harmlessly enough that nothing complains,
and leaves `SCK` floating. The symptom is silence, which is the symptom
of everything else too.

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

### PCM1808 — wiring

Firmware 0.10.3 adds the capture path: a Producer brings up an I²S input at
boot and sends what it captures. It is on by default, so the published
artifact is the image an Analog Source runs — decision 5 wants one binary
for every role, and nothing starts unless the node actually holds the
producer role.

**Which end owns the clock is the board's decision, not a preference**, and
the two kinds of board differ:

- **A bare PCM1808** has no oscillator. The ESP32 must supply MCLK, BCK and
  LRCK and the module follows. This is the better arrangement where it is
  available, because it puts capture and playback on **one clock** — what
  decision 12's synchronisation wants, and what makes a node that both
  records and plays one device rather than two sharing a box.
- **A self-clocked module** — the `GLA ANA TO I2S 96K/24BIT` board sold as
  "PCM1808 3.5mm Stereo Analog Audio Signal to I2S output", and its
  relatives —
  carries its own **24.576 MHz oscillator**. The PCM1808 runs from it and
  generates BCK and LRCK, and the header exposes **MCLK as an output**. The
  ESP32 has no choice but to follow.

The board this was first built against is the second kind, so
`OAL_ADC_SLAVE` defaults to **on**. Getting it wrong is not subtle: two
masters driving one clock line produce nothing usable, and the symptom is
silence.

Being the slave costs what being master would have bought. A node that
captures from a self-clocked ADC *and* plays a stream has two clock domains
inside it — the ADC's crystal and the DAC's. That is a real cost, and the
board imposes it.

**Set the module's options first — the back of the board carries the
tables, and the default is wrong for us.**

| `M/S OPTION` | OP2 | OP3 |
| --- | --- | --- |
| M-96K | open | open |
| **M-48K** | **short** | open |
| SLAVE | short | short |

| `FORMAT OPTION` | OP1 |
| --- | --- |
| **I2S-24** | **open** |
| LJ-24 | short |

**An untouched board is `M-96K`: master at 96 kHz.** That is twice the
profile's rate, and 96 000 frames a second sent down a 48 kHz stream plays
back at half speed. **Short OP2** and it becomes master at 48 kHz, which is
exactly the wire format. Leave OP1 open for I²S-24, which is what the
firmware expects — `LJ-24` is left-justified and would sound like quiet
distortion rather than like a mistake.

`SLAVE` is the third row, and it is tempting because it would put capture
and playback on one clock. Resist it for now: the module's oscillator still
feeds the PCM1808's system clock, so BCK and LRCK arriving from an unrelated
ESP32 clock is not an arrangement the part is specified for. `M-48K` is the
supported answer and needs one solder bridge.

**Wiring** — the header reads `DATA BCLK LRCK MCLK GND` down the board, and
the pins are ordered to match, so the leads run parallel and nothing
crosses:

| Module | XIAO ESP32S3 | |
| --- | --- | --- |
| DATA | D2 (GPIO3) | audio into the ESP |
| BCLK | D3 (GPIO4) | bit clock, **an input to the ESP** |
| LRCK | D4 (GPIO5) | word select, **an input to the ESP** |
| MCLK | **leave unconnected** | an *output* on this board; two drivers on one line is how boards get damaged |
| GND | GND | on the XIAO's other side, with VDD |
| VDD / GND on `POWER` | see below | |

D2–D5 are four adjacent pins on the side **opposite** the DAC's
`D8`/`D9`/`D10`, so a node can carry both boards without either reaching
across. The ESP32-S3 has two I²S peripherals, so one node really can capture
a turntable and play a stream at once. `D5` stays free here and carries MCLK
only when driving a bare PCM1808, where the same four pins still map in
header order.

**Power is 5–12 V on the `VDD`/`GND` header**, not 3.3 V. The board
regulates it down itself, so `VUSB` on the XIAO is the straightforward
choice and an external supply works for a permanent install.

That has a consequence worth stating rather than assuming: because the
PCM1808 runs from the board's own 3.3 V rail, **the I²S outputs are 3.3 V
logic whatever you feed `VDD`**. A 12 V supply does not put 12 V anywhere
near the ESP32. If a future board of this kind regulates differently, that
is the thing to check before wiring the clocks in — the ESP32-S3's inputs
are not 5 V tolerant.

Powering it from `VUSB` means the ADC only lives while the XIAO has USB
power, which is the same caveat the DAC has and fine on a bench.

One difference from the DAC is worth remembering before blaming the
firmware for a noise: **supply noise matters more to a converter that is
recording than to one that is playing**, because it lands in the captured
signal and nothing downstream can take it out again. `VUSB` also feeds a
Wi-Fi radio in transmit bursts. The module's own regulator gives some
rejection and this may never be audible — but if the capture has a hiss or
a whine that tracks Wi-Fi activity, an independent supply on the `VDD`
header is the first thing to try, and is what the 5–12 V range is for.

The firmware measures the rate rather than assuming it, which is what
catches an `M-96K` board that nobody remembered to strap. The capture trace reports
what actually arrives, and warns when it is not what the wire expects:

```
W (…) oal_capture: ADC is running at about 96000 Hz, not 48000 — check the
      module's rate strapping, or this stream plays at the wrong speed
```

Read that number before troubleshooting anything else about the sound.

Audio in is either the 3.5 mm `JACK1` or the `R_IN GND L_IN` header — the
same signal, so use whichever suits the enclosure.

Expect on the serial log:

```
I (…) oal_capture: I2S in on BCLK=4 WS=5 DIN=6, slave — the ADC sets the rate
I (…) oal_capture: input 5/10/15 ms (min/now/max), 48000 Hz, dropped 0 ms, …
```

That second line is the capture trace, on the same five-second cadence as
the playout one and for the same reason: a rate and a fill together say
which end is wrong, where either alone only says that something is. The
input ring is deliberately small — 40 ms against the playout's 200 — because
they are sized against opposite things. A playout buffer covers the largest
gap a distant sender can leave; this one covers the jitter of a task on the
same chip, and every frame it holds is delay between the needle and the
speaker that nothing downstream will absorb.

### The measurement microphone

Status: **settled — the I²S part is in hand.** The board photographed
carries six pads, `SEL LRCL DOUT BCLK GND 3V`, with an Adafruit logo and
`VIN/Logic: 3.3V` on the face. The `LRCL` is the tell, by the pad test
below: this is an ICS-43434, not the PDM board an earlier delivery
supplied.

That is the fortunate half of the two. `oal_capture` brings up
**standard I²S RX** — Philips slots, 32 bits, ESP as master — and has no
PDM path at all, so the part now in hand is the one the existing capture
code already speaks. The PDM wiring is kept below because a PDM board is
also in the drawer and the two must never be confused, not because
either is still a candidate.

#### Read the pads before soldering

One pad decides it:

| | pads | tell |
| --- | --- | --- |
| **I²S** (ICS-43434, SPH0645) | `3V GND SEL LRCL DOUT BCLK` | **has a word-select** — `LRCL` or `WS` |
| **PDM** (MP34DT01-M and kin) | `3V GND SEL CLK DAT` | **no word-select anywhere** |

Six pads with an `LRCL` is I²S. Five pads with `CLK` and `DAT` is PDM, no
matter what the listing said — mismarked and mis-shipped boards are
ordinary at this price. The chip marking is harder to read than the pads
and not worth squinting at; count the pads.

The difference is not cosmetic:

| | signals | what comes out |
| --- | --- | --- |
| I²S mic | SCK/BCLK, WS/LRCL, SD/DOUT | PCM samples, ready to use |
| PDM mic | CLK, DAT | a 1-bit pulse-density stream at 1–3 MHz |

The ESP32-S3 can read both. Standard I²S RX takes the first; **PDM RX with
a hardware PDM-to-PCM converter** (`SOC_I2S_SUPPORTS_PDM_RX`) takes the
second and decimates to 48 kHz PCM at no CPU cost. Different driver modes,
not different capabilities.

**Either way the ESP is the clock master**, which is the point that governs
the combined box below: neither microphone has an oscillator, and neither
does anything until this end clocks it. That is the opposite of the
self-clocked PCM1808 module, and it is why the two capture inputs can never
share pins.

Both wirings below use **D0 and D1 for the clock and the data**, so the
pin budget in the combined box is the same either way; the I²S part needs
one extra pin for its word select, which is why it takes D5 as well.

---

### PDM microphone — wiring

#### As a stand-alone node

Four wires. The board's SEL pad has a pull-down on it, so left channel is
the default and the pad can be left unconnected.

| Breakout | XIAO ESP32S3 | |
| --- | --- | --- |
| 3V | 3V3 | **3.3 V, not 5** — a bare MEMS part, not a regulated module |
| GND | GND | |
| CLK | D0 (GPIO1) | 1–3 MHz, **an output from the ESP** |
| DAT | D1 (GPIO2) | the pulse stream, into the ESP |
| SEL | leave open, or GND | open or low = left; tie to 3V3 for right |

Nothing else is needed. A stand-alone measurement node is a XIAO, this
board and a USB cable — which is why it is worth building one eventually
even though the combination below avoids having to.

#### Combined with the turntable ADC, in one box

This is the arrangement to build now: one enclosure that is the vinyl
Producer most of the time and the measurement microphone for ten minutes.
**Both converters stay wired; only one captures**, chosen by the `input`
setting in NVS (`protocol/CONTROL.md`) and read at boot.

| | pins | clock role |
| --- | --- | --- |
| PCM1808 module (line) | D2, D3, D4 | **master** — it drives BCLK and LRCK, the ESP follows |
| PDM microphone (mic) | D0, D1 | **slave** — the ESP drives CLK |
| PCM5102A DAC, if fitted | D8, D9, D10 | ESP drives |
| UART console | D6, D7 | leave alone |

The two capture inputs sit on **separate pins and never share one**. That
is not tidiness: the PCM1808 module drives BCLK from its own 24.576 MHz
oscillator whenever it has power, and the ESP drives CLK for the
microphone. Put either pair on one wire and two outputs fight, which
produces nothing usable and reports itself as silence.

Being PDM helps here rather than hurting. It needs **two pins where an I²S
microphone needs three**, so the microphone fits in D0/D1 and leaves D5
spare in a box that already spends D2–D4 on the ADC and D8–D10 on a DAC.

Power in the combined box comes from two rails, and they are not
interchangeable:

- **The microphone takes 3V3** from the XIAO's regulator.
- **The PCM1808 module takes 5–12 V** on its own `VDD` header and
  regulates internally (see its section above).

Feeding the microphone from the ADC module's supply rail is the mistake to
avoid; it is a 3.3 V part.

---

### ICS-43434 I²S microphone — wiring

If the board carries an `LRCL` pad, this is the one. It is the part
`docs/ROOM-CALIBRATION.md` named as its reference, and the better
instrument of the two: 65 dB SNR, a flatter response, 24 bits.

#### Wiring — D0, D1, D5, and it is not a preference

| Breakout | XIAO ESP32S3 | Kconfig | |
| --- | --- | --- | --- |
| 3V / VIN | 3V3 | | **3.3 V** — the silkscreen says so too |
| GND | GND | | |
| BCLK | D0 (GPIO1) | `OAL_MIC_BCLK_GPIO=1` | bit clock, **an output from the ESP** |
| DOUT | D1 (GPIO2) | `OAL_MIC_DIN_GPIO=2` | audio into the ESP |
| LRCL / WS | D5 (GPIO6) | `OAL_MIC_WS_GPIO=6` | word select, **an output from the ESP** |
| SEL | leave open, or GND | | open or low = left; tie to 3V3 for right |

**These three are what is left over, and that is the whole argument.** The
board exposes eleven GPIO pads. D6 and D7 are the UART console, so nine
remain, and a node carrying all three sub-boards needs nine:

| | pins |
| --- | --- |
| PCM5102A DAC | D8, D9, D10 |
| PCM1808 ADC | D2, D3, D4 |
| ICS-43434 microphone | **D0, D1, D5** |

Three plus three plus three against nine free. There is exactly one
assignment that lets every combination exist on every board, and this is
it. Any other choice for the microphone forbids a combination somebody
wants — putting it on D8/D9/D10 reads as tidy on a board with no DAC, and
costs the **speaker that also listens**, which is the arrangement a Google
Home or a Nest Mini is and the one most worth building.

The wiring is not pretty: D0 and D1 sit above the ADC's three pads and D5
below them, so the microphone's leads straddle the ADC's. On a speaker
node the middle three are simply empty. That is the price of the
combination, and it is the right thing to pay.

**D5 is free, despite being `OAL_ADC_MCLK_GPIO`.** `OAL_ADC_SLAVE` defaults
to `y` for the self-clocked "ANA TO I2S" module, and `oal_capture` sets
`.mclk = I2S_GPIO_UNUSED` whenever this end is the slave, so nothing drives
the pin. The one arrangement this forbids is a **bare** PCM1808 — which has
no oscillator, needs a real master clock on D5, and therefore cannot share
a board with the microphone. The module used here is not that part.

No MCLK in any of these. The part needs only the bit and word clocks,
which is why five wires is enough.

#### Combined with the turntable ADC, in one box

Same argument and the same box as the PDM case; one pin more.

| | pins | clock role |
| --- | --- | --- |
| PCM1808 module (line) | D2, D3, D4 | **master** — it drives BCLK and LRCK, the ESP follows |
| ICS-43434 (mic) | D0, D1, **D5** | **slave** — the ESP drives both clocks |
| PCM5102A DAC, if fitted | D8, D9, D10 | ESP drives |
| UART console | D6, D7 | leave alone |

That uses every pin on the ADC side of the board. It fits, with nothing
spare — which is the one practical argument in the PDM part's favour, and
worth knowing before deciding a swap is a pure upgrade.

**Do not put the microphone's data on D2.** It is the PCM1808's `DATA`
pin, and an earlier revision of this document made exactly that mistake:
two outputs on one wire, which produces nothing usable and reports itself
as silence.

**D5 is already spoken for in the config, if not on the board.**
`OAL_ADC_MCLK_GPIO` defaults to GPIO6, which is D5 — the master clock for
a *bare* PCM1808. The self-clocked module used here outputs its own MCLK
and leaves that pin unconnected, so the wire is free; but the firmware
still names it when it brings up line mode. Worth setting `mclk_gpio` to
`-1` in that path before wiring a microphone's word select to the same
pin, so the two can never be configured at once by accident.

Power splits the same way as before — the microphone on **3V3**, the
PCM1808 module on **5–12 V** into its own `VDD` header.

---

### What either part is and is not good for

Worth being plain, because it changes what the calibration results mean.
A PDM MEMS microphone of this class is a **voice-grade part**. It will
show you room modes, a boomy corner, the broad shape of a response curve
and an arrival time — which is what `docs/ROOM-CALIBRATION.md` Phase 1
actually asks for.

It is not a measurement microphone in the instrumentation sense, and no
calibration file ships with it, so absolute SPL and the last few decibels
of flatness are not on offer. Treat the first sweeps as **relative**
measurements — this speaker against that one, this position against
another — rather than as absolute truth about the room.

The ICS-43434 is the better instrument — 65 dB SNR against a PDM part's
typical 61, flatter, 24-bit — and it is what the proposal named. It is
still not a calibrated measurement microphone, so the "read it as
relative" advice survives the upgrade; it just starts from a better
floor.

Mono, either way. One microphone is one pressure reading, which is correct
for measurement — a stereo image is not what a sweep is asking about.

**And the node folds it for you, as of firmware 0.43.0.** The part puts its
sample in whichever half of the frame SEL selects and leaves the other at
digital silence, so an unfolded microphone Producer streams audio in the
left channel and nothing in the right. `oal_capture` now applies the
playout's own `oal_channel_apply` with `LEFT` on the way out, so what
leaves the node is an ordinary centred stream. Nothing downstream needs
configuring — and in particular a consumer's `channel` setting stays a
description of *that speaker* rather than of whatever it happens to be
listening to.

### What the firmware still needs

`oal_capture` brings up **standard I²S RX only**. Firmware 0.31.0 added the
`input` setting and a `mic` branch that selects a second set of pins, which
is right, and how much work is left depends entirely on which board this
turns out to be:

**If it is the ICS-43434**, the existing path is nearly correct — an I²S
master reading bit clock, word select and data is exactly what the part
wants. What needs fixing is the pin defaults: 0.31.0 shipped
`OAL_MIC_DIN_GPIO` defaulting to GPIO3, which is the PCM1808's data pin.
Move the data to D1 (GPIO2) and the word select to D5 (GPIO6), per the
table above.

**If it is PDM**, more is missing:

- PDM RX mode in `oal_capture` (`i2s_channel_init_pdm_rx_mode`), selected
  when `input` is `mic`.
- Kconfig for two mic pins rather than three; `OAL_MIC_WS_GPIO` has
  nothing to configure on a PDM part.
- The port pinned down rather than left to `I2S_NUM_AUTO`: the PDM-to-PCM
  converter is not necessarily present on both I²S controllers, and a
  channel allocated on the wrong one fails at init.

Either way, `input` set to `mic` currently selects a pin set that collides
with the ADC, so it is not ready to wire against yet.

### ESP32-C3 — considered again, still no

Status: rejected. Raised as a stopgap while more S3 boards were on order.

The C3 was scaffolding while the first S3s were in transit, and decision 5
removed its target once they were verified. It came up again as a temporary
third node and the answer is still no, for a reason the intervening work
supplied rather than the original decision:

**One core.** Decision 2 leans on the S3's second core so that I²S
servicing runs apart from Wi-Fi transmit bursts. Every fault found while
making the first speaker audible was a timing fault — a send loop
descheduled, a ring emptied by a gap, a buffer sized against the wrong
thing — so putting the playout deadline and the Wi-Fi stack on one core is
the least attractive experiment this project has available.

The rest is merely work: a new partition table for 4 MB flash (and one
cannot be delivered over the air), a CI target to re-add, and 400 KB of
SRAM to fit ~96 KB of static audio buffers into. One I²S peripheral also
makes a C3 a Consumer or a Producer, never both.

### CX31993 USB dongle

**Status: an ESP32-S3 hosted it and played a tone through it, 2026-08-19,
first attempt.** Still an isolated track — it builds on ESP-IDF 5.4 against
an alpha driver and nothing has been measured over time. `USB-AUDIO.md`
holds the result, what it settled and what it did not; the descriptors and
the analysis are here.

Decision 8 appendix A covers this in full and the analysis is not repeated
here. The short version: it gives a shielded, finished output stage with a
headphone amplifier, and it requires the ESP32-S3 to be a USB **host**,
which needs a board that can source VBUS and a spare port for the console.
That is a board question, not a software one, and it is unsolved. Drift
correction also gets harder, because a USB DAC owns its own clock where
I²S lets the ESP trim its own through the APLL.

**Two references, and one of them settles the software question.**

<https://github.com/Averyy/esp-uac2-host> is a USB Audio Class 2.0 **host**
driver for the ESP32-S3, as an ESP-IDF component. Read 2026-08-15, and it
answers several things this section had open:

- **UAC 2.0 works at full speed.** The ESP32-S3's USB is full-speed only
  (12 Mbps), and there was reason to think UAC 2.0 devices would demand high
  speed. Its own figures put 48 kHz / 24-bit / stereo — exactly our wire
  format — at about a quarter of the bus. Bandwidth is not the obstacle.
- **The clock problem has a standard answer.** This section notes that a USB
  DAC owns its own clock where I²S lets the ESP trim its own through the
  APLL. The driver implements the feedback endpoint, which is how
  asynchronous USB audio resolves that: the device reports its true rate and
  the host adapts. That does not make the drift question disappear, but it
  moves it from "unsolved" to "solved the way USB audio always solves it",
  and it will want reconciling with decision 12's playout contract.
- **It costs an ESP-IDF bump.** The component wants 5.4; this project builds
  on 5.3.1.
- **It carries a microsecond first-frame timestamp**, added by its author for
  synchronising measurements — the same problem `ROOM-CALIBRATION.md` has.
  Their stated use is playing measurement sweeps through a miniDSP.

What it does **not** settle:

- **Alpha, and validated narrowly.** Version 0.1.2, described by its author
  as new and under active testing, exercised against a miniDSP 2x4 HD and an
  ESP32-based simulator. Single-clock UAC2 devices only, and not yet on the
  component registry.
- **The board question is untouched.** This blockage was never a software
  one: the ESP32-S3 must source VBUS as host and still leave a console port
  free. That remains exactly as stated above.

<https://github.com/rbouteiller/airplay-esp32> is an ESP32 AirPlay receiver,
supplied as further prior art and not yet read here.

#### What the dongle's descriptors say

Read 2026-08-18 from the actual part — a CX31993 + MAX97220 "PRO" USB-C
dongle, VID `0x3302` PID `0x336A` — enumerated on Windows 11 and dumped with
USB Device Tree Viewer. This closes the "first thing to check" above, and the
answer is *mostly yes, with two things to verify in firmware*.

**It can run at full speed.** The device publishes a **Device Qualifier
descriptor** with `bNumConfigurations 0x01`, and an **Other Speed
Configuration** to go with it. That is the descriptor pair a device only
provides when it has a second, slower personality — so the dongle is not
high-speed-only, and an ESP32-S3 host (full speed, 12 Mbps) is a viable host
for it. Had the qualifier been absent, this whole idea would have been dead
on the spot.

**There is no UAC 1.0 fallback.** The original question was whether it could
enumerate as UAC 1.0, which some driver stacks find easier. It cannot: the
Audio Control interface header inside the *Other Speed* configuration still
reads `bcdADC 0x0200`. The full-speed personality is UAC 2.0 as well. The
speed changes; the class version does not. So `esp-uac2-host` (or equivalent)
is required — there is no simpler UAC 1.0 route to fall back on.

**Bandwidth is comfortable.** At full speed, the speaker interface's 24-bit
alternate setting declares `wMaxPacketSize 576` bytes at `bInterval 1` (one
packet per millisecond). Our wire format needs 48 000 × 3 × 2 = 288 bytes per
millisecond — half the reserved packet. This matches the driver's own claim
of about a quarter of the bus.

**Two clock sources, where the driver documents one.** The dongle exposes
clock source `0x09` for the speaker path and `0x0A` for the microphone path,
both `bmAttributes 0x03` (internal programmable, not synced to SOF) with
`bmControls 0x07` (frequency host-programmable, validity read-only).
`esp-uac2-host` states support for **single-clock** UAC2 devices. That is the
first real compatibility risk, and it is worth noting the shape of it: the
driver would need to select and program the right clock for the interface it
is streaming to. If only playback is used, only clock `0x09` matters, which
may make it a non-issue in practice — but it is untested.

**Synchronous endpoints, no feedback endpoint.** Every audio endpoint is
isochronous with `bmAttributes 0x0D` — SyncType *Synchronous* — and no
feedback endpoint appears anywhere in the configuration. This matters more
than the clock count: the section above credits `esp-uac2-host` with solving
the drift problem *by implementing the feedback endpoint*, and this device
does not offer one. It instead expects the host to deliver exactly one
packet per SOF and slaves itself to the host's frame timing. For a Consumer
node that is arguably better news than a feedback endpoint would be — the ESP
keeps its own clock authority and decision 12's playout contract stays
intact — but it does mean the drift correction path here is *not* the one the
driver's README describes, and would have to be re-thought rather than
inherited.

The rest, for the record:

- **Speaker path** — Input Terminal 1 (USB Streaming, 2 ch, FL/FR) → Feature
  Unit 2 (mute + per-channel volume) → Output Terminal 3 (Speaker).
  Interface 1 alternates 1/2/3 are 16/24/32-bit. 48 kHz / 24-bit / stereo is
  present, which is exactly our wire format, unconverted.
- **Microphone path** — Input Terminal 4 (Microphone, **1 channel**) →
  Feature Unit 5 → Output Terminal 6. Interface 2 alternates 1/2 are
  16/24-bit. Mono only, which is worth knowing before anyone considers this
  dongle for `LISTENING.md`'s Listener role — adequate for a measurement
  microphone, useless for anything stereo.
- **Interface 3 is HID**, the consumer volume/play keys.
- `bNumConfigurations 0x01`, bus-powered, `MaxPower 0x32` — **100 mA**. Modest,
  but it is current the ESP32-S3 board must be able to source as host, which
  is the unsolved board question above and not a software one.
- The tool flags a firmware quirk in the Interface Association Descriptor
  (`bInterfaceCount must be greater than 1`). Cosmetic on Windows; a stricter
  host stack could be less forgiving.

## Accessory: wired Ethernet, to remove an air hop

**Not built. Proposed, with the measurement that motivates it.**

Run 35 measured what a second air hop costs. A stream sourced from a node
crosses the air twice on one channel — up from the producer, back down to
the consumer — where a Hub-sourced stream crosses once, because the Hub
reaches the access point over Ethernet. The stall rate roughly doubles:

| path | stalls |
| --- | --- |
| Hub → node, one air hop | 6 740 – 9 006 ppm |
| node → AP → node, two air hops | 12 338 – 12 965 ppm |

The buffer absorbs it — 10 ppm late either way — but the airtime is spent
whether or not it is survived, and it is spent on the channel every other
node shares.

**A USB-C to Ethernet adapter on the node removes its own hop.** Wiring the
*producer* is worth the most: that hop is upstream of every consumer, so
removing it takes the shared cost out for all of them at once. Wiring a
consumer only helps that one.

### What it would take

The ESP32-S3 is already a USB host in this project — that is how the
CX31993 audio dongle works — so the peripheral and the pattern are proven.
What is new is the class driver.

- **`iot_usbh_ecm`**, an Espressif ESP-IoT-Solution component, is a USB host
  driver for CDC-ECM: Ethernet frames encapsulated in USB packets. Its
  examples list ESP32-S3 among the supported targets.
- **RTL8152/RTL8153** dongles are reported working with an ECM host driver
  on the S3, which is the cheap and common chipset.

### The candidate adapter, and the exact catch

**Cudy UE10C, RTL8153, about 109 SEK.**

The RTL8153 reports `bNumConfigurations 2`, and which one the host picks
decides everything:

| configuration | interface |
| --- | --- |
| 1 (the default) | vendor-specific Realtek — needs the `r8152` protocol |
| **2** | **CDC-ECM** — what `iot_usbh_ecm` speaks |

A host that enumerates the adapter and accepts the default gets the vendor
interface and no driver to talk to it with. Linux carries a whole separate
driver, `drivers/net/usb/r8153_ecm.c`, for exactly the case where the
vendor one is unavailable and configuration 2 is used instead.

So the work is not writing a driver, it is **selecting configuration 2 at
enumeration** — which is precisely what
`CONFIG_USB_HOST_ENABLE_ENUM_FILTER_CALLBACK` exists for, and the same knob
the component's own notes call out for CH397A. That turns the main unknown
from "will this chipset work" into one specific thing to get right.

The RTL8152 is the 100 Mbit part and the RTL8153 the gigabit one; the
two-configuration arrangement is the 8153's.

### The open question that is actually harder

**Powering it.** This file already records twice that the ESP32-S3 must
source VBUS to act as host, and that this is a board question rather than a
software one. A gigabit Ethernet adapter makes it sharper: an RTL8153
negotiating a gigabit link draws a few hundred milliamps, where the CX31993
audio dongle draws tens. The assembled nodes in `hardware-photos/` are
already fed through soldered power leads rather than the USB-C socket, which
is the shape of the answer, but the supply has to be sized for it.

**Firmware cannot pin the link to 100 Mbit, and it is not the fix anyway.**

CDC-ECM has no request for it. `ConnectionSpeedChange` runs the other way —
device to host, reporting the speed that was negotiated — while the
class-specific requests the host may send are the packet and multicast
filters and the statistics. Link speed is settled PHY to PHY between the
adapter and the switch port, and the host is not a party to it. Changing
what the adapter advertises means writing its PHY registers over
vendor-specific control transfers, which is the `r8152` work that choosing
ECM exists to avoid: implement it and ECM is no longer needed.

Where it *can* be set, if it turns out to be wanted:

- **A two-pair cable, and no configuration anywhere.** 100BASE-TX uses pairs
  1-2 and 3-6; 1000BASE-T needs all four. With the others absent, gigabit
  cannot be negotiated and the link settles at 100M. An old 100M patch lead
  does this by construction, works behind an unmanaged switch, and is undone
  by swapping the cable.
- **A managed port advertising 100M full only.** Restrict the
  advertisement; do not disable auto-negotiation on one side. Forcing one
  end while the other negotiates is the classic duplex mismatch — the link
  comes up, one end runs half and the other full, and the result is late
  collisions and throughput that reads like a failing cable. A poor thing to
  introduce into a path being made quieter.

The saving is worth perhaps 80-100 mA, so **the supply is the real answer**,
not the link speed. The assembled nodes are already fed through soldered
leads rather than the USB-C socket; sizing that rail for the adapter settles
it, and a two-pair cable is a cheap extra if the margin is tight.

Firmware *can* read the negotiated speed from the notification, which is
worth surfacing in `/status`: "wired, 100M" against "wired, 1G" is the kind
of fact that explains a power draw or a link that will not come up.

**Latency and jitter through the USB path remain unmeasured**, and this
project has twice found that *when* packets arrive matters far more than how
many. A USB Ethernet path that delivers in clumps would move the problem
rather than remove it. `arrivalGaps` and the margin buckets are already in
place to tell the difference, so the experiment is cheap once it enumerates.

**The board has one USB port**, so this and the CX31993 audio dongle are
mutually exclusive. Ethernet nodes are I²S nodes — a PCM5102A DAC or a
PCM1808 ADC — which is the deployment this is aimed at anyway. Power then
has to come from the BAT pads or the 5 V pin rather than the USB-C socket,
which is already how the assembled nodes in `hardware-photos/` are fed.

### What a fully wired path would actually be worth

Fixed nodes — the speakers that live in one room and never move — are the
ones worth wiring, and wiring them changes the arithmetic rather than just
improving it.

A Hub-sourced stream (Spotify, radio) to a wired node crosses **no air at
all**: the Hub already reaches the access point over Ethernet, so the whole
path becomes switched. Wire the turntable producer too and node-to-node
joins it.

**The buffer is sized for a number that would stop existing.** 400 ms of
ring and a 200 ms target are there to survive a measured 373 ms radio stall.
Switched Ethernet at 2.3 Mbit/s has no contention, no retries and no rate
adaptation; `arrivalGaps` should collapse. If the worst gap becomes tens of
milliseconds, the target follows, and the 1.5× trim rule turns a 50 ms
target into roughly **95 ms air to ear against today's 320**. That reopens
the question decision 17 left unanswered — whether someone at a turntable
could live with the latency.

Three things stand in the way, and two are already in this project's own
measurements.

**The bottleneck moves to the sender.** `oal_playout.c` records that a
Windows sender wakes on a 15.6 ms system timer and emits packets in clumps
of three. That is the Hub, not the network. Wire everything and the floor
becomes the sender's cadence, so the target cannot go far below ~50 ms until
the Hub asks Windows for a better timer — which becomes worth doing only
once the network has stopped dominating.

**Wired is not automatically clean.** Run 30 measured Energy Efficient
Ethernet on the Hub's I219-LM producing 145 ms stalls and 101 ms sends, cut
to 40 and 11 by disabling it. A switch port with EEE enabled would
reintroduce exactly the stalls the cable was run to escape, and it would
present as a mystery because Ethernet is supposed to be perfect. Check every
port in the path.

**A group runs at its slowest member.** Two speakers in one room must play in
step, so a wired node at 50 ms beside a Wi-Fi node at 300 ms is a quarter of
a second apart and unusable. Only an all-wired group can run tight; a group
with one Wi-Fi member runs at Wi-Fi depth and its wired members gain
reliability but no latency.

That last point is the strongest case for splitting `delayMs`, which today
carries both a group's buffer depth and a node's output-stage offset (see
`TUNING.md`). A group-wide `targetMs` chosen by the worst path in the group,
plus a per-node `alignMs`, would let a wired pair run at 90 ms while a
Wi-Fi speaker elsewhere runs at 320 — each aligned within itself, neither
dragging the other.

### The alternative worth pricing against it

**W5500 SPI Ethernet** is supported natively by ESP-IDF (`esp_eth`), needs no
USB class driver, and — the real advantage — **leaves the USB port free**, so
a node could have wired Ethernet *and* the USB audio dongle at once. It
costs four SPI pins plus interrupt and reset against the XIAO's eleven, and
a soldered module instead of a plug-in adapter.

Neither has been tried. The USB route is cheaper and reversible; the SPI
route is better supported and does not consume the port.

## Accessory: a second power feed, for a node whose USB port is taken

Status: in hand and fitted to the USB-audio Consumer
(`hardware-photos/09-consumer-usb-with-pd-trigger.jpg`).

A node hosting a USB peripheral — the CX31993 audio dongle, or the Ethernet
adapter above — has no USB-C socket left to be powered through, and it must
also source VBUS for the thing it is hosting. Power therefore arrives on the
5 V and GND pads instead, which is how the assembled nodes in
`hardware-photos/` are already fed.

**A USB-C PD trigger board is what makes that work from a modern charger.**

### Why it is needed at all

A USB-C source applies no voltage until it detects a sink. Detection is the
sink presenting Rd pull-downs on CC1/CC2; without them the port stays dead.
Solder bare wires to a USB-C charger and you get nothing — where a USB-A
charger of the same vintage would hand you 5 V unconditionally, because
USB-A has no such handshake.

So the trigger board is a small PD sink controller: it presents the CC
resistors, the charger turns on, and 5 V appears on its output pads. The
voltage-selection pads are the second half of the same chip — shorting one
negotiates 9, 12, 15 or 20 V over PD.

### Leave every voltage pad open

**This is the part that can destroy a node.** The XIAO's 5 V pin is a 5 V
pin. Shorting the 9 V pad puts nine volts on it, and the 20 V pad puts
twenty — into the board, and through it into whatever USB peripheral is
being hosted. Unshorted is 5 V, which is the only setting this project
wants.

**On the board actually bought, the selection pads carry no voltage
silkscreen at all** — they are three pads marked `1`, `2`, `3` beside the
`+` and `−` output pair, and nothing on the board says which is which
(`hardware-photos/08-usb-c-pd-trigger.jpg`). That makes the instruction
above easier rather than harder to follow: leave all three open and the
question never arises. It also means there is no way to verify a guess from
the board itself, so if a higher voltage is ever wanted, **measure the
output on a meter before connecting anything to it** — a mislabelled guess
costs the XIAO, the dongle, and whatever the dongle is plugged into.

The higher voltages are genuinely useful, just not here: they are for
feeding a buck converter, or a class-D amplifier that wants more headroom
than 5 V allows. Neither is part of a node today. If one ever is, the
regulator goes between the trigger board and everything else, never after.

### What it buys beyond making the charger work

Current headroom. A phone charger negotiating 5 V through a proper PD sink
will supply far more than the few hundred milliamps a laptop port offers
grudgingly, which is exactly the margin the gigabit Ethernet adapter above
needs and the reason its power question was left open rather than solved.

### Worth checking on the first one

That the board's 5 V rail reaches the hosted peripheral through the XIAO's
own port. This works on the assembled USB-audio node already — the dongle
plays — so the 5 V pad and VBUS are connected on that board. It is still the
first thing to measure if a peripheral enumerates and then browns out under
load, because a diode in that path would pass a light load and sag under a
heavy one.

### What the assembled node looks like

Four things in a line, and no enclosure yet: the charger cable into the
trigger board's USB-C socket, the trigger board's `+` and `−` to the XIAO's
5 V and GND pads, the CX31993 dongle in the XIAO's own USB-C port, and the
external antenna on its u.FL lead. The dongle's 3.5 mm output goes on to
whatever is being driven.

Note which socket is which, because they look alike and only one of them is
an input: **the charger goes to the trigger board, never to the XIAO.** The
XIAO's port is the one hosting the dongle, and that is the whole reason
this accessory exists.

## Supply quality, and what a capacitor will and will not buy

A node's playback clock was measured **178 ppm slow** on one power supply
and inside ±20 ppm on two others, on the same board. The error followed the
supply across a swap onto a second board, so it is the supply and not the
crystal.

That is a large pull for supply variation, and the mechanism is **not
established**. The likeliest candidate is transient sag: `VUSB` feeds a
Wi-Fi radio that draws in bursts, and a supply with high output impedance —
or a thin cable — dips on every transmission. What that does to the 40 MHz
oscillator's operating point is plausible rather than proven. A scope on
the 3V3 rail during transmission would settle it, and nobody has looked.

**What is worth fitting, in order of likely effect:**

| | | |
| --- | --- | --- |
| **Bulk, 100–470 µF across the 5 V input** | electrolytic or low-ESR polymer, close to the board | The one that addresses burst sag, because it holds charge across a transmission |
| 100 nF ceramic at the module | | Already present on the XIAO and inside the module. Adding more does little |

So the useful part is the **bulk** capacitor, not another decoupler. Two
cautions: it will not rescue a supply that sags below the regulator's
dropout and stays there — that is a cable or a supply problem, not a
filtering one — and a large capacitance across a USB input draws an inrush
spike at plug-in that some supplies answer by shutting down. 100–220 µF is
usually safe; past that, an inrush limiter earns its place.

**None of this is required for working audio.** The playout's trim absorbed
that 178 ppm node all night and held its speaker within 4 ms of its
partner. This is about a node not depending on which charger came to hand,
which matters more for somebody building from these notes than it did for
the bench that found it.

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
- Cudy UE10C USB-C Ethernet adapter (RTL8153): about 109 SEK — accessory,
  not yet working; see "Accessory: wired Ethernet" above
- USB-C PD trigger board (second power feed): a few SEK — accessory; see
  "Accessory: a second power feed" above, and leave every voltage pad open

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

Verified on hardware 2026-08-07 (`LINK-MEASUREMENTS.md` run 18) except
where noted.

- ~~capture line input at 24-bit/48 kHz~~ — 47 998 Hz measured, 0 read errors
- ~~verify master/slave configuration~~ — slave; the module clocks itself
- ~~confirm actual BCLK and LRCK~~ — reported by the capture trace
- ~~test channel balance~~ — −5 / −7 dBFS, both channels live
- measure silence noise floor — the meter can now answer this; not yet done
- determine clipping input level — above −3 dBFS clips; the practical
  headroom on this turntable and preamp has not been characterised

### Is anything actually being captured?

The question the first turntable raises, and until firmware 0.12.0 nothing
could answer it.

**Every counter the capture path reported was identical whether a record
was playing or the cable was lying on the floor.** The rate, the buffer
fill, the drop count, the read errors — all of them count *frames*, and a
powered ADC clocks out frames regardless of what is on its inputs. So a
trace reading

```
oal_capture: input 40/40/40 ms (min/now/max), 47998 Hz, dropped 584980 ms,
             silence 0 ms, underruns 0, read errors 0
```

says the ADC is alive, clocked correctly and delivering — and says nothing
whatsoever about whether a turntable is connected to it.

From 0.12.0 the trace carries a peak level, and so does `GET /status`:

```
oal_capture: input idle, level L -14 R -15 dBFS, 47998 Hz, read errors 0
             — no stream is taking it
oal_capture: input idle, level SILENT — nothing on the input, 47998 Hz, …
```

Measured continuously from boot rather than only while streaming, because
the person asking is standing at the turntable with nothing playing. Both
channels separately, because half a turntable failing is the ordinary
fault — a lifted ground, a bad RCA, a worn cartridge coil — and one number
for both reports that as merely quiet.

Reading the numbers:

| Peak        | Means                                             |
| ----------- | ------------------------------------------------- |
| `-120`      | digital silence. Nothing is reaching the ADC      |
| −60 to −40  | the ADC's own noise floor, or a source turned off |
| −20 to −6   | a healthy line-level signal                       |
| above −3    | the preamp is close to clipping; turn it down     |

The Hub shows the same reading in the device table's **Line in** column,
and the switchboard puts it under each "Line in on …" tile — so a phone
standing next to the turntable says *signal −14 / −15 dB* or *silent —
nothing on the input* without anything being started.

### That "dropped" figure is not a fault

An idle capture ring fills and then discards its oldest frames forever,
which is correct: live audio should stay current, and holding old frames
back would only make the delay permanent. But it makes `dropped` climb by
a second every second from boot, which reads as catastrophic loss.

From 0.12.0 a node that has never been asked for a packet says `input
idle … — no stream is taking it` instead, and only reports buffer figures
once something has actually read from it.

### End-to-end test

**Done 2026-08-07**, first time a record played through this system.

- ~~ADC node captures audio~~
- ~~RTP/UDP transport~~
- ~~receiver node plays audio~~
- measure latency, packet loss behaviour and drift — **still open**. The
  first run had the two nodes on different mesh access points, three hops
  across the backhaul, and lost the occasional sample. That is a network
  condition rather than a result; the run worth measuring puts both nodes
  on one access point.

Latency, by arithmetic rather than measurement: 40 ms of capture ring at
most, plus the network, plus the playout target of 100 ms, plus 20 ms of
DMA. Around 160 ms needle to speaker. Fine for another room, and the
reason the capture ring is deliberately a fifth of the playout one — every
millisecond there is delay nothing downstream will absorb.

## Bringing up the DAC

Firmware 0.9.0 added the playout path: a Consumer starts an I²S output at
boot, buffers what arrives and feeds the DAC from it. Pins come from
`idf.py menuconfig` under **OpenAudioLink Test Node**, defaulting to the
table above.

The first sound this project makes should be the Hub's test tone:

1. Wire the DAC, headphones or an amplifier on its output, and flash 0.9.3.
2. The log says `I2S out on BCLK=7 WS=9 DOUT=8, 48000 Hz, stereo, 100 ms
   buffer`. If it does not, audio never started and the reason is on the
   line after it — the node still receives and still counts, so this is
   survivable rather than fatal.
3. From the Hub, send a test tone to the node: **Stream → test tone**, or
   `POST /api/stream/test-tone` with the node as the destination.
4. `GET /stream` on the node — **port 41001**, not 80 — now carries a
   `playout` object beside the reception statistics.

Read those two together, because they answer the same question from
different sides:

| What you see | What it means |
| --- | --- |
| `playing: true`, `underruns` and `droppedFrames` steady | Working. A click would be loss on the air, and `stats` says so |
| `underruns` climbing | The ring runs dry. Each one is an audible gap; the log names them too |
| `silenceFrames` climbing but `underruns` not | One long silence — the stream stopped rather than stumbling |
| `droppedFrames` climbing | The ring overfills. Bursts from the sender, or its clock is faster than this DAC's |
| **both** climbing | A bursty sender: clumps overflow the ring, the gaps between them empty it |
| `running: false` | I²S never started; the pins are wrong or in use |
| Silence, everything else healthy | XSMT, or `SCK` not grounded. See the wiring notes above |

**A click and a dropout are different faults**, and this is the pairing
that tells them apart: loss on the air shows in `stats`, a ring that ran
dry shows in `underruns`, and drift shows as one of them climbing slowly
while the other stays at zero.

`payloadErrors` is meaningless while a **tone** is playing. It compares
every sample against the pattern source, so a tone makes it count nearly
every sample. Large numbers there mean nothing unless the producer was
sending `pattern`.

Expect about **120 ms** from the Hub to the speaker: 100 ms of playout
buffer and 20 ms of DMA. Both are visible in the log line and adjustable.

### What the first hardware test changed

The tone came out of the DAC and interrupted constantly — irregularly,
sometimes seconds apart, sometimes several times a second. Three separate
faults, and the first one is why it took a while:

**`silenceFrames` could never move.** It was only incremented on a
*partial* chunk, but the ring is filled and drained in whole 240-frame
packets, so the count was either a whole chunk or nothing. The one counter
meant to reveal starvation was structurally stuck at zero while the ring
was starving several times a second. `underruns` now counts occurrences,
`silenceFrames` counts the silence actually inserted, and a re-prime is
logged as a warning rather than an info line.

**The playout re-primed on the first empty chunk.** Its comment reasoned
that an empty ring means the stream stopped — but for the first 5 ms a
stopped stream, a Wi-Fi retry and a sender that simply left a gap all look
identical. So a 5 ms gap in arrival became a whole buffer's worth of
silence while the ring refilled. It now waits 200 ms before concluding the
music ended.

**The sender was sending in clumps.** Windows' default timer resolution is
15.6 ms, so the Hub's `Task.Delay(1)` between packets really waited that
long and the catch-up loop then released three packets back to back — and
after a stall, up to the catch-up cap of twenty. The node's counters showed
both symptoms at once: `droppedFrames` in the millions from clumps
overflowing a 60 ms ring, and constant re-priming from the gaps emptying
it. The average rate was correct the whole time, which is why nothing on
the Hub looked wrong. The Hub now asks Windows for a 1 ms timer while a
stream runs, which is what `winmm`'s timer API exists for.

That was not the end of it. The dropouts got much better and did not
stop, and the next three faults were only findable by making the node
report both clocks every five seconds — frames arriving per second
against frames written to the DAC per second, with the buffer's minimum
and maximum fill across the interval. Everything below came from reading
those lines; none of it was visible in the counters.

**The DAC's clock was never wrong.** Two counter samples had implied a
consistent −890 ppm, which looked exactly like the drift decision 12
describes and would have justified building rate matching. It was an
accounting error instead: `i2s_channel_write` can accept less than it was
given and still return `ESP_OK`, and the tail was being discarded —
samples already removed from the ring, counted by nothing. A ring drained
by an invisible leak is indistinguishable from a fast playback clock. With
the write completed properly, `out` reads 48000 Hz on every line and has
never since read anything else.

**It was not the radio either.** The node had roamed to the far mesh
node at −80 dBm, which was an obvious suspect and wrong: moving it back
to −47 dBm changed nothing, and not one packet was lost at either signal
level. Late is not lost, and Wi-Fi is very good at being late.

**It was the Hub's send loop being descheduled.** The node's trace showed
arrival at 46801 Hz for one five-second window and 49055 Hz for the next —
120 ms of audio missing, then handed over in a lump. The loop ran on the
.NET thread pool and awaited inside itself, so every iteration's
continuation queued behind everything else in the process. Given its own
`LongRunning` thread at `AboveNormal` priority and synchronous sends, the
same trace settled to within ±2000 ppm, the ring stopped reaching either
end, and the tone became listenable.

What remains at the time of writing is rare and much larger: every minute
or so the trace shows a single window missing 60 to 105 ms of audio,
followed by the catch-up lump and an underrun. Nothing that fits in an
ESP32's RAM comfortably buffers that, so it wants finding rather than
absorbing.

**It is not the Hub, and its own counter says so.** Over 10 348 packets
the send loop's worst lateness was 25 ms with fourteen late wakes, while
the node was seeing 105 ms holes in the same period. That is the whole
value of having both ends report against their own clock: the theory that
had just been proved right for the large stalls was wrong for these, and
one field settled it instead of another evening of argument.

So the delay happens after the packet leaves the Hub, with **no packet
lost at all** — something buffers them and releases them together.

The first suspect is the one that was easy to overlook: these
measurements were taken with the Hub on a **laptop, over Wi-Fi**. That
puts every packet across the air twice — laptop to access point, access
point to node, plus the mesh backhaul when the two ends are on different
nodes — and the Hub's lateness counter cannot see any of it. It times the
send call, which happens before the packet reaches the network adapter,
so the laptop's own radio and its power management sit entirely outside
the number that exonerated the Hub. Windows adapters buffer hard with
power saving on, which is precisely this signature.

The second suspect is the mesh itself: this network runs an ASUS ZenWiFi
with roaming between nodes (the node has been seen to change BSSID), and
an access point that periodically leaves the channel to scan stops
serving its clients for about this long and delivers the backlog
afterwards.

**Moving the Hub to the wired server settled it.** Minutes at a time of
`+0 ppm`, zero underruns, zero drops, zero silence, with the ring sitting
at 105 ms against its 100 ms target and swinging about 35 ms. The holes
were the laptop's own wireless hop, not the mesh.

What is left looks like ordinary radio: one five-second window short by
40 ms, in the same interval that RSSI moved from −52 to −57, and the
window after it took a 55 ms deficit without a single underrun. That is
a buffer doing its job rather than a fault, and it is the point at which
this stopped being worth chasing.

Getting there needed one more fix, and it is the reason the wired test
looked at first like a regression: **the audio socket must send from the
interface that can reach the receiver.** It bound to `0.0.0.0` and let
the routing table choose, so on a server with Tailscale and a Hyper-V
switch the audio left by an interface the node cannot be reached from —
the Hub counting packets sent, the node reporting nothing received, both
telling the truth. `LocalAddressSelector` was written for exactly this
and was already used for OTA and for librespot's zeroconf; the audio
socket was simply never given it. A machine with one interface cannot
show this fault, which is why every earlier test passed.

One lesson worth keeping: the node's arrival figure is measured when the
*node reads the socket*, so it cannot tell a late sender from a late
reader. Both ends now report their own lateness against their own clock,
which is the only way that question gets answered rather than argued.

Simulating the ring against that sender settled one thing that intuition
got backwards. Tightening the Hub's catch-up cap — so a stall releases
fewer packets — sounds like the gentler choice and is the opposite: what
the cap discards is gone for good, so a cap shorter than a stall turns a
burst the ring could have swallowed into a gap no buffer can cover. The
cap belongs just under the receiver's headroom above its target, and stayed
at 100 ms. The same simulation says the node's changes alone fix the
dropouts even with the old 15.6 ms sender; the Hub's timer is margin
rather than the cure.

The buffer was resized as part of the same fix: **a playout buffer is
sized by the largest gap a sender can leave, not by the network's jitter.**
Wi-Fi jitter here is 1–2 ms; the sender's gap was 15.6 ms and its bursts
were longer. 20 ms of ring never stood a chance. It became 60 ms of target inside 160 ms of
ring, and then 100 ms inside 200 ms once the sender was fixed and the
remaining gaps could be measured properly.

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
