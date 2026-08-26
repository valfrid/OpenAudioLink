#include "oal_input.h"

#include <string.h>

const char *oal_input_name(oal_input_t input)
{
    switch (input) {
    case OAL_INPUT_LINE: return "line";
    case OAL_INPUT_MIC:  return "mic";
    default:             return "line";
    }
}

bool oal_input_parse(const char *name, oal_input_t *out)
{
    if (name == NULL || out == NULL) {
        return false;
    }
    static const oal_input_t all[] = { OAL_INPUT_LINE, OAL_INPUT_MIC };
    for (size_t i = 0; i < sizeof(all) / sizeof(all[0]); i++) {
        if (strcmp(name, oal_input_name(all[i])) == 0) {
            *out = all[i];
            return true;
        }
    }
    return false;
}

const char *oal_input_describe(oal_input_t input)
{
    switch (input) {
    case OAL_INPUT_MIC:
        return "Microphone — an ICS-43434 for room measurement. This end clocks it.";
    case OAL_INPUT_LINE:
    default:
        return "Line in — a PCM1808 ADC for a turntable or CD. The module clocks itself.";
    }
}

bool oal_input_is_clock_master(oal_input_t input)
{
    /*
     * Only the microphone. The self-clocked PCM1808 module carries a
     * 24.576 MHz oscillator and drives BCK and LRCK itself, so asserting
     * them from this end would put two drivers on one wire — which is the
     * reason the two inputs get separate pins rather than a shared bus.
     *
     * A *bare* PCM1808, with no oscillator, would want this end to be
     * master too. That board is not what this project was built against
     * (`HARDWARE.md`), and `OAL_ADC_SLAVE` remains the switch for it: this
     * function answers for the input *kind*, and the ADC's own strapping
     * stays where it was.
     */
    return input == OAL_INPUT_MIC;
}
