# OpenAudioLink Control and Status Protocol

Version: 0.1 (draft)
Part of protocol-suite 0.1.

## Overview

The control protocol is the reliable control-plane channel between a
Controller (normally the Hub) and a device. It carries commands, status and
configuration — never audio.

## Transport

- HTTP/1.1 over TCP, JSON request and response bodies.
- Each device runs a small HTTP server on TCP port `41001` (announced as
  `ctrlPort` in discovery).
- No authentication in 0.1 (trusted local network); an authentication layer
  is planned before 1.0.

## Common behaviour

- Successful responses use `200 OK` with a JSON body.
- Errors use an appropriate HTTP status and body
  `{ "error": "<machine-readable-code>", "message": "<human text>" }`.
- Unknown JSON fields in requests must be ignored.

## Endpoints (Phase 2.4 command set)

| Method | Path              | Purpose                                     |
| ------ | ----------------- | ------------------------------------------- |
| GET    | `/status`         | Read status                                 |
| POST   | `/identify`       | Physically identify the device (blink LED)  |
| POST   | `/rename`         | Set the human-readable name                 |
| POST   | `/reboot`         | Reboot the device                           |
| GET    | `/config`         | Read configuration                          |
| PUT    | `/config`         | Write configuration                         |
| POST   | `/factory-reset`  | Request factory reset (clears identity, Wi-Fi, config) |
| POST   | `/config`         | Set the roles the node takes                |
| GET    | `/stream`         | Stream state and measurement counters       |
| POST   | `/stream/start`   | Producer: begin streaming to destinations   |
| POST   | `/stream/stop`    | Stop a producer; clear a consumer's counters |
| POST   | `/ota`            | Pull and install a firmware image (see `OTA.md`) |
| GET    | `/peers`          | Other nodes this one has heard announce      |

### GET /status

```json
{
  "oal": "0.1",
  "id": "oal-9f8e7d6c-…",
  "name": "Kitchen",
  "roles": ["consumer"],
  "hw": "esp32s3-pcm5102a",
  "fw": "0.1.0",
  "uptimeS": 1234,
  "heapFree": 206936,
  "wifi": {
    "joined": true,
    "ssid": "valfrid-n",
    "bssid": "7c:10:c9:7a:0b:d0",
    "channel": 9,
    "rssi": -68
  },
  "audio": { "state": "idle" }
}
```

`audio.state` is `"idle"` or `"playing"`; stream details are added in
Phase 3.

When the device is not associated, `wifi` is `{ "joined": false }` and the
remaining fields are absent rather than zero — a missing reading and a
reading of zero are different things.

The **BSSID matters as much as the RSSI**. In a mesh every access point
advertises the same SSID, so signal strength alone cannot distinguish a
node far from the right access point from one attached to the wrong one.

Volatile state lives here and not on the discovery announce. Signal
strength changes constantly, and an announce is multicast to every device
on the network every few seconds; a Controller that wants telemetry asks
for it.

### GET /peers

What this node has heard from other nodes on the discovery group. Every
node has always received these announcements and until now discarded them,
which is workable only while a PC holds the Controller role. A party system
has no PC, and a Controller that cannot see the speakers cannot route to
them (`docs/DECISIONS.md` decision 9).

```json
{
  "peers": [
    {
      "id": "mac-7c10c97a0bd0",
      "name": "PartySpeaker",
      "roles": ["consumer"],
      "address": "192.168.0.71",
      "ctrlPort": 41001,
      "ageMs": 2431
    }
  ]
}
```

Newest announcement first. A peer heard from more than 30 s ago — six
missed announces — is omitted rather than listed as stale, so nothing has
to interpret `ageMs` to know who is present. A node never lists itself,
though its own multicast does come back to it.

`ageMs` is the age of the announcement, not a timestamp: nodes have no
shared clock, and an absolute time from one would mean nothing to another.

### POST /config

Sets the roles a node takes (`docs/DECISIONS.md` decision 5): one firmware
image serves every node, and what a given board does is configuration.

Request `{ "roles": ["consumer"] }` → response
`{ "status": "stored", "roles": ["consumer"], "appliesAt": "reboot" }`.

At least one role is required, and an unrecognised name rejects the whole
request with `400` rather than silently storing a subset. The roles take
effect at the next boot, because they decide which tasks start; changing
them under a running node would mean tearing down live audio.

### POST /rename

Request `{ "name": "Kitchen" }` → response `{ "name": "Kitchen" }`.
The new name must appear in subsequent announces.

### PUT /config

Whole-document replace of the device's persisted configuration object.
The schema is owned by the device's hardware profile; unknown keys are
rejected with `400`.

### POST /reboot, /identify, /factory-reset

Empty request body. `reboot` and `factory-reset` respond `200` before
acting (within 1 s). `factory-reset` returns the device to factory identity
and un-provisioned state; it remains flashable/provisionable over USB.

## Hub REST API

The Hub exposes its own REST API (default TCP `41080`) for the web UI and
integrations. It is a superset consumer of this protocol: `/api/health`,
`/api/devices`, and per-device command proxying. The Hub API is documented
with the Hub itself and versioned with the Hub, not with the protocol suite;
only the device-facing endpoints above are part of the suite.

### Stream endpoints

A **producer** is told where to send; it cannot know that itself:

```json
{ "destinations": ["192.168.0.71"], "port": 41100,
  "source": "pattern", "toneHz": 1000 }
```

A **consumer** listens from boot and is never told to start. It has
nothing to configure, and a receiver that must be armed before it can be
sent to makes every producer start a race.

`GET /stream` returns the counters, shaped by the node's role — packets
sent and pacing slips for a producer, reception statistics for a
consumer. Both are needed to read a result: loss at a consumer means
nothing without knowing the producer kept its rate.

`source` selects the synthetic signal, which exists so the network can be
characterised before any ADC or DAC does. `pattern` derives every sample
from its absolute frame index, so a consumer recomputes what it should
have received and counts what differs — corruption a sequence-number
check cannot see. `tone` is a sine, for listening once a DAC exists, and
payload errors are meaningless against it.

`POST /stream/stop` clears a consumer's counters as well as stopping a
producer, because clearing them is how the next measurement begins.

## Polling

A Controller may poll `/status` to display device health. The Hub does so
every 10 seconds for devices it believes online, with a short timeout: a
node that does not answer promptly would give a stale reading anyway, and
the next cycle comes round shortly. A failed poll leaves the previous
reading in place rather than blanking it, and readings carry the time
they were taken so nothing is shown as fresher than it is.

## Revision history

- 0.1 — initial draft: HTTP/JSON, Phase 2.4 command set.
  Later additions within 0.1: `/stream*` measurement endpoints, `/peers`.
