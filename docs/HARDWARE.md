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
| Vin | 5 V |
| GND | GND |
| BCLK | D8 (GPIO7) |
| LRC | D9 (GPIO8) |
| DIN | D10 (GPIO9) |
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
