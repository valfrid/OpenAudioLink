# OpenAudioLink Architecture

Version: Phase 1 Architecture v1.0

## 1. Vision

OpenAudioLink is a local-first, open-source distributed audio system for synchronized stereo playback across multiple rooms.

The system shall run on the local network without cloud dependency.

Typical sources may include Windows system audio, Spotify Connect, USB audio, internet radio and analog sources such as vinyl players, televisions or mixers.

## 2. Logical roles

OpenAudioLink is role-based rather than device-based. A physical device may implement one or more roles, but each role has one clearly defined responsibility.

### Controller

Responsible for:

- discovery
- source selection
- receiver assignment
- route ownership
- configuration
- volume and mute
- status and diagnostics
- system state

Normally implemented by the Windows Hub.

The Analog Source may implement a limited Controller role in standalone mode.

### Producer

Responsible for generating an RTP audio stream.

Examples:

- Windows Hub for WASAPI, Spotify, USB audio or internet radio
- ESP32 Analog Source for line-level analog audio

### Consumer

Responsible for receiving and playing audio.

Implemented by the ESP32 Receiver.

Responsibilities:

- RTP reception
- jitter buffering
- clock correction
- I²S DAC output
- volume and mute
- diagnostics

### Provisioner

Responsible for device lifecycle management.

Implemented by the Windows Hub.

Responsibilities:

- initial USB flashing
- identity assignment
- Wi-Fi provisioning
- hardware-profile selection
- OTA management
- compatibility checks
- recovery flashing

## 3. Control plane and audio plane

### Control plane

The control plane handles:

- discovery
- routing
- source and receiver assignment
- stream ownership
- volume and mute
- configuration
- diagnostics
- OTA
- provisioning

### Audio plane

The audio plane carries RTP/UDP audio directly from the active Producer to the selected Consumers.

Examples:

```text
Windows-hosted source
Windows Hub -> RTP/UDP -> Receiver(s)
```

```text
Analog source
ESP32 Analog Source -> RTP/UDP -> Receiver(s)
```

The Windows Hub coordinates external streams but does not normally relay them.

## 4. Software deliverables

### OpenAudioLink Hub

Windows software implementing:

- Controller
- Producer for Windows-hosted sources
- Provisioner

Functions include:

- device inventory
- source and receiver management
- routing
- web UI
- Windows audio capture
- RTP generation
- USB flashing
- provisioning
- OTA
- diagnostics

The installed product may internally contain a background service, desktop audio agent and web application.

### OpenAudioLink Receiver

ESP32 firmware implementing Consumer.

### OpenAudioLink Analog Source

ESP32 firmware implementing Producer.

In standalone mode it may provide a limited web interface that discovers receivers and controls only its own stream.

## 5. Protocol suite

OpenAudioLink uses a documented protocol suite rather than one single protocol.

The suite includes:

- RTP over UDP for audio
- local device discovery
- reliable IP control and status API
- OTA over IP
- USB serial or native USB provisioning

All interfaces shall be documented, versioned and implementation-independent.

## 6. Hardware strategy

### Target platform

ESP32-S3 is the intended long-term ESP platform.

Reference hardware:

- Receiver: ESP32-S3 + PCM5102A
- Analog Source: ESP32-S3 + PCM1808 with onboard oscillator

Reference audio format:

- stereo
- 48 kHz
- 24-bit PCM
- I²S

### Temporary development platform

Available ESP32-C3 boards may be used initially for:

- discovery
- control-plane work
- provisioning experiments
- OTA experiments
- basic RTP transport tests

The C3 is not the final audio reference platform. Code should keep hardware-specific details isolated so migration to S3 is straightforward.

## 7. Device lifecycle

```text
Blank or existing ESP
    -> USB detection
    -> firmware flash
    -> hardware profile
    -> identity
    -> Wi-Fi provisioning
    -> network discovery
    -> normal operation
    -> OTA
    -> USB recovery
```

## 8. Versioning

Track independently:

- Windows Hub version
- Receiver firmware version
- Analog Source firmware version
- protocol-suite version
- hardware-profile version

Compatibility depends on explicit protocol and hardware-profile information, not matching version strings.

## 9. Design principles

- local first
- direct Producer-to-Consumer audio path
- simple receivers
- documented interfaces
- architecture before implementation
- one primary responsibility per role
- hardware-specific code isolated behind profiles
- OTA and recovery designed from the start
- no unnecessary hardware variants
- incremental, testable development

## 10. Phase 1 status

Phase 1 is complete.

Approved decisions:

- logical role model
- control-plane/audio-plane separation
- direct RTP path
- Windows Hub as system centre
- ESP32 Analog Source as external Producer
- ESP32 Receiver as Consumer
- Windows-managed flashing, provisioning and OTA
- ESP32-S3 as target platform
- PCM5102A receiver DAC
- PCM1808 source ADC with onboard oscillator
- temporary use of ESP32-C3 during early development
