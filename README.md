# OpenAudioLink

OpenAudioLink is a local-first, open-source multi-room audio platform.

It distributes synchronized stereo audio over a local IP network using inexpensive ESP32 audio nodes and a Windows-based Hub.

## Core architecture

OpenAudioLink is built around four logical roles:

- **Controller** — coordinates devices, sources, receivers and routes.
- **Producer** — generates RTP audio streams.
- **Consumer** — receives and plays RTP audio.
- **Provisioner** — flashes, configures, updates and recovers devices.

The Windows application normally implements Controller, Producer for Windows-hosted sources, and Provisioner.

An ESP32 analog source implements Producer and may provide a limited Controller role in standalone mode.

An ESP32 receiver implements Consumer only.

## Control plane and audio plane

The control plane manages discovery, configuration, routing, ownership, volume, status, OTA and provisioning.

The audio plane carries RTP/UDP audio directly from the active Producer to the selected Consumers.

External audio sources are not normally routed through the Windows Hub.

## Initial hardware

### Development hardware

During early development, available ESP32-C3 boards may be used for control-plane, discovery, provisioning and basic RTP experiments.

### Target audio hardware

- Receiver: ESP32-S3 + PCM5102A stereo I²S DAC
- Analog source: ESP32-S3 + PCM1808 stereo I²S ADC with onboard oscillator

The ESP32-S3 is the intended long-term platform. ESP32-C3 support is temporary development support and is not the reference hardware target.

## First development focus

1. Windows Hub repository and application skeleton
2. Device model and control API
3. Discovery
4. Web UI
5. USB flashing and provisioning
6. OTA management
7. RTP sender in Windows
8. ESP receiver proof of concept
9. ADC source proof of concept
10. Synchronization and clock correction

See `docs/ARCHITECTURE.md`, `docs/MASTER_PROMPT.md`, `docs/HARDWARE.md` and `docs/ROADMAP.md`.

## Repository layout

```text
docs/       Phase 1 architecture, roadmap, hardware baseline, master prompt
protocol/   Protocol suite specifications (discovery, control, identity)
hub/        OpenAudioLink Hub (.NET 8 solution: service, API, web UI, tests)
firmware/   ESP32 firmware (ESP-IDF test node and shared components)
```

Build instructions are in `CONTRIBUTING.md`. Licensed under the MIT License.
