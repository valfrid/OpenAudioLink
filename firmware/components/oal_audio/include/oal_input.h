#pragma once

#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * What a Producer captures from.
 *
 * The mirror of `oal_output_t`, and for the same reason: decision 8 makes
 * the wiring at the ends of the chain a hardware-profile property. A
 * Consumer is soldered to a DAC or has a dongle pushed in; a Producer has a
 * line-level ADC or a microphone. Both facts should survive a reboot, a
 * rename and a firmware update, so both live in NVS.
 *
 * It exists because one box is going to be both. A room measurement needs a
 * microphone at the listening position; a turntable needs a line input by
 * the record player; and neither justifies its own ESP32. They are never
 * used at once, so a setting read at boot is enough — the same shape as the
 * output stage, roles and the channel profile, and for the same reason: it
 * describes the box, not what is playing.
 *
 * **The two want the ESP in opposite roles, and that is why the pins must
 * not be shared.** The self-clocked PCM1808 module carries its own
 * oscillator, drives BCK and LRCK, and makes this end the slave. The
 * ICS-43434 is a plain I2S slave and needs BCK and WS supplied, making this
 * end the master. Put them on one pin and, in microphone mode, the ESP
 * drives clock lines the powered ADC board is also driving. `HARDWARE.md`
 * already records what that sounds like: "two masters driving one clock
 * line produce nothing usable, and the symptom is silence."
 *
 * Free of ESP-IDF headers so it can be tested on a host, matching
 * `oal_output_t` and `oal_channel_t`. The parser is reachable from the
 * control API, and a setting that silently falls back to the wrong input is
 * a node that captures nothing with no error anywhere.
 */

typedef enum {
    /**
     * Line level through an I2S ADC — the PCM1808 of `docs/HARDWARE.md`.
     * A turntable, a CD player, a TV. The only thing that existed before
     * this enum.
     */
    OAL_INPUT_LINE = 0,

    /**
     * An I2S MEMS microphone, the ICS-43434 of `docs/ROOM-CALIBRATION.md`.
     * Room measurement, and later whatever a stationary listening node
     * turns out to be for.
     *
     * Mono by nature: the part puts its samples in one half of the frame
     * and the SEL pin says which. The channel profile is what makes that a
     * stereo stream again (decision 10).
     */
    OAL_INPUT_MIC,
} oal_input_t;

/**
 * Unconfigured nodes capture from the line input.
 *
 * A promise rather than a preference, exactly as with the output stage:
 * every Producer already deployed was configured before this setting
 * existed, reads nothing from NVS, and must keep capturing from the ADC it
 * is wired to. A default of microphone would silence the turntable on the
 * next update.
 */
#define OAL_INPUT_DEFAULT OAL_INPUT_LINE

/** Wire name: "line" or "mic". */
const char *oal_input_name(oal_input_t input);

/**
 * Parses a wire name. Returns false for anything unrecognised rather than
 * guessing, because guessing here picks the wrong set of pins.
 */
bool oal_input_parse(const char *name, oal_input_t *out);

/** One line for a person choosing between them. */
const char *oal_input_describe(oal_input_t input);

/**
 * Whether this end must generate the bit and word clocks.
 *
 * The whole reason the two inputs cannot share pins, reduced to one
 * question. True for the microphone, which is a slave and needs clocking;
 * false for the self-clocked ADC module, which clocks itself and this end
 * as well.
 */
bool oal_input_is_clock_master(oal_input_t input);

#ifdef __cplusplus
}
#endif
