# OpenAudioLink Protocol Suite

Protocol-suite version: **0.1** (pre-release; breaking changes allowed until 1.0)

OpenAudioLink uses a documented suite of protocols rather than one single
protocol. Every interface is versioned and implementation-independent: any
conforming implementation may replace the reference one.

## Members of the suite

| Interface        | Transport            | Specification    | Status        |
| ---------------- | -------------------- | ---------------- | ------------- |
| Device discovery | UDP multicast, JSON  | `DISCOVERY.md`   | Draft v0.1    |
| Device identity  | (model, not a wire protocol) | `IDENTITY.md` | Draft v0.1 |
| Control / status | HTTP/JSON over TCP   | `CONTROL.md`     | Draft v0.1    |
| Audio transport  | RTP over UDP         | not yet written  | Phase 3       |
| OTA over IP      | HTTP/JSON            | `OTA.md`         | Draft v0.1    |
| USB provisioning | USB serial           | not yet written  | Phase 2.5     |

## Port assignments

| Port  | Transport | Use                                        |
| ----- | --------- | ------------------------------------------ |
| 41000 | UDP       | Discovery (multicast group `239.255.41.10`) |
| 41001 | TCP       | Device control/status API (HTTP)           |
| 41080 | TCP       | Hub web UI and REST API (default, configurable) |
| 41100+| UDP       | RTP audio streams (assigned by Controller) |

## Versioning rules

- The **protocol-suite version** is a single `major.minor` value carried in
  discovery announcements (`proto` field).
- Compatibility decisions are made from the announced protocol-suite version
  and hardware profile, never by comparing firmware or Hub version strings.
- Before 1.0, minor version bumps may break compatibility. From 1.0 on,
  additions bump minor, breaking changes bump major.
- Each specification records its own revision history in its header.

## Design constraints (from the approved architecture)

- Control plane and audio plane stay separate.
- Audio flows directly from the active Producer to the selected Consumers;
  the Hub coordinates but does not normally relay external streams.
- Receivers are simple Consumers and never need to know what the source is.
- Reference audio format: stereo, 48 kHz, 24-bit PCM, RTP/UDP, I²S at the
  hardware boundary.
