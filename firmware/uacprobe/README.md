# uacprobe — USB host + UAC 2.0, first light

The smallest program that answers the two questions blocking track A in
`docs/USB-AUDIO.md`: **does an ESP32-S3 enumerate a UAC 2.0 DAC, and can it
push a tone through it.**

It is not a node. No Wi-Fi, no RTP, no discovery, no OTA, and nothing from
the `oal_*` components. It builds on **ESP-IDF 5.4** because
`esp-uac2-host` requires it, where the rest of the firmware builds on 5.3.1
— keeping those apart is the whole reason this is a separate application
rather than a flag in `testnode`.

## What it does

1. **Enumerates and reports.** Opens the device and prints the specific
   facts `docs/USB-AUDIO.md` has open questions about: UAC version, how many
   clock sources, the sync type and feedback endpoint of every alternate
   setting, and whether 48 kHz / 24-bit / stereo is actually offered. These
   were read off Windows with USB Device Tree Viewer; this is the same
   reading taken by the machine that has to live with them.
2. **Plays a 1 kHz sine** at 48 kHz / 24-bit / stereo, −14 dBFS, and reports
   a running frame count once a second.

The frame counter is not decoration. Run 18's lesson was that an instrument
which cannot distinguish the failure from the success is not an instrument,
and here "no sound" has to be separable from "no data".

## The rig, without soldering anything

Try this first. It uses the board's own USB-C connector and two adapters,
and it either works or it fails on one measurable thing.

```
  USB-serial adapter          XIAO ESP32S3            dongle
  ───────────────────         ────────────            ──────
  RX  ──────────────────────  D6 (GPIO43, U0TXD)   read the log
  TX  ──────────────────────  D7 (GPIO44, U0RXD)   reflash without buttons
  GND ──────────────────────  GND
  5V  ──────────────────────  5V

                              USB-C ── [C plug→A socket]
                                        ── [A plug→C socket] ── CX31993
```

Three things about it are worth knowing before it disappoints anybody.

**The CC pins are already handled.** The second adapter — the one that came
in the dongle's box — presents Rp on CC, which is the signal the dongle
needs in order to attach as a device. Nothing else in the chain has to
negotiate anything, because the XIAO's USB-OTG is put into host mode in
software rather than by CC detection.

**But that Rp is 56 kΩ *to VBUS*.** With no VBUS there is no pull-up, the
dongle sees nothing, and the whole chain is inert. **VBUS is the entire
question**, and everything else here is already solved.

**So measure it before believing any of it.** The middle of the chain has an
exposed USB-A socket, whose pin 1 is VBUS and pin 4 is GND — far easier to
probe than anything on the board. Power the XIAO from the serial adapter,
plug in the first adapter only, and measure across those two pins:

- **~5 V** — the board's 5 V pin reaches the connector. Plug in the rest and
  go.
- **0 V** — VBUS enters the board through a diode that will not pass current
  back out, which is the failure `docs/USB-AUDIO.md` predicts. Inject 5 V at
  that USB-A socket from the same supply, or fall back to the soldered rig
  below. Do not go looking for a firmware problem: there is no firmware
  problem, there is no power.

**The board's own silkscreen is the best evidence available before the
meter.** On a XIAO ESP32S3 the pin marked `5V` on the front is marked
**`VUSB`** on the back — the USB connector's own supply net rather than a
regulated rail derived from it. A pin named after the thing we need it to
reach is the outcome to hope for, and it turns the measurement from a coin
flip into a confirmation. It is still a confirmation worth doing: whether
anything sits in that path is not something a label can tell you.

It also explains the "never both at once" rule below with a mechanism rather
than caution. If that pin really is VBUS, then feeding it 5 V while USB-C is
plugged into a PC pushes current back into the PC's port.

`MaxPower` on the measured dongle is 100 mA, and a XIAO with the radio idle
is around 100 mA more. A USB-serial adapter's 5 V pin comes straight from a
PC port and should carry that, but if the board browns out when the dongle
attaches, give the 5 V pin its own supply and keep only GND and RX from the
adapter.

### The soldered rig, if VBUS is not there

Easier than it looks, because **the XIAO ESP32S3 breaks the USB data lines
out on its back as two pads labelled `D+` and `D−`** — no need to find
GPIO20 and GPIO19 anywhere on the module. They sit in the middle of the
board beside the JTAG pads (`MTCK`, `MTMS`, `MTDI`, `MTDO`) and the battery
pads (`BAT+`, `BAT−`).

```
XIAO back pad  D+  ──┐
XIAO back pad  D−  ──┤ USB-A receptacle ── C↔A adapter ── dongle
    5 V supply ──────┤
           GND ──────┘
```

Those pads are **wired in parallel with the USB-C connector**, not instead of
it, so the rule stands whichever route is used: **do not have both occupied
at once** — one for the dongle, the other for flashing, never together.

