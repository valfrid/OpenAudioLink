# OpenAudioLink Device Identity Model

Version: 0.1 (draft)
Part of protocol-suite 0.1.

## Identity

Every device has exactly one stable identity string, used as the key in
discovery, control, routing and OTA:

- **Provisioned identity**: `oal-<uuid>` where `<uuid>` is a lowercase
  UUIDv4 assigned by the Provisioner during USB provisioning and stored in
  device NVS. It survives OTA updates and reflashing that preserves NVS.
- **Factory identity** (before provisioning, or after factory reset):
  `mac-<12 lowercase hex digits>` derived from the primary Wi-Fi MAC.

Identity is never derived from the IP address. Renaming a device changes its
`name`, never its `id`.

## Descriptive attributes

Alongside its identity, every device reports:

| Attribute          | Example              | Notes                                    |
| ------------------ | -------------------- | ---------------------------------------- |
| `name`             | `"Kitchen"`          | Assigned via provisioning or `rename`    |
| `roles`            | `["consumer"]`       | A set; see `DISCOVERY.md`                |
| `hw` (profile)     | `"esp32s3-pcm5102a"` | See hardware profiles below              |
| `fw`               | `"0.1.0"`            | Firmware/application version (semver)    |
| `proto` / `oal`    | `"0.1"`              | Protocol-suite version                   |
| `caps`             | `["control-v0"]`     | Capability identifiers                   |

Compatibility decisions use the protocol-suite version and hardware profile,
never firmware version string comparison.

## Hardware profiles

A hardware profile identifies the audio-relevant hardware configuration and
selects the matching firmware behaviour (I²S pins, clocking, DAC/ADC init).
Initial profiles:

| Profile id         | Platform  | Audio hardware                | Purpose                     |
| ------------------ | --------- | ----------------------------- | --------------------------- |
| `xiao-esp32s3-pcm5102a` | XIAO ESP32S3 | PCM5102A stereo I²S DAC  | **Preferred Consumer**      |
| `xiao-esp32s3-pcm1808`  | XIAO ESP32S3 | PCM1808 stereo I²S ADC   | **Preferred Producer**      |
| `xiao-esp32s3`     | XIAO ESP32S3 | none                       | Development                 |
| `esp32s3-pcm5102a` | ESP32-S3 Super Mini | PCM5102A stereo I²S DAC | Secondary Consumer     |
| `esp32s3-pcm1808`  | ESP32-S3 Super Mini | PCM1808 stereo I²S ADC  | Secondary Producer      |
| `esp32s3-devkit`   | ESP32-S3 Super Mini | none                    | Development             |
| `esp32c3-devkit`   | ESP32-C3  | none                          | Temporary; removed once S3 is verified |

The board is part of the profile, not just the audio hardware: pin
mapping differs between boards, and the XIAO has an external antenna
where the Super Mini has a PCB one.

Hardware-profile definitions are versioned independently of firmware.

## Roles

Roles are logical, per the architecture: a device may implement several,
so `roles` is a set and every role a device holds appears in it. A
standalone producer that also arbitrates announces
`["producer", "controller"]` rather than naming one role and hiding the
rest behind a capability flag.

Roles are stored in device NVS and are configuration, not a property of
the binary — one firmware image serves every node (`docs/DECISIONS.md`
decision 5). The hardware profile constrains which roles are possible: a
board with a DAC and no ADC cannot be a producer.

Unset roles default to `["consumer"]`, which is the common case.

## Revision history

- 0.1 — initial draft: provisioned/factory identity, attributes, initial
  hardware profiles. Later in 0.1, still draft: the single `role`
  attribute became the `roles` set, using the architecture's vocabulary.
