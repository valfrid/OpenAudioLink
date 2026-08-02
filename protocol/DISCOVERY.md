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
| `roles`    | string[] | yes      | Logical roles held; see below                       |
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
  "roles": ["consumer"],
  "hw": "esp32s3-devkit",
  "fw": "0.1.0",
  "caps": ["control-v0"],
  "ctrlPort": 41001
}
```

#### Roles

Roles are capabilities, not device types (`docs/ARCHITECTURE.md` section 2),
and a device may hold several — an analog source that also plays is both a
producer and a consumer. `roles` is therefore a list, never a single value.

| Role         | Meaning                                                   |
| ------------ | --------------------------------------------------------- |
| `controller` | Discovery, source selection, receiver assignment, routing |
| `producer`   | Generates an RTP audio stream                             |
| `consumer`   | Receives and plays audio                                  |

An announce carries at least one role. A receiver ignores roles it does
not recognise but must keep the ones it does, so a node running newer
firmware is not treated as less capable than it claims.

The Provisioner role of the architecture is not announced: nothing on the
network acts on knowing which device performs USB flashing and recovery.

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
  Later in 0.1, still draft: `role` (one of `receiver`, `analog-source`,
  `hub`) replaced by `roles`, a list drawn from the architecture's own
  vocabulary. Two names for one concept was the problem; a device holding
  several roles at once was the thing the single field could not express.

## The `claimed` field

An announcement may carry `"claimed": true`. It means the sender holds the
Controller role because nobody else did, rather than because it was
configured to (`docs/DECISIONS.md` decision 9).

Precedence uses it: a configured Controller outranks a claimer, and among
equals the lower `id` wins. Every node computes the same answer alone, from
announcements already on the wire — there are no votes and no terms.

The field is absent rather than false when not claiming, so a node that
does not understand it sees exactly what it saw before.

## The `address` field

An announcement may carry `"address"`. It is where the sender should be
reached, and it takes precedence over the datagram's source address.

Reading the source is right for a device announcing for itself on its own
segment, and it is what a node does when the field is absent. It is wrong
the moment anything rewrites the source: a VPN subnet router that NATs what
it forwards had a Hub recorded at the router's address, so every node
addressed the Controller at a machine that was not running one. Nothing
about discovery looked wrong — the Hub was found, with the right name, id,
roles and port — and joining could never have worked.

The Hub sets it per destination, choosing the same-subnet address that
device should use, the same way an OTA URL is chosen.

## When a device is discovered but cannot be reached

Discovery succeeding says a device's announcements arrive. It says nothing
about whether the address in them is reachable, and the two can differ in a
way that looks like nothing being wrong at all.

The case that cost an evening: a laptop running Tailscale had accepted a
subnet route for `192.168.0.0/24` — its own LAN — advertised by a node on a
remote network using the same range. Windows preferred the tunnel by a
metric of 5 against 306, and the subnet router NATed what it forwarded, so
nodes recorded the Hub at the router's address on the far network. The Hub
appeared in every peer table with the correct id, name, roles and port.
Only the address belonged to a machine running nothing, and no request from
a node could ever arrive.

What made it hard to see is that every direction that had ever been used
still worked. The Hub polls nodes, pushes firmware and starts streams, and
all of that is Hub to node. Nothing needed to reach the Hub until a
Consumer had to ask a Controller to join, so the fault had been latent for
the entire life of the project.

Worth checking, in this order:

1. Is the device in `GET /peers` on a node at all? If not, the announcement
   is not arriving — on Windows, outbound multicast follows the routing
   table and a VPN adapter routinely wins it.
2. Is the address in that entry one the Hub actually has? Compare against
   `ipconfig`. An address that is not on the machine means something
   rewrote the source.
3. Does `curl http://<that address>:41080/api/health` answer? A refusal
   from an address that pings is a different machine answering to the same
   number on another network.
4. `Get-NetRoute -DestinationPrefix <your LAN>/24` — more than one route,
   with a VPN interface winning on metric, is the cause. A ping that
   answers in tens of milliseconds with no `arp -a` entry confirms it.

`tailscale up --accept-routes=false` removes it. Routing a local network
through a VPN gains nothing and costs tens of milliseconds on every packet
to a device in the same room, which for synchronised audio is not a detail.

The `address` field above exists because of this: a Controller states where
it is rather than letting the network infer it.
