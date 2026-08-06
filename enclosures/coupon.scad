// Fit-test coupon for the speaker-dongle receiver.
//
// Not a full shell: a floor to glue the two boards to, with a short
// wall standing up at each connector edge just tall enough to carry
// that connector's hole. This is the cheap, fast print that answers
// "is the hole in the right place and the right size" before spending
// hours on a full enclosure — see enclosures/README.md.
//
// "No walls" in that README means no full enclosure, not literally
// zero material at the panel edges: a cutout needs something to be cut
// out of. If a different kind of test piece was intended, say so.
//
// Retention is glue, not standoffs (MEASUREMENTS.md: both boards
// confirmed to have no mounting holes) — the recessed outlines below
// are placement guides for that glue, not structural.

include <boards.scad>

floor_thickness = 3;
wall_height     = 14;   // tall enough to clear both connectors with margin; trim once confirmed
wall_thickness  = 2.4;
overlap         = 0.2;  // small extra so walls and floor fuse instead of touching at a bare face

footprint_clearance = 0.3; // per side, so a real board drops into its outline

hole_clearance = 0.3; // per side, added to the audio jack's diameter and to the rectangular cutouts

module board_outline(x0, y0, width, length, depth) {
    translate([x0 - footprint_clearance, y0 - footprint_clearance, floor_thickness - depth])
        cube([width + 2 * footprint_clearance, length + 2 * footprint_clearance, depth + 0.1]);
}

module floor() {
    difference() {
        cube([assembly_width, assembly_length, floor_thickness]);
        board_outline(dac_x0, dac_y0, dac_width, dac_length, 0.3);
        board_outline(xiao_x0, xiao_y0, xiao_width, xiao_length, 0.3);
    }
}

// North wall: carries the DAC's line-out jack hole.
module north_wall() {
    difference() {
        translate([0, -wall_thickness, 0])
            cube([assembly_width, wall_thickness + overlap, wall_height]);

        // rotate([-90,0,0]) turns the cylinder to run along Y; the
        // translate's Y component is where that run starts, so it has
        // to begin outside the wall's outer (north) face to cut all
        // the way through to the inner face at y=0.
        translate([dac_x0 + dac_width / 2, -wall_thickness - 0.1, floor_thickness + dac_pcb_thickness + dac_jack_barrel_height])
            rotate([-90, 0, 0])
            cylinder(h = wall_thickness + 0.2, d = dac_jack_diameter + hole_clearance, $fn = 48);
    }
}

// East wall: carries either the XIAO's own USB-C cutout, or the
// external power jack cutout, depending on use_external_power_jack in
// boards.scad.
module east_wall() {
    difference() {
        translate([assembly_width - overlap, 0, 0])
            cube([wall_thickness + overlap, assembly_length, wall_height]);

        // rotate([0,-90,0]) turns the cube to run along X; the
        // translate's X component ends up as the FAR (outer) face
        // after rotation, so it has to sit past the wall's outer face
        // for the cut to reach all the way back to the inner face.
        if (use_external_power_jack) {
            translate([assembly_width + wall_thickness + 0.1, power_jack_y - (power_jack_cutout_width + hole_clearance) / 2, power_jack_z - (power_jack_cutout_height + hole_clearance) / 2])
                rotate([0, -90, 0])
                cube([power_jack_cutout_height + hole_clearance, power_jack_cutout_width + hole_clearance, wall_thickness + 0.2]);
        } else {
            translate([assembly_width + wall_thickness + 0.1, xiao_y0 + xiao_usbc_y - (xiao_usbc_width + hole_clearance) / 2, floor_thickness + xiao_pcb_thickness - hole_clearance / 2])
                rotate([0, -90, 0])
                cube([xiao_usbc_height + hole_clearance, xiao_usbc_width + hole_clearance, wall_thickness + 0.2]);
        }
    }
}

union() {
    floor();
    north_wall();
    east_wall();
}
