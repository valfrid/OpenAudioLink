#pragma once

#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * How a Consumer's audio leaves the board.
 *
 * Decision 8 says this is a hardware-profile property rather than an
 * architectural one: a Consumer is defined by receiving RTP, buffering it,
 * correcting drift and playing it, and the last step's wiring belongs to
 * the board. `docs/USB-AUDIO.md` turned that from a claim into a second
 * output stage, so this is the setting that picks between them.
 *
 * It sits beside the channel profile and the volume in NVS, and for the
 * same reason: all three answer "what is this particular box", not "what is
 * playing". A node is soldered to a DAC or has a dongle pushed into it, and
 * that fact should survive a reboot, a rename and a firmware update.
 *
 * Free of ESP-IDF headers so it can be tested on a host, matching
 * `oal_channel_t`. There is little arithmetic here, but the parser is
 * reachable from the provisioning form and the control API, and a setting
 * that silently falls back to the wrong output stage is a node that plays
 * nothing with no error anywhere.
 */

typedef enum {
    /**
     * I²S to a PCM5102A or MAX98357A. The reference receiver in
     * `docs/HARDWARE.md`, and the only thing that existed before this enum.
     */
    OAL_OUTPUT_I2S = 0,

    /**
     * USB Audio Class 2.0 to a dongle the node hosts, `docs/USB-AUDIO.md`.
     * Costs the node its USB console — the ESP32-S3 shares one PHY between
     * USB-Serial/JTAG and USB-OTG — which is why this is a deliberate
     * setting and not something detected at boot.
     */
    OAL_OUTPUT_USB,
} oal_output_t;

/**
 * Unconfigured nodes use I²S.
 *
 * Not a preference so much as a promise: every node already deployed was
 * configured before this setting existed, reads nothing from NVS, and must
 * keep playing through the DAC it is wired to. A default of USB would take
 * the house silent on the next update.
 */
#define OAL_OUTPUT_DEFAULT OAL_OUTPUT_I2S

/** Wire name: "i2s" or "usb". */
const char *oal_output_name(oal_output_t output);

/**
 * Parses a wire name. Returns false for anything unrecognised rather than
 * falling back to a default, so a typo is refused instead of quietly
 * sending audio to a stage the board does not have.
 */
bool oal_output_parse(const char *name, oal_output_t *out);

/** A sentence for the provisioning form and the setup page. */
const char *oal_output_describe(oal_output_t output);

#ifdef __cplusplus
}
#endif
