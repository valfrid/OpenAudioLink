# Board Measurements

Fill in the **Value** columns. Everything in `enclosures/` is generated
from these numbers, so they are worth measuring carefully once.

All dimensions in **millimetres**, to 0.1 mm. Use calipers rather than a
ruler — a 0.5 mm error on a connector cutout is the difference between a
plug that seats and one that does not.

## Datum convention

Same rule for every board:

- Lay the board **component side up**, oriented as in the reference photo
  noted per board below.
- **Origin** is the lower-left corner of the PCB outline.
- **X** runs right, **Y** runs away from you, **Z** is up out of the
  board's **top face**. The underside of the PCB is therefore at
  `Z = -thickness`.
- Positions of round features are to their **centre**.

If you use a different origin, say which corner and I will adapt — but
please use the same one consistently across all three boards.

## Measuring the 3.5 mm jacks

The panel cutout is driven by the **barrel centre height**, not the
outline of the plastic body, so measure both:

- *Body height* — top of the jack housing above the PCB top face.
- *Barrel centre height* — centre of the round hole the plug enters,
  above the PCB top face. Usually roughly half the body height.
- *Protrusion* — how far the jack's face sticks out past the PCB edge.
  Negative if it is set back from the edge.

---

## 1. ESP32 board

Two boards are in play. The **Seeed XIAO ESP32S3** is the preferred
platform (`docs/DECISIONS.md` §6) and Seeed publish its mechanical data,
so its dimensions may be taken from their drawings rather than measured —
worth checking against the physical board when it arrives. Its antenna is
external, so instead of a keep-out zone it needs a **U.FL pigtail route
and a bulkhead or retention point** for the antenna.

The table below is for the **ESP32-S3 Super Mini**, the secondary board
already in hand.

### 1a. ESP32-S3 Super Mini

Orientation: component side up, **USB-C connector at the far edge (+Y)**.

| #  | Measurement | Why it matters | Value |
| -- | ----------- | -------------- | ----- |
| 1  | Board length (X, long edge) | Cavity size | |
| 2  | Board width (Y, short edge) | Cavity size | |
| 3  | PCB thickness | Edge slot width | |
| 4  | USB-C body width | Panel cutout | |
| 5  | USB-C body height above PCB top | Panel cutout | |
| 6  | USB-C centre position along X | Panel cutout | |
| 7  | USB-C protrusion past the board edge | Whether the shell must be relieved | |
| 8  | Tallest top-side component height (likely a button or the USB shell) | Internal clearance | |
| 9  | Tallest bottom-side component height (0 if flat) | Standoff height | |
| 10 | Antenna keep-out: distance from the antenna end of the board that must stay clear of plastic and metal | RF range | |
| 11 | Spacing between the two pad rows (centre to centre across Y) | Header clearance / edge support | |
| 12 | Confirm: any mounting holes? If yes, diameter and positions | Retention method | |

Please also confirm **which end the antenna is at** — in the photo the
red ceramic part at the left edge looks like the chip antenna, but I do
not want to design a keep-out around the wrong component.

### 1b. XIAO ESP32S3 — measured, for the speaker-dongle receiver

Caliper measurements from the physical board, superseding the Seeed
drawing numbers for this build.

| #  | Measurement | Why it matters | Value |
| -- | ----------- | -------------- | ----- |
| 1  | Board length (X) | Cavity size | 21.5 mm |
| 2  | Board width (Y) | Cavity size | 18 mm |
| 3  | PCB thickness | Edge slot width | *needed* |
| 4  | USB-C body width | Panel cutout | 9 mm |
| 5  | USB-C body height above PCB top | Panel cutout | 3.2 mm |
| 6  | USB-C centre position along the board edge | Panel cutout | assumed centred — confirm |
| 7  | USB-C protrusion past the board edge | Whether the shell must be relieved | 1.5 mm |
| 8  | Tallest top-side component height | Internal clearance | *needed* |
| 9  | Tallest bottom-side component height | Standoff height | *needed* |
| 10 | Antenna keep-out | RF range | not applicable in the usual sense — antenna is off-board, see below |
| 11 | Spacing between the two pad rows | Header clearance | *needed* (may be moot — see assembly note below) |
| 12 | Mounting holes | Retention method | none seen — confirm |

**Antenna.** External FPC sheet, adhesive-backed (3M), **18 × 38 mm**,
connected to the board's U.FL-style pad by a short coax pigtail. Not a
keep-out zone in the PCB sense — it needs a flat glue surface inside the
shell (lid or a side wall) away from metal, plus enough slack in the
pigtail to reach it. Still needed: pigtail length from the board pad to
where the sheet will sit, and which board edge the pad is on.

