#include "oal_output.h"

#include <string.h>

const char *oal_output_name(oal_output_t output)
{
    switch (output) {
    case OAL_OUTPUT_I2S: return "i2s";
    case OAL_OUTPUT_USB: return "usb";
    default:             return "i2s";
    }
}

bool oal_output_parse(const char *name, oal_output_t *out)
{
    if (name == NULL || out == NULL) {
        return false;
    }
    static const oal_output_t all[] = { OAL_OUTPUT_I2S, OAL_OUTPUT_USB };
    for (size_t i = 0; i < sizeof(all) / sizeof(all[0]); i++) {
        if (strcmp(name, oal_output_name(all[i])) == 0) {
            *out = all[i];
            return true;
        }
    }
    return false;
}

const char *oal_output_describe(oal_output_t output)
{
    switch (output) {
    case OAL_OUTPUT_USB:
        return "USB dongle — the node hosts a USB DAC. Costs the USB console.";
    case OAL_OUTPUT_I2S:
    default:
        return "I2S DAC — PCM5102A or MAX98357A wired to the board.";
    }
}
