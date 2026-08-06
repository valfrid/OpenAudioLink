# OpenAudioLink Enclosures

3D-printable enclosures for the receiver and analog source nodes.

Status: measuring the receiver boards (XIAO ESP32S3 + PCM5102A, the
"speaker-dongle" build) in `MEASUREMENTS.md`. No geometry yet.

## Approach

Enclosures are **parametric source, not binary models**. They are written
in OpenSCAD, so every dimension is a named parameter, changes are
reviewable in a diff like any other code, and CI exports the STLs as
downloadable artifacts — the same pattern as the firmware images and the
Hub package. Nobody needs CAD software installed to get a printable file.

```text
MEASUREMENTS.md   Board dimensions to fill in — the input to everything here
boards.scad       Measured board data as parameters (generated from the above)
receiver.scad     ESP32-S3 + PCM5102A enclosure
source.scad       ESP32-S3 + PCM1808 enclosure
coupon.scad       Fit-test plate: standoffs and cutouts only, no walls
```

## Print a coupon before an enclosure

`coupon.scad` produces a thin plate carrying only the board retention and
the connector cutouts. It prints in minutes rather than hours and is the
cheapest way to find out that a dimension is wrong. Do not print a full
enclosure until a coupon fits.

## Design constraints

These come from the architecture and the hardware, not from taste:

- **Antenna mounting, not clearance.** The preferred board (Seeed XIAO
  ESP32S3) has no PCB antenna — only a U.FL connector — so the enclosure
  needs a way to get the antenna *outside*: a U.FL-to-SMA pigtail through
  a bulkhead hole, or a retention point for the supplied flexible
  antenna. This replaces the keep-out zone a PCB antenna would have
  needed, and is the main reason that board was chosen.
  The antenna is not optional: the board is effectively deaf without it.
  Boards that do have a PCB antenna (the ESP32-S3 Super Mini) still need
  that zone kept clear of thick plastic and any metal.
- **USB must stay accessible.** The device lifecycle in
  `docs/ARCHITECTURE.md` ends in USB recovery, so the port has to be
  reachable without disassembling the enclosure.
- **Analog separation.** Keep the audio jack and its wiring away from the
  ESP32 and any switching regulator to limit injected noise.
- **Ventilation.** The ESP32-S3 warms up under sustained Wi-Fi load.
- **Print without supports.** Overhangs at 45 degrees or shallower.

## Retention: two of the three boards have no mounting holes

From the supplied photos, the PCM1808 board has four corner mounting
holes, but the ESP32-S3 Super Mini and the PCM5102A module appear to have
none — only pad and header holes. Those two therefore cannot be screwed
down and must be held another way: edge slots the PCB slides into,
printed clips over the board edges, or captured between the two halves of
the shell on soft pads.

This needs confirming against the physical boards, because it decides the
internal structure of both enclosures.

The XIAO ESP32S3 is a different board again, and Seeed publish its
dimensions and mechanical models — so unlike the anonymous modules it may
not need measuring by hand at all. Confirm against the physical board on
arrival.
