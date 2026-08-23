# OpenAudioLink

A local-first, open-source multi-room audio platform. It distributes
synchronised stereo audio over an ordinary IP network using inexpensive
ESP32-S3 nodes and a Windows Hub.

No cloud, no account, no vendor. The Hub runs on a PC you own, the nodes
cost a few euros each, and audio goes straight from whatever is producing it
to whatever is playing it.

## Where it is now

Working, in daily use, and measured:

- **Two or more nodes playing one stream in sync**, from Spotify, internet
  radio, a turntable through a line-in ADC, or a test tone.
- **L24 stereo, 48 kHz, 5 ms packets** — AES67-grade timing, on consumer
  Wi-Fi, behind a jitter buffer sized for it.
- **10 ppm of audio arriving too late to play**, zero packet loss, over a
  two-hour run. That is 1 packet in 100 000, and it started the same day at
  2 530 ppm.
- **Updates over the air, with rollback.** A bad image reverts itself at the
  next boot and the Hub says so.

Not finished: room calibration by microphone, standalone "island" mode, and
the node as a USB audio device. See `docs/ROADMAP.md`.

## How it fits together

Four roles, which are configuration rather than different products:

- **Controller** — coordinates devices, sources and routes.
- **Producer** — generates RTP audio.
- **Consumer** — receives and plays it.
- **Provisioner** — flashes, configures, updates and recovers devices.

The Hub is normally Controller, Provisioner, and Producer for anything
sourced on the PC. A node with an ADC is a Producer; a node with a DAC or a
USB dongle is a Consumer. One firmware image serves them all, with the role
stored in NVS (decision 5).

**The control plane** carries discovery, configuration, routing, volume,
status and OTA. **The audio plane** carries RTP/UDP straight from Producer
to Consumers. The Hub names the destinations; it does not relay the audio.

## Getting started

1. **Install the Hub** — `docs/INSTALLING-THE-HUB.md`
2. **Build a node** — `docs/HARDWARE.md` for the boards, wiring and parts,
   and `docs/hardware-photos/` to see what you are aiming at. A node is one
   microcontroller board and one audio board; nothing is custom and there is
   no OpenAudioLink board to source.
3. **Flash and provision it** — the Hub's setup page; credentials go in over
   the node's own Wi-Fi portal and never into this repository
4. **Play something** — `docs/LISTENING.md`

The switchboard — the everyday screen for choosing what plays where — is
`docs/CAST-POINTS.md`.

## Documentation

Two kinds, deliberately kept apart.

### How it works now

Reference for the system as it stands. Read these to use it or change it.

| | |
| --- | --- |
| `docs/ARCHITECTURE.md` | The shape of the system and why it has that shape |
| `docs/INSTALLING-THE-HUB.md` | Installing and updating the Hub |
| `docs/HARDWARE.md` | Boards, DACs, ADCs, wiring, enclosures |
| `docs/hardware-photos/` | Photographs of the real thing — what a node is made of |
| `docs/TUNING.md` | The jitter buffer, its two knobs, and how to read the counters |
| `docs/LISTENING.md` | Playing audio end to end |
| `docs/CAST-POINTS.md` | Rooms and groups from a phone |
| `docs/CONTROL-SURFACE.md` | What every control in the Hub does |
| `docs/USB-AUDIO.md` | The USB dongle output stage |
| `docs/LIBRESPOT.md` | Spotify as a source |
| `docs/WINDOWS-AUDIO-CAPTURE.md` | Capturing what the PC is playing |
| `protocol/` | Wire specifications: discovery, control, RTP, identity, OTA |

### How it got here

The record of what was tried, measured, and decided. Read these to
understand *why* something is the way it is — or before changing it, since
most of the obvious alternatives are in here with the reason they failed.

| | |
| --- | --- |
| `docs/DECISIONS.md` | Numbered decisions, with what each one closed off |
| `docs/LINK-MEASUREMENTS.md` | 34 measured runs: loss, jitter, stalls, buffers |
| `docs/ROADMAP.md` | What is done, what is next, what was abandoned |
| `docs/ROOM-CALIBRATION.md` | The microphone experiment, not yet run |
| `docs/BUILDING-LIBRESPOT-WINDOWS.md` | Building librespot, and what went wrong |
| `docs/MASTER_PROMPT.md` | The project's standing brief |

These two groups are separate on purpose. The history is long — a third of
the documentation by line count — and it is worth keeping, because this
project has repeatedly re-derived conclusions it had already reached and
paid for. But it should not be in the way of someone who only wants to know
how to set a buffer.

## Repository layout

```text
docs/       Reference and history (see above)
protocol/   Wire specifications
hub/        OpenAudioLink Hub (.NET 8: service, API, web UI, tests)
firmware/   ESP32-S3 firmware (ESP-IDF app and shared components)
enclosures/ Parametric 3D-printable enclosures (OpenSCAD source)
```

## A note on credentials

Wi-Fi credentials are never committed. Nodes are provisioned through their
own captive portal, and the standalone network's passphrase is generated on
the Hub and stored in its data directory. This repository is public; treat
anything in it as published.

Build instructions are in `CONTRIBUTING.md`. Licensed under the MIT License.
