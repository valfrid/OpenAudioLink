# OpenAudioLink Firmware

ESP-IDF (v5.x) firmware for OpenAudioLink nodes.

```text
components/       Shared, portable components (no SoC-specific code)
  oal_wifi/       NVS credentials + SoftAP provisioning portal
  oal_discovery/  Discovery announce/probe per protocol/DISCOVERY.md
  oal_control/    Control server (/status, /reboot, /ota) per protocol/CONTROL.md
testnode/         Test firmware: boot -> Wi-Fi -> announce -> controllable + OTA
uacprobe/         Experiment: USB host + UAC 2.0 DAC, enumerate and tone
```

The eventual Receiver and Analog Source firmware will live here as separate
projects reusing the shared components. Hardware-specific behaviour (I²S
pins, DAC/ADC init) belongs in hardware-profile code, never in shared
components. The ESP32-S3 is the only target that builds.

**`uacprobe` is not a node and shares nothing with one.** It is the first
step of `docs/USB-AUDIO.md` track A, it builds on **ESP-IDF 5.4** where
`testnode` builds on 5.3.1, and it uses none of the `oal_*` components. That
separation is the point rather than an accident: the driver it depends on
requires 5.4, and no experiment gets to move the toolchain every node in the
house is built with. Its own README covers wiring, flashing and how to read
what it prints.

## Flash layout

8 MB, two OTA slots of 4032 KB each, no factory partition
(`testnode/partitions.csv`). The built-in two-OTA table caps app
partitions at 1 MB whatever the chip, which is what the C3-era images
used; sizing the table to the XIAO's real flash gives 3.9x the room.

**A change to the partition table cannot be delivered over the air.** OTA
writes app partitions only; the table lives at 0x8000. Moving between
layouts means a USB re-flash of every node, and that clears NVS, so
Wi-Fi credentials, name and roles have to be entered again.

## Getting a node running without a toolchain

1. **Download images**: on GitHub, open the latest CI run under **Actions**
   and download the `testnode-esp32s3` artifact. It contains:
   - `testnode-esp32s3-flash.bin` — complete flash image for USB flashing
   - `testnode-esp32s3-ota.bin` — application image for OTA updates
2. **Flash over USB (first time only)**: open
   <https://espressif.github.io/esptool-js/> in Chrome or Edge, connect the
   board over USB, choose the `...-flash.bin` file at address `0x0`, and
   program it. No installed tools required.
3. **Watch it boot** (optional): the console is on the native USB port, so
   the same connection used for flashing shows the log at 115200 baud. In
   esptool-js use the separate **Console** section — the Connect button in
   the programmer forces download mode, in which the application never
   runs.
4. **Join your Wi-Fi**: the node boots into a setup access point named
   `OpenAudioLink-XXXXXX`. Connect to it with a phone or laptop, open
   <http://192.168.4.1/>, and enter your network name and password. The
   node saves them and reboots onto your network. Credentials stay on the
   device — they are never part of the repository or the images.
5. **Verify**: the node appears in the Hub web UI within a few seconds.
   From there you can reboot it or push the `...-ota.bin` of a newer build
   over the air — no USB needed again.

If joining fails repeatedly (for example a mistyped password), the node
falls back to the setup access point so you can correct it.

## Building from source (development)

```bash
cd testnode
idf.py set-target esp32s3
idf.py build flash monitor
```

Optional build-time Wi-Fi credentials for bench work can be set under
menuconfig → *OpenAudioLink Test Node*; NVS credentials from the portal
take precedence.
