# OpenAudioLink Master Prompt

Version: Phase 1 Architecture v1.0

You are assisting with the implementation of OpenAudioLink.

Treat the architecture below as approved. Do not redesign it unless explicitly asked.

## Project vision

OpenAudioLink is a local-first, open-source synchronized multi-room audio platform.

It distributes stereo audio over a local IP network using a Windows Hub and inexpensive ESP32 audio nodes.

There is no required cloud dependency.

## Logical roles

### Controller

Coordinates devices, sources, receivers, routing, ownership, volume, configuration and diagnostics.

Normally implemented by the Windows Hub.

An Analog Source may implement a limited Controller role for its own stream in standalone mode.

### Producer

Generates RTP audio.

Examples:

- Windows Hub for Windows-hosted sources
- ESP32 Analog Source for analog input

### Consumer

Receives and plays RTP audio.

Implemented by the ESP32 Receiver.

### Provisioner

Manages flashing, provisioning, OTA, compatibility and recovery.

Implemented by the Windows Hub.

## Control plane and audio plane

Keep them separate.

Control plane:

- discovery
- routing
- assignment
- volume
- diagnostics
- configuration
- OTA
- provisioning

Audio plane:

- active Producer sends RTP/UDP directly to selected Consumers

External audio is not normally relayed through the Windows Hub.

## Software deliverables

1. OpenAudioLink Hub for Windows
2. OpenAudioLink Receiver firmware
3. OpenAudioLink Analog Source firmware

## Hardware baseline

Target hardware:

- Receiver: ESP32-S3 + PCM5102A DAC
- Analog Source: ESP32-S3 + PCM1808 ADC with onboard oscillator

Reference audio format:

- stereo
- 48 kHz
- 24-bit PCM
- RTP over UDP
- I²S at the audio hardware boundary

Temporary development hardware:

- available ESP32-C3 boards may be used initially for discovery, control-plane, provisioning, OTA and basic RTP work
- keep hardware-specific code isolated
- ESP32-S3 remains the reference target

## Windows-first implementation strategy

The Windows Hub is the centre of the system and should be developed first.

Initial Windows priorities:

1. repository skeleton
2. service and API skeleton
3. web UI shell
4. device model
5. discovery
6. basic control commands
7. USB flashing and provisioning
8. OTA management
9. Windows RTP Producer
10. receiver integration

Likely Windows architecture:

- background service for orchestration
- ASP.NET Core API
- browser-based UI
- optional desktop agent for session-bound WASAPI capture

## Protocol suite

Do not call it one protocol.

The suite includes:

- RTP/UDP audio transport
- local discovery
- reliable IP control/status
- OTA over IP
- USB provisioning

All interfaces must be:

- documented
- versioned
- implementation-independent
- testable

## Receiver rules

The Receiver is a simple Consumer.

It should not know whether the source is Spotify, Windows, USB, analog or internet radio.

Responsibilities:

- receive RTP
- jitter buffer
- clock correction
- I²S DAC output
- volume/mute
- status
- OTA
- recovery

## Analog Source rules

The Analog Source is primarily a Producer.

Responsibilities:

- I²S ADC capture
- packetization
- timestamps
- RTP transmission
- status
- OTA
- recovery

In standalone mode it may provide only the limited Controller functions required to route its own stream.

## Device lifecycle

```text
USB detect -> flash -> profile -> identity -> Wi-Fi -> discovery -> normal use -> OTA -> USB recovery
```

## Versioning

Track separately:

- Hub version
- Receiver firmware
- Analog Source firmware
- protocol-suite version
- hardware-profile version

## Design rules

- preserve role separation
- preserve direct Producer-to-Consumer audio flow
- keep receivers simple
- keep hardware abstractions portable
- prefer incremental and testable work
- do not add post-1.0 features prematurely
- architecture before implementation
- no unnecessary hardware variants
- local-first operation
- OTA and recovery from the beginning

## Current project state

Phase 1 is complete.

ADC and DAC boards have been ordered and are expected in about one week.

Development should now enter Phase 2, starting with the Windows Hub and repository foundation.

The first ESP tests may use available ESP32-C3 boards. Move the reference audio implementation to ESP32-S3 when the ordered audio boards are available.

## First task for a new development session

Inspect the GitHub repository and create the Phase 2 repository foundation without changing the approved architecture.

Begin with:

- repository layout
- build system
- Windows Hub skeleton
- protocol model
- device identity model
- discovery design
- minimal ESP test firmware

Do not start advanced audio features until the repository, discovery and provisioning foundations are stable.
