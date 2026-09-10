# OpenAudioLink OTA Protocol

Version: 0.1 (draft)
Part of protocol-suite 0.1.

## Overview

OTA is pull-based: the Controller tells a device to fetch a firmware image
over HTTP, and the device installs it into its inactive OTA slot and
reboots.

**The Hub is not privileged here — it just happens to hold the file.** A
device is given a URL and fetches it; nothing in the protocol says who
serves it. Any Controller that can name a URL the device can reach can
drive an update, which is what makes a Hub-less deployment possible in
principle. Today the Hub is the only thing that does, because it is the
only thing with a firmware store and an HTTP server.

There is one real constraint on that, and it is not in this protocol but
in the device: see *Plain HTTP, and what it would take to fetch a
release* below.

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
{ "url": "http://192.168.1.10:41080/firmware/testnode-esp32s3-ota.bin" }
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

### Version

A device's firmware version is the `version` field of the `esp_app_desc_t`
in its image header, which is also what it reports in discovery announces
and `GET /status`. Taking both from the same place means an image cannot
claim one version and announce another.

The Controller should read that field out of a stored image and show it,
because the failure it prevents is silent: installing an image that
carries the version already running completes normally, reboots, and
leaves the device reporting exactly what it reported before. Without the
version on display that is indistinguishable from an update that did
nothing.

## Plain HTTP, and what it would take to fetch a release

The obvious question, given that the device fetches a URL: why not point
it straight at a GitHub release and drop the upload-to-Hub step
altogether? Nothing in this protocol forbids it. The device does.

`ota_task()` in `oal_control.c` calls `esp_https_ota()` with a config
carrying only `.url`, `.timeout_ms` and `.keep_alive_enable` — **no
`crt_bundle_attach` and no `cert_pem`** — and `CONFIG_MBEDTLS_CERTIFICATE_BUNDLE`
is not set. There are therefore no root certificates to verify a server
against, so in practice only **plain HTTP** works. GitHub is HTTPS-only.
That is why this document says "over HTTP" and means it.

Closing it is small: enable the certificate bundle and attach it. Two
things to test rather than assume before relying on it:

- **Redirects.** GitHub sends release downloads to
  `objects.githubusercontent.com`, so the fetch is cross-host and lands on
  a different certificate than the one first presented.
- **Memory.** A TLS handshake wants tens of kilobytes, and the OTA path
  already carries a note about `esp_https_ota` allocating from the pool
  that leaves a node unable to be updated when it is short.

What it would buy is worth the work: the Hub would stop needing to be a
file host, a phone or any other Controller could drive an update without
serving anything itself, and an image would be fetched from the same
place CI published it rather than from a copy somebody uploaded by hand.

## Not yet in 0.1 (planned per roadmap 2.6)

- device-side checksum/signature verification before reboot
- hardware-profile/protocol compatibility checks before offering an update
- automatic rollback on failed boot
- update progress reporting

## Revision history

- 0.1 — initial draft: pull-based OTA from the Hub over plain HTTP.
  Later corrected in place: the Hub is where the file happens to live,
  not a role the protocol requires. Plain HTTP is a device limit — no
  certificate bundle is compiled in — rather than a protocol one.
