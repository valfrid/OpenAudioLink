# uacprobe — USB host + UAC 2.0, first light

> **Concluded. Kept as a record, not as a target.**
>
> Both questions below were answered yes, and the capability moved into the
> node firmware: `oal_audio` links `usb` and `uac2_host`, and a Consumer
> with `output` set to `usb` plays through a dongle. This application is
> **no longer built by CI or attached to releases** — the measurements in
> `docs/USB-AUDIO.md` are what it was for, and they are recorded there.
>
> It should still build if you want to run it again, but nothing checks
> that on every push any more, so expect to fix bit rot first. The pinned
> `esp-uac2-host` commit is the likeliest thing to have moved.

The smallest program that answers the two questions blocking track A in
`docs/USB-AUDIO.md`: **does an ESP32-S3 enumerate a UAC 2.0 DAC, and can it
push a tone through it.**

It is not a node. No Wi-Fi, no RTP, no discovery, no OTA, and nothing from
the `oal_*` components. It builds on **ESP-IDF 5.4** because
`esp-uac2-host` requires it — which was once the whole reason this was a
separate application rather than a flag in `testnode`, back when the rest
of the firmware was on 5.3.1. The tree has since moved to 5.4 as well, so
what keeps it separate now is only its sdkconfig: USB in host mode,
console on UART, no Wi-Fi.

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

## The rig

No soldering, no adapters, no cable. The dongle plugs **straight into the
XIAO's USB-C socket**; the serial adapter carries power and the console.

```
  USB-serial adapter          XIAO ESP32S3
  ───────────────────         ────────────
  RX  ──────────────────────  D6 (GPIO43, U0TXD)   read the log
  TX  ──────────────────────  D7 (GPIO44, U0RXD)   reflash without buttons
  GND ──────────────────────  GND
  5V  ──────────────────────  5V

                              USB-C ──── CX31993 dongle
```

**Why the CC pins turn out not to matter.** Both ends of that joint present
Rd — the XIAO's socket is wired as a device, and so is the dongle — and two
Rd's facing each other normally means neither side attaches. It works
anyway, because CC only decides *whether a port switches VBUS on*. The
XIAO's VBUS is not switched by anything: it is wired to the 5 V rail. Feed
that pin and the socket is live regardless of what CC says, the dongle sees
VBUS, powers up and enumerates. Nothing negotiates because nothing here has
a vote.

The USB-OTG side is the same story — host mode is set in software, not by
CC detection.

**So VBUS was the only real question, and on this board the answer is yes.**
Measured by the thing working: a XIAO powered through its 5 V pin puts 5 V
on its own USB-C socket. The back silkscreen said as much, marking that pin
`VUSB` rather than naming a regulated rail.

That also sharpens the never-both-at-once rule below into a mechanism:
feeding the 5 V pin while USB-C is plugged into a PC pushes current into the
PC's port.

**Where this stops generalising.** A board with a real CC controller, or one
whose VBUS is gated, will not behave this way — and neither will a USB-C
*device* that refuses to attach until it sees Rp. The adapters described in
`docs/USB-AUDIO.md` are still the answer there; they simply were not needed
here.

`MaxPower` on the measured dongle is 100 mA, and a XIAO with the radio idle
is around 100 mA more. A USB-serial adapter's 5 V comes straight from a PC
port and carries that comfortably. If the board browns out when the dongle
attaches, give the 5 V pin its own supply and keep only GND and RX from the
adapter.

### The soldered rig, if a board ever needs it

Not needed on a XIAO ESP32S3 — kept for a board whose VBUS turns out to be
gated. Easier than it looks, because **the XIAO ESP32S3 breaks the USB data lines
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
