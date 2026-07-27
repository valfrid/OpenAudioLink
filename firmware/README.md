# OpenAudioLink Firmware

ESP-IDF (v5.x) firmware for OpenAudioLink nodes.

```text
components/       Shared, portable components (no SoC-specific code)
  oal_wifi/       NVS credentials + SoftAP provisioning portal
  oal_discovery/  Discovery announce/probe per protocol/DISCOVERY.md
  oal_control/    Control server (/status, /reboot, /ota) per protocol/CONTROL.md
testnode/         Test firmware: boot -> Wi-Fi -> announce -> controllable + OTA
```

The eventual Receiver and Analog Source firmware will live here as separate
projects reusing the shared components. Hardware-specific behaviour (I²S
pins, DAC/ADC init) belongs in hardware-profile code, never in shared
components — the ESP32-C3 is temporary development hardware and the
reference platform is the ESP32-S3.

## Getting a node running without a toolchain

1. **Download images**: on GitHub, open the latest CI run under **Actions**
   and download the artifact for your board (`testnode-esp32c3` or
   `testnode-esp32s3`). It contains:
   - `testnode-<target>-flash.bin` — complete flash image for USB flashing
   - `testnode-<target>-ota.bin` — application image for OTA updates
2. **Flash over USB (first time only)**: open
   <https://espressif.github.io/esptool-js/> in Chrome or Edge, connect the
   board over USB, choose the `...-flash.bin` file at address `0x0`, and
   program it. No installed tools required.
3. **Join your Wi-Fi**: the node boots into a setup access point named
   `OpenAudioLink-XXXXXX`. Connect to it with a phone or laptop, open
   <http://192.168.4.1/>, and enter your network name and password. The
   node saves them and reboots onto your network. Credentials stay on the
   device — they are never part of the repository or the images.
4. **Verify**: the node appears in the Hub web UI within a few seconds.
   From there you can reboot it or push the `...-ota.bin` of a newer build
   over the air — no USB needed again.

If joining fails repeatedly (for example a mistyped password), the node
falls back to the setup access point so you can correct it.

## Building from source (development)

```bash
cd testnode
idf.py set-target esp32c3   # or esp32s3
idf.py build flash monitor
```

Optional build-time Wi-Fi credentials for bench work can be set under
menuconfig → *OpenAudioLink Test Node*; NVS credentials from the portal
take precedence.
