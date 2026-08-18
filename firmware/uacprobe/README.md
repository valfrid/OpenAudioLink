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

## Wiring

The dongle is bus-powered and sources nothing, so the board has to. See
`docs/USB-AUDIO.md` for why the USB-C connector is the wrong place to try
this — the short version is that VBUS enters through a diode that will not
pass current back out, and both ends present Rd on CC so they never attach.

```
ESP32-S3  GPIO20 (D+) ──┐
          GPIO19 (D−) ──┤ USB-A receptacle ── C↔A adapter ── dongle
   5 V supply ──────────┤   (the adapter came in the dongle's box and
          GND ──────────┘    carries the Rp the device needs to attach)
```

Feed the same 5 V to the board's 5 V pin and to the receptacle's VBUS, and
share the ground. `MaxPower` on the measured dongle is 100 mA.

The receptacle sits in parallel with the board's own USB-C connector on the
same two pins, so **do not have both occupied at once** — use one for the
dongle and the other for flashing, never together.

## Flashing and the console

**Flashing works over the board's USB-C port**, even though the application
takes that peripheral for host duty: hold BOOT, tap RESET, and the ROM
bootloader takes the pins back as a serial device. Release BOOT, flash,
reset.

**The console does not.** `sdkconfig.defaults` moves it to UART0 and
explains why at length: USB-Serial/JTAG and USB-OTG share one PHY and one
pin pair on the S3, and only one can have them. Put a USB-serial adapter on
the board's UART0 TX/RX pins to read the log. On a XIAO ESP32S3 those are
D6/D7 — the same two pins `HARDWARE.md` tells you to keep clear of I²S, for
this reason.

Without the adapter the board runs and says nothing, which looks exactly
like a board that is not running.

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
