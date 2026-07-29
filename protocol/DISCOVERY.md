# OpenAudioLink Discovery Protocol

Version: 0.1 (draft)
Part of protocol-suite 0.1.

## Overview

Discovery lets the Hub (Controller) learn which OpenAudioLink devices exist on
the local network, and lets devices be found without any configuration beyond
Wi-Fi credentials. It is a control-plane interface.

Discovery is push-based: devices periodically multicast an **announce**
message. A Controller may additionally multicast a **probe** to request an
immediate announce, so a freshly started Hub does not have to wait a full
announce interval.

## Transport

- UDP, multicast group `239.255.41.10`, port `41000`.
- Multicast TTL 1 (link-local only; OpenAudioLink is local-first).
- Payload: UTF-8 JSON, one message per datagram, maximum 1400 bytes.

## Messages

Every message carries:

| Field  | Type   | Meaning                                          |
| ------ | ------ | ------------------------------------------------ |
| `oal`  | string | Protocol-suite version, e.g. `"0.1"`             |
| `type` | string | `"announce"` or `"probe"`                        |

### announce

Sent by every device (including the Hub itself):

- on boot, once the network is up,
- every 5 seconds thereafter,
- immediately (unicast to the prober's source address and port) when a
  `probe` is received.

Fields in addition to the common ones:

| Field      | Type     | Required | Meaning                                             |
| ---------- | -------- | -------- | --------------------------------------------------- |
| `id`       | string   | yes      | Stable device identity (see `IDENTITY.md`)          |
| `name`     | string   | yes      | Human-readable name                                 |
| `role`     | string   | yes      | `"receiver"`, `"analog-source"` or `"hub"`          |
| `hw`       | string   | yes      | Hardware-profile identifier                         |
| `fw`       | string   | yes      | Firmware / application version                      |
| `caps`     | string[] | no       | Capability identifiers                              |
| `ctrlPort` | number   | no       | TCP port of the device control API (default 41001)  |

Example:

```json
{
  "oal": "0.1",
  "type": "announce",
  "id": "mac-a0b1c2d3e4f5",
  "name": "testnode",
  "role": "receiver",
  "hw": "esp32c3-devkit",
  "fw": "0.1.0",
  "caps": ["control-v0"],
  "ctrlPort": 41001
}
```

### probe

Sent by a Controller to the multicast group. No additional fields:

```json
{ "oal": "0.1", "type": "probe" }
```

Devices answer with a unicast `announce` to the datagram's source address and
source port, after a random delay of 0–500 ms to avoid a reply burst.

A Controller **should probe periodically**, not only at startup, and should
send the probe by unicast to devices it already knows in addition to the
multicast group. Multicast frames are unacknowledged and never retransmitted
over Wi-Fi, so periodic announces alone make a healthy device appear to flap
between online and offline. A unicast probe and the unicast announce it
draws both get link-layer retries, which makes liveness reliable without
changing the protocol.

## Liveness

A Controller marks a device **online** when an announce arrives and
**offline** after 30 s of silence. The window is deliberately wider than
three announce intervals because multicast loss is normal on Wi-Fi; with
the Controller also probing directly, a device has several chances to
report in before it is written off. The device's IP address is taken from the most recent
announce datagram's source address.

## Receiver rules

- Unknown JSON fields must be ignored (forward compatibility).
- Messages that are not valid JSON, lack `oal`/`type`, or have an
  incompatible protocol-suite major version are silently dropped.
- `id` is the device key: a changed IP address for a known `id` is an update,
  not a new device.

## Revision history

- 0.1 — initial draft: announce/probe over UDP multicast, JSON payload.
