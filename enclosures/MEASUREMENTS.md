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
| 1  | Board length (Y, long edge) | Cavity size | |
| 2  | Board width (X, short edge) | Cavity size | |
| 3  | PCB thickness | Edge slot width | |
| 4  | Jack **body** height above PCB top | Internal clearance | |
| 5  | Jack **barrel centre** height above PCB top | Panel hole centre | |
| 6  | Jack centre position along X, and protrusion past the edge | Panel hole position | |
| 7  | Tallest top-side component excluding the jack | Lid clearance | |
| 8  | Tallest bottom-side component (0 if flat) | Standoff height | |
| 9  | Spacing between the two header rows (centre to centre) | Edge support width | |
| 10 | Distance from board edge to the nearest header hole centre | How much edge is free to clamp | |
| 11 | Confirm: any mounting holes? | Retention method | |
| 12 | Height of soldered headers above PCB top, with sockets fitted | Stack height | |

---

## Decisions that change the design

Not measurements, but they affect the geometry as much as any number:

1. **Headers or direct wiring?** Soldered headers plus sockets add
   roughly 8–12 mm of stack height. Wires soldered straight to the pads
   make a much smaller box. Which are you planning?
2. **Boards stacked or side by side?** Stacking is compact; side by side
   is thinner and keeps the analog board further from the ESP32.
3. **Mounting.** Free-standing on a shelf, wall-mounted, or screwed
   behind a speaker? This decides whether the shell needs keyholes or
   feet.
4. **Closure.** Self-tapping screws into printed bosses, heat-set
   inserts, or snap fit? Screws are the most forgiving to print; snap
   fits need the most iteration.
5. **Power.** USB-C into the ESP32 only, or a separate supply into the
   audio board? This decides how many openings the shell needs.
