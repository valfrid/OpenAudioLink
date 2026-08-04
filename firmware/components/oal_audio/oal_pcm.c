#include "oal_pcm.h"

#define SIGN_BIT 0x800000u
#define WRAP     0x1000000

int32_t oal_pcm_read_l24(const uint8_t *bytes)
{
    uint32_t value = ((uint32_t)bytes[0] << 16) | ((uint32_t)bytes[1] << 8) | bytes[2];

    /*
     * Sign-extended by subtraction rather than by shifting. Shifting a
     * negative signed value is undefined behaviour, and the compiler is
     * entitled to do something surprising with it; this is the same idiom
     * oal_channel.c uses, for the same reason.
     */
    return (value & SIGN_BIT) ? (int32_t)value - WRAP : (int32_t)value;
}

void oal_pcm_l24_to_i2s(const uint8_t *payload, int32_t *out, size_t samples)
{
    if (payload == NULL || out == NULL) {
        return;
    }

    for (size_t i = 0; i < samples; i++) {
        /*
         * Multiplied by 256 rather than shifted left by 8, again because
         * the value can be negative. The result is exact and cannot
         * overflow: the widest input, -8388608, maps to exactly INT32_MIN.
         */
        out[i] = oal_pcm_read_l24(payload + i * 3) * 256;
    }
}
