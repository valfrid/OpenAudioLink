// Measured board data for the speaker-dongle receiver
// (XIAO ESP32S3 + PCM5102A DAC), generated from MEASUREMENTS.md
// sections 1b and 3, plus the assembly photos from the design
// conversation. Pure parameters — no geometry here, see coupon.scad.
//
// Assembly layout: the two boards sit end to end with their header
// rows facing each other across a gap, the wiring docs/HARDWARE.md
// describes (VIN opposite VUSB, no crossed leads). The DAC's line-out
// jack is at the far end from the gap; the XIAO's USB-C is on its east
// short edge, near the gap; the antenna pigtail leaves near the XIAO's
// south-west corner.
//
// Shared coordinate system for the whole assembly:
//   X: west -> east
//   Y: north (the DAC's jack edge, Y=0) -> south (the XIAO's far edge)
//   Z: up, from the floor the boards are glued to
//
// Both boards are assumed centred on one shared X centreline — not yet
// confirmed against the real glued assembly. Check it on the coupon
// before it goes into a full shell.

// ---- PCM5102A DAC ----
dac_width               = 17.25; // X extent
dac_length              = 32;    // Y extent: jack edge (north) to header edge (south)
dac_pcb_thickness       = 1.6;
dac_jack_diameter       = 5.1;   // panel hole for the barrel
dac_jack_protrusion     = 1.5;   // past the north edge
dac_jack_y              = 5;     // north edge to barrel centre, along Y
// Body height above PCB top: measured as a 6.5mm total stack including
// the PCB, so 6.5 - dac_pcb_thickness.
dac_jack_body_height    = 6.5 - dac_pcb_thickness;
// Barrel centre height above PCB top was not measured directly.
// MEASUREMENTS.md's rule of thumb is "roughly half the body height" —
// treat this as an estimate to confirm, not a caliper reading.
dac_jack_barrel_height  = dac_jack_body_height / 2;
dac_top_component_height    = dac_jack_body_height; // the jack is the tallest thing on top
dac_bottom_component_height = 0; // confirmed: nothing on the back

// ---- XIAO ESP32S3 ----
xiao_width              = 21.5; // X extent (both header rows run this way)
xiao_length             = 18;   // Y extent: north edge (facing the DAC) to south edge
xiao_pcb_thickness      = 1.2;
xiao_usbc_width         = 9;    // on the east short edge
xiao_usbc_height        = 3.2;  // above PCB top (total stack incl. PCB measured 4.5mm, consistent)
xiao_usbc_protrusion    = 1.5;  // past the east edge
// Assumed centred on the east edge — not confirmed.
xiao_usbc_y             = xiao_length / 2;
xiao_top_component_height    = xiao_usbc_height; // the USB-C shell is the tallest thing on top
xiao_bottom_component_height = 0; // confirmed: nothing on the back

// ---- Assembly ----
board_gap = 3; // MEASUREMENTS.md gives 2-5mm with no fixed value; nominal pick

dac_y0  = 0;                      // DAC's north (jack) edge
xiao_y0 = dac_length + board_gap; // XIAO's north (header) edge

assembly_width  = max(dac_width, xiao_width);
assembly_length = xiao_y0 + xiao_length;

// Centring offsets so each board's own local X=0..width sits on the
// shared centreline. This is the unconfirmed assumption noted above.
dac_x0  = (assembly_width - dac_width) / 2;
xiao_x0 = (assembly_width - xiao_width) / 2;

// ---- Power ----
// Two options on the table — see MEASUREMENTS.md decision 5:
//   true  - a panel-mount USB-C jack on a 2-wire feed to VUSB/GND;
//           the XIAO's own USB-C stays inside, unused.
//   false - plug straight into the XIAO's own USB-C, which then needs
//           its own cutout on the east wall instead.
use_external_power_jack = true;

power_jack_cutout_width  = 14;  // rectangular panel cutout
power_jack_cutout_height = 5.5;
// Placed on the east wall near the XIAO's header row, for the shortest
// wire run to VUSB/GND. That's >=90 degrees from the north-facing
// audio jack, satisfying the "keep power away from audio" constraint.
// It is a flying-lead part, not fixed to a board edge, so this is a
// starting point, not a hard requirement.
power_jack_x = xiao_x0 + xiao_width; // level with the XIAO's east edge
power_jack_y = xiao_y0 + 4;          // a few mm south of the header row
power_jack_z = 8;                    // above the floor; a guess, confirm against the jack's own body

// ---- Antenna ----
antenna_sheet_width    = 18;
antenna_sheet_length   = 38;
antenna_pigtail_length = 55; // XIAO pad to the sheet, landing mid-sheet's long edge
// The pad itself is near the XIAO's south-west corner per the assembly
// photo — worth confirming exactly before routing the glue point.
