# OpenAudioLink Firmware

ESP-IDF (v5.x) firmware for OpenAudioLink nodes.

```text
components/       Shared, portable components (no SoC-specific code)
  oal_discovery/  Discovery announce/probe per protocol/DISCOVERY.md
testnode/         Minimal Phase 2.3 test firmware: boot -> Wi-Fi -> announce
```

The eventual Receiver and Analog Source firmware will live here as separate
projects reusing the shared components. Hardware-specific behaviour (I²S
pins, DAC/ADC init) belongs in hardware-profile code, never in shared
components — the ESP32-C3 is temporary development hardware and the
reference platform is the ESP32-S3.

## Test node

The test node proves the first discovery milestone:

```text
ESP boots -> announces -> Hub discovers -> device appears in UI
```

### Build and flash

```bash
cd testnode
idf.py set-target esp32c3        # or esp32s3
idf.py menuconfig                # set OpenAudioLink Test Node -> Wi-Fi SSID/password
idf.py build flash monitor
```

Wi-Fi credentials are build-time configuration for now; runtime USB
provisioning replaces this in Phase 2.5. With no SSID configured the node
boots and logs a warning instead of connecting, so default builds (and CI)
work without credentials.
