# OpenAudioLink OTA Protocol

Version: 0.1 (draft)
Part of protocol-suite 0.1.

## Overview

OTA is pull-based: the Controller tells a device to fetch a firmware image
over HTTP, and the device installs it into its inactive OTA slot and
reboots. The Hub acts as the firmware repository.

## Flow

```text
Operator uploads image to Hub (POST /api/firmware)
Hub -> device: POST /ota { "url": "http://<hub>:41080/firmware/<file>" }
Device -> Hub: HTTP GET of the image
Device: writes inactive OTA slot -> verifies image header -> reboots
Device: announces with the new fw version -> visible in Hub UI
```

## Device endpoint

`POST /ota` on the device control port (41001):

```json
{ "url": "http://192.168.1.10:41080/firmware/testnode-esp32c3-ota.bin" }
```

Response `200 { "status": "accepted" }` — the download and install proceed
asynchronously; progress is observable via logs and, ultimately, the new
version in discovery announces. A device that fails the update keeps
running its current firmware.

### Choosing the URL host

The Controller must advertise an address the device can actually reach.
A host running a VPN or overlay network (Tailscale, ZeroTier, Docker,
Hyper-V) has several local addresses, and the routing table's preferred
one is often not on the device's network — the device then fails at
connect with no useful diagnosis. Pick the Controller address whose
subnet contains the device, falling back to a routed address only when no
local subnet does.

## Images

- OTA images are application images only (not merged flash images).
- Devices use two OTA app slots; an interrupted update never bricks the
  running slot. USB recovery remains available per the device lifecycle.
- The Hub records size and SHA-256 for every stored image
  (`GET /api/firmware`).

## Not yet in 0.1 (planned per roadmap 2.6)

- device-side checksum/signature verification before reboot
- hardware-profile/protocol compatibility checks before offering an update
- automatic rollback on failed boot
- update progress reporting

## Revision history

- 0.1 — initial draft: pull-based OTA from the Hub over plain HTTP.
