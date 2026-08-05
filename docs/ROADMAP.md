# OpenAudioLink Roadmap

## Phase 1 — Architecture

Status: complete.

Deliverables:

- role-based architecture
- control-plane/audio-plane separation
- software component boundaries
- hardware baseline
- lifecycle and versioning model
- repository structure
- master prompt

## Phase 2 — Repository and Windows foundation

The Windows Hub is the first implementation focus.

### 2.1 Repository foundation

- create repository structure
- commit Phase 1 documentation
- select license
- establish coding conventions
- add CI
- create buildable Windows and firmware skeletons

### 2.2 Windows Hub skeleton

Likely baseline:

- modern .NET
- ASP.NET Core backend
- Windows background service
- optional logged-in desktop audio agent
- browser-based web UI

Initial functions:

- health endpoint
- configuration storage
- device inventory
- logging
- web UI shell

### 2.3 Device model and discovery

Implement:

- device identity
- role
- hardware profile
- firmware version
- protocol version
- capabilities
- online/offline state

First proof:

```text
ESP boots -> announces -> Hub discovers -> device appears in UI
```

ESP32-C3 boards may be used for this milestone.

### 2.4 Basic control plane

Commands:

- identify
- rename
- reboot
- read status
- write configuration
- factory-reset request

### 2.5 USB flashing and provisioning

Windows application should:

- detect ESP devices
- flash bundled firmware
- assign identity
- select role and hardware profile
- configure Wi-Fi
- verify network appearance

### 2.6 OTA foundation

- firmware manifest
- checksums
- compatibility checks
- manual update workflow
- recovery behaviour

## Phase 3 — First Windows-to-receiver audio path

```text
Windows WASAPI -> RTP/UDP -> ESP receiver -> I²S DAC
```

Goals:

- 24-bit/48 kHz stereo
- stable playback
- jitter buffer
- basic clock correction
- one and then multiple receivers

## Phase 4 — Analog Source

When the ordered boards arrive:

```text
PCM1808 -> ESP32-S3 -> RTP/UDP -> Receiver(s)
```

Goals:

- clean ADC capture
- direct Producer-to-Consumer stream
- Hub control without Hub audio relay
- standalone limited Controller mode later

## Known gaps, small enough to fix when convenient

- **`SystemAudioSource` refuses a sample-rate mismatch.** It throws when
  the Windows endpoint is not at 48 kHz, with a comment saying resampling
  is not implemented. It is now: `RationalResampler` was built for
  librespot and does exactly this. Wiring it in matters more than it
  sounds, because Spotify's lossless tier is only available inside their
  own desktop app — capturing that app's output is the only route to
  lossless from Spotify (`LIBRESPOT.md`), and it arrives at 44.1 kHz.
- **Drift correction.** The playout ring absorbs it and counts both ends
  but does not trim the clock. Belongs with decision 2's multi-speaker
  synchronisation rather than on its own.
- **Concealment.** A lost packet is 5 ms of silence, not an
  interpolation. Worth measuring before deciding it needs fixing.

## Later candidates

Priority is intentionally not fixed:

- USB audio input
- ~~Spotify Connect~~ — done, `LIBRESPOT.md`
- AirPlay as a second provider adapter. The strongest remaining argument
  is that it needs no account at all, where Spotify Connect binds a cast
  point to whoever signed it in
- Chromecast as a *provider*: a dongle's output into an ESP32 Producer,
  keeping the Cast front end that already works and is account-free while
  OpenAudioLink distributes to speakers. Analog through the PCM1808 first,
  because it needs no new parts and no sample-rate conversion
- internet radio
- Home Assistant integration
- Bluetooth input
- DSP
- more hardware profiles
- alternative Consumers: USB Audio Class DAC on an ESP32-S3 in host mode,
  a PC Consumer application, a Raspberry Pi Consumer (see decision 8)
- a wall control surface — see `CONTROL-SURFACE.md`
