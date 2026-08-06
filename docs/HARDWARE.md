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

Firmware 0.10.1 adds the capture path: a Producer brings up an I²S input at
boot and sends what it captures. Enable it under **OpenAudioLink Test Node**
in `idf.py menuconfig`; it is off by default, because a Producer with no ADC
still streams the synthetic sources every link measurement was made with.

**Which end owns the clock is the board's decision, not a preference**, and
the two kinds of board differ:

- **A bare PCM1808** has no oscillator. The ESP32 must supply MCLK, BCK and
  LRCK and the module follows. This is the better arrangement where it is
  available, because it puts capture and playback on **one clock** — what
  decision 12's synchronisation wants, and what makes a node that both
  records and plays one device rather than two sharing a box.
- **A self-clocked module** — the common `ANA TO I2S 96K/24BIT` boards —
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

**Wiring a self-clocked module** (header order on the board is `DATA BCLK
LRCK MCLK GND`):

| Module | XIAO ESP32S3 | |
| --- | --- | --- |
| DATA | D5 (GPIO6) | audio into the ESP |
| BCLK | D3 (GPIO4) | bit clock, **an input to the ESP** |
| LRCK | D4 (GPIO5) | word select, **an input to the ESP** |
| MCLK | **leave unconnected** | an *output* on this board; two drivers on one line is how boards get damaged |
| GND | GND | |
| VDD / GND on `POWER` | see below | |

GPIO 3–6 are D2–D5, four adjacent pins on the side **opposite** the DAC's
`D8`/`D9`/`D10`, so a node can carry both boards without either reaching
across. The ESP32-S3 has two I²S peripherals, so one node really can capture
a turntable and play a stream at once.

**Check the supply voltage before connecting it.** These boards carry an
onboard regulator, and 5 V input is common — but the marking is not
conclusive from a photograph and the wrong choice destroys the board. Check
the seller's description. If it cannot be established, **try 3.3 V first**:
too little means it does not run, too much means it does not survive.

**The rate is the board's to choose, and 24.576 MHz gives two answers.**
512fs is 48 kHz and 256fs is 96 kHz, selected by the module's strapping —
the `OP1`/`OP2`/`OP3` pads on this one are the likely selects, and `96K` on
the silkscreen is a hint rather than a statement about how it left the
factory. **This matters:** 96 kHz frames sent down a 48 kHz profile play
back at half speed.

The firmware measures it rather than assuming. The capture trace reports
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