`EN` is also broken out on the back, which is a hardware reset if the board
ends up somewhere a button cannot reach.

## Flashing and the console

**Flash it the normal way the first time**: the CI artifact
`uacprobe-esp32s3` holds `uacprobe-esp32s3-flash.bin`, a complete image for
<https://espressif.github.io/esptool-js/> at address `0x0`, exactly like the
node images in `firmware/README.md`.

**After that, every flash needs download mode — but nothing is ever
stuck.** Download mode lives in the chip's ROM and is entered by the state
of a pin at reset, before any application code runs at all. Whatever this
firmware does with USB is undone by the reset that precedes it, so the board
cannot be flashed into a corner. The worst case is a button press.

What is genuinely lost is the *automatic* entry into download mode. Normally
esptool asks the board to reset itself over the serial port it is already
talking to; once this application claims the USB peripheral there is no
serial port for it to ask. So:

> **Type `download` on the serial console.** The board reboots straight into
> download mode — no buttons, no extra wires, over the UART already
> connected to read the log.
>
> Or, always available: **hold BOOT, tap RESET, release BOOT.**

**Why not just wire the adapter to EN and be done?** Because entering
download mode takes two lines, not one: a reset *and* GPIO0 held low while
the chip comes up. On a XIAO ESP32S3 **GPIO0 is not brought out anywhere** —
it reaches the BOOT button and nothing else. Wiring EN to the adapter's RTS
buys an automatic reset and still leaves a finger on BOOT, which is most of
the inconvenience for all of the effort. Worse, plenty of USB-serial
adapters assert RTS the moment a port is opened, so a direct EN connection
can hold the board in reset and make it look dead.

The `download` command exists because of that dead end. The chip has a
second route to the same place — a bit in an RTC register telling the ROM to
enter download mode on the next boot regardless of GPIO0 — so the firmware
sets it and restarts itself. The trigger is a whole word rather than a
keystroke because the ESP's RX line may be floating, and a single character
would let electrical noise reboot the experiment.

This needs the adapter's **TX wired to D7**, which is the same wire the
UART flashing route below wants. Wire both directions from the start.

**And you can flash over the UART adapter instead**, without touching the
USB-C side of the rig at all. The ROM listens on U0TXD/U0RXD as well as on
USB, so with the adapter already wired to D6/D7 the same button press puts
the board in reach of `idf.py -p COM<uart> flash`. This wants both
directions wired — D6 to the adapter's RX *and* D7 to its TX — where merely
reading the log needs only D6.

Whichever route: **disconnect the serial adapter's 5 V before plugging in
USB-C**, so the board is not fed from two supplies at once. If the 5 V pin
really is VBUS, doing both pushes current into the PC's port.

The banner this program prints at boot repeats the button sequence, on the
theory that the person reading the log is the person about to need it.

**The console never comes back to USB.** `sdkconfig.defaults` moves it to
UART0 and explains why at length: USB-Serial/JTAG and USB-OTG share one PHY
and one pin pair on the S3, and only one can have them. Reading the log
needs the adapter above, at 115200 baud, with its logic level set to
**3.3 V** — a 5 V TX line into GPIO44 is out of spec.

To read the log you only need the adapter's **RX** on D6 and a common
ground; TX is there for completeness and nothing in this program listens.

Without the adapter the board runs and says nothing, which looks exactly
like a board that is not running.

**Power up and start the monitor before plugging in the dongle.** The banner
prints within a second of boot, and attaching afterwards separates "the
program is running and waiting" from "the device never arrived" — two
failures that otherwise look identical.

```
idf.py -p COMn flash          # over USB-C, in download mode
idf.py -p COMm monitor        # over the UART adapter
```

## Reading the output

A good run says, in order: the playback interface connected, then the
device block with VID `3302` PID `336A` and `UAC 2.0`, then the clock and
interface lists, then a verdict, then a rising frame count.

Things worth knowing before they surprise anybody:

- **`clocks: 2`** is expected on this dongle and provokes a warning, because
  `esp-uac2-host` documents single-clock devices. Expected is not the same
  as fine — if playback works anyway, that is a finding to write down.
- **`feedback endpoint: no`** is expected. The dongle is synchronous and
  follows the host's SOF. That changes the drift story rather than breaking
  it; see `docs/USB-AUDIO.md`.
- **A truncated interface list.** The parser holds four streaming
  interfaces; the dongle's Windows dump has five alternates carrying
  endpoints. The program warns when the list is at that limit rather than
  reporting whatever fitted.
- **Nothing at all after the banner** means the device is not attaching.
  Check VBUS at the receptacle first — that is the cheapest thing that can
  be wrong and, per `docs/USB-AUDIO.md`, the most likely.
