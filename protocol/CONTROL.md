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

### GET /status

```json
{
  "oal": "0.1",
  "id": "oal-9f8e7d6c-…",
  "name": "Kitchen",
  "role": "receiver",
  "hw": "esp32s3-pcm5102a",
  "fw": "0.1.0",
  "uptimeS": 1234,
  "wifi": { "rssi": -52, "ip": "192.168.1.40" },
  "audio": { "state": "idle" }
}
```

`audio.state` is `"idle"` or `"playing"`; stream details are added in
Phase 3.

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

## Revision history

- 0.1 — initial draft: HTTP/JSON, Phase 2.4 command set.
