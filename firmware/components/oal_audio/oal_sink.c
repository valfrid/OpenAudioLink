#include "oal_sink.h"

const oal_sink_t *oal_sink_for(oal_output_t output)
{
    /*
     * One image, both stages. The alternative — a build flag — was the
     * thing this was chosen over: a house with a DAC node and a dongle
     * node would need two firmware images, two OTA channels and a way to
     * remember which board got which, and getting that wrong bricks a
     * speaker in a way nobody can see from the Hub.
     *
     * The cost is the USB host stack linked into every node whether or not
     * it hosts anything. That is flash, which the 8 MB parts have, against
     * an operational mistake, which nobody has.
     */
    switch (output) {
    case OAL_OUTPUT_USB: return oal_sink_usb();
    case OAL_OUTPUT_I2S: return oal_sink_i2s();
    default:             return NULL;
    }
}