---

## 2. PCM1808 ADC ("GLA ANA TO I2S 96K/24BIT")

Orientation: component side up, **3.5 mm jack on the left edge**, as in
the photo.

| #  | Measurement | Why it matters | Value |
| -- | ----------- | -------------- | ----- |
| 1  | Board length (X) | Cavity size | |
| 2  | Board width (Y) | Cavity size | |
| 3  | PCB thickness | Standoff shoulder | |
| 4  | Mounting hole diameter | Screw or boss size | |
| 5  | Mounting hole 1 centre (X, Y) from origin | Standoff positions | |
| 6  | Mounting hole spacing: X between centres, Y between centres | Standoff positions | |
| 7  | Jack **body** height above PCB top | Internal clearance | |
| 8  | Jack **barrel centre** height above PCB top | Panel hole centre | |
| 9  | Jack centre position along Y, and protrusion past the edge | Panel hole position | |
| 10 | Tallest top-side component (the electrolytic capacitor looks tallest) | Lid clearance | |
| 11 | Height of the I2S OUT and POWER headers above PCB top, with plugs fitted | Lid clearance — often the real limit | |
| 12 | Tallest bottom-side component (0 if flat) | Standoff height | |

---

## 3. PCM5102A DAC

Orientation: component side up, **3.5 mm jack at the top edge**, as in
the photo.

| #  | Measurement | Why it matters | Value |
| -- | ----------- | -------------- | ----- |
| 1  | Board length (Y, long edge) | Cavity size | 32 mm |
| 2  | Board width (X, short edge) | Cavity size | 17.25 mm |
| 3  | PCB thickness | Edge slot width | *needed* |
| 4  | Jack **body** height above PCB top | Internal clearance | *needed* |
| 5  | Jack **barrel centre** height above PCB top | Panel hole centre | *needed* |
| 6  | Jack centre position along X, and protrusion past the edge | Panel hole position | protrusion 1.5 mm; X position given as "5 mm from shortside" — **which reference edge?** confirm |
| 7  | Tallest top-side component excluding the jack | Lid clearance | *needed* |
| 8  | Tallest bottom-side component (0 if flat) | Standoff height | *needed* |
| 9  | Spacing between the two header rows (centre to centre) | Edge support width | *needed* |
| 10 | Distance from board edge to the nearest header hole centre | How much edge is free to clamp | *needed* |
| 11 | Confirm: any mounting holes? | Retention method | none seen — confirm |
| 12 | Height of soldered headers above PCB top, with sockets fitted | Stack height | not applicable — headers are bridged directly to the XIAO with short jumpers, no sockets. Board-to-board gap and any Z-offset still *needed* |

Barrel hole diameter (panel cutout, not in the table above): **5.1 mm**.

---

## Decisions that change the design

Not measurements, but they affect the geometry as much as any number:

1. **Headers or direct wiring?** Soldered headers plus sockets add
   roughly 8–12 mm of stack height. Wires soldered straight to the pads
   make a much smaller box. Which are you planning?
2. **Boards stacked or side by side?** Stacking is compact; side by side
   is thinner and keeps the analog board further from the ESP32.
   **Resolved for the speaker-dongle receiver: side by side**, with the
   USB-C edge and the line-out jack edge flush at one end — the real
   install has both cables leaving through one tight gap behind a
   speaker, so that end becomes the single connector panel.
3. **Mounting.** Free-standing on a shelf, wall-mounted, or screwed
   behind a speaker? This decides whether the shell needs keyholes or
   feet.
   For the speaker-dongle: wedged in the gap behind a speaker cabinet,
   not screwed down and not free-standing on a visible shelf — low
   priority on keyholes/feet, high priority on staying slim.
4. **Closure.** Self-tapping screws into printed bosses, heat-set
   inserts, or snap fit? Screws are the most forgiving to print; snap
   fits need the most iteration.
5. **Power.** USB-C into the ESP32 only, or a separate supply into the
   audio board? This decides how many openings the shell needs.
   For the speaker-dongle: leaning towards a **2-wire feed** into a
   panel-mount DC barrel jack rather than the XIAO's onboard USB-C —
   more flexible to place on the shell given the tight install location.
   A candidate jack was measured at **5.5 × 14 mm**, but it is not yet
   clear which two features that spans (barrel bore vs. bushing length,
   thread diameter, panel hole size) — confirm before it goes in
   `boards.scad`.
