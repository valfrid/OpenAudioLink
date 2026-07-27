# Windows Audio Capture

Status: decided, not yet implemented (Phase 3)

## Target use case

```text
User plays Spotify (or anything else) on the PC
    -> Hub captures the audio
    -> RTP/UDP to every selected receiver node
    -> PC keeps playing through its own speakers as normal
```

The user selects nothing, installs nothing and reconfigures nothing in
Windows. Whatever is audible on the PC is what the receivers play.

## Decision: WASAPI loopback capture

The Hub captures audio with **WASAPI loopback** on the active render
endpoint. Loopback attaches to a normal output device and returns a copy
of what is being played to it, so local playback continues unaffected.

No virtual audio device is involved.

### Options considered and rejected

| Option | Why not |
| ------ | ------- |
| Third-party virtual cable (VB-CABLE and similar) | Selecting the cable as the output device silences the PC's own speakers, which is wrong for a system where the PC is often also a listening position. Adds a manual install, and its donationware licence prevents bundling it with the Hub. |
| Our own virtual audio driver | A kernel-mode driver needs an EV certificate, Microsoft attestation signing and maintenance across Windows releases. A sub-project, not a feature — revisit only if a real requirement appears. |

Neither is needed for the target use case. A virtual cable remains a
route users can choose themselves if they ever want strict per-app
streaming; it is not part of the product.

### Per-application capture, later

If "stream only Spotify, not notification sounds" becomes a requirement,
the answer is **process loopback capture**
(`ActivateAudioInterfaceAsync` with `AUDIOCLIENT_ACTIVATION_PARAMS`,
Windows 10 build 20348 / Windows 11 and later), which captures a single
process tree with no third-party software. This is a later refinement,
not a Phase 3 goal.

Note this is unrelated to *Spotify Connect* in the roadmap, where a node
appears as a Spotify playback target and no PC is involved at all.

## Where the capture runs

Loopback capture must run **in the logged-in user's session**. A Windows
service runs in session 0 and cannot reach the user's audio endpoints, so
capture belongs to the desktop audio agent described in
`ARCHITECTURE.md`, not to the background service. The agent captures and
produces RTP; the service keeps its Controller and Provisioner roles.

Consequence: streaming a Windows-hosted source requires a logged-in
session. Sources that need no user session (analog input, and later
internet radio) are unaffected, which is consistent with the Analog Source
being an independent Producer.

## Format conversion

The endpoint dictates what we capture — typically 32-bit float, stereo,
at whatever rate the device is configured for (48 kHz commonly, 44.1 kHz
often). The reference wire format is 24-bit big-endian PCM at 48 kHz
(`protocol/AUDIO-RTP.md`), so the agent must:

1. Read the endpoint's shared-mode mix format rather than assume one.
2. Resample to 48 kHz when the endpoint is not already there.
3. Convert samples to 24-bit and **byte-swap to big-endian** for RTP.

Assuming the mix format silently produces wrong-speed or wrong-pitch
audio; skipping the byte swap produces loud static.

## Idle endpoints

A loopback stream can stop delivering packets when nothing is playing to
the endpoint, and a machine with no output device present has nothing to
attach to at all. The agent must therefore:

- handle "no active render endpoint" without crashing or spinning,
- keep the stream alive while idle — the common remedy is to hold a
  silent render stream open on the same endpoint,
- send no RTP while a stream is idle rather than emitting garbage, and
  mark the first packet after a gap so receivers can reset cleanly.

## One source, many receivers

The Producer sends a copy of each packet to every selected receiver
(unicast is the default, per `protocol/AUDIO-RTP.md`). All copies carry
the same RTP timestamps, so receivers applying the same playout delay
stay roughly aligned.

Bandwidth per receiver, L24 stereo at 48 kHz with 5 ms packets:

```text
payload   48000 x 2 x 3 bytes/s            = 2.30 Mbit/s
headers   200 pkt/s x 40 bytes (RTP+UDP+IP) = 0.06 Mbit/s
total                                       ~ 2.37 Mbit/s
```

Four receivers is roughly 9.5 Mbit/s leaving the PC. That is comfortable
on decent Wi-Fi, but airtime — not link rate — is the real limit once
several ESP32 nodes share the same access point. Multicast or wired
segments become the answer well before bandwidth does.

Sample-accurate alignment between receivers needs a shared clock (PTP);
until then, expect receivers to be close but not phase-locked. This is
acceptable for music in separate rooms and is the known gap to close in
Phase 3.

## Latency budget

Capture buffer, 5 ms packetisation, network transit and the receiver's
jitter buffer put end-to-end latency in the region of 60–100 ms. That is
fine for music, which is the target use case. Lip-sync with video — a TV
as a source — needs a tighter budget and is not addressed by this design.
