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

void oal_pcm_write_l24(int32_t value, uint8_t *bytes)
{
    if (bytes == NULL) {
        return;
    }

    if (value > 8388607) {
        value = 8388607;
    } else if (value < -8388608) {
        value = -8388608;
    }

    /*
     * Through unsigned before shifting, because shifting a negative value
     * right is implementation-defined. Two's complement makes the low 24
     * bits of the unsigned conversion exactly the bits the wire wants.
     */
    uint32_t bits = (uint32_t)value;
    bytes[0] = (uint8_t)((bits >> 16) & 0xFFu);
    bytes[1] = (uint8_t)((bits >> 8) & 0xFFu);
    bytes[2] = (uint8_t)(bits & 0xFFu);
}

void oal_pcm_i2s_to_l24(const int32_t *in, uint8_t *payload, size_t samples)
{
    if (in == NULL || payload == NULL) {
        return;
    }

    for (size_t i = 0; i < samples; i++) {
        /*
         * Divided by 256 rather than shifted right by 8. For a 24-bit ADC
         * in 32-bit slots the low byte is zero and the two agree exactly;
         * where it is not, division truncates towards zero and a shift
         * towards negative infinity, so they differ by one part in
         * sixteen million on negative samples. Division is chosen because
         * it is defined, and because it is the exact inverse of the
         * multiply on the playback side.
         */
        oal_pcm_write_l24(in[i] / 256, payload + i * 3);
    }
}

int32_t oal_pcm_gain_q16(uint8_t percent)
{
    if (percent >= 100) {
        return OAL_GAIN_UNITY;
    }
    if (percent == 0) {
        return 0;
    }

    /*
     * (percent / 100)^3 * 65536, without ever dividing before multiplying.
     * The widest intermediate is 100^3 * 65536, which is 6.5e10 and needs
     * the 64-bit type; doing it in 32 bits overflows at about 39 %, and
     * the symptom would be a volume control that gets louder as it is
     * turned down.
     */
    uint64_t cubed = (uint64_t)percent * percent * percent;
    return (int32_t)(cubed * OAL_GAIN_UNITY / 1000000u);
}

void oal_pcm_apply_gain(int32_t *samples, size_t count, int32_t gain_q16)
{
    if (samples == NULL || gain_q16 >= OAL_GAIN_UNITY) {
        return;
    }

    if (gain_q16 <= 0) {
        for (size_t i = 0; i < count; i++) {
            samples[i] = 0;
        }
        return;
    }

    for (size_t i = 0; i < count; i++) {
        /*
         * Divided by 65536 rather than shifted right by 16, for the reason
         * every other conversion in this file avoids shifting: the value
         * is signed and can be negative, where a right shift is
         * implementation-defined. The compiler turns a division by a power
         * of two into a shift and a correction anyway.
         *
         * The 64-bit intermediate is necessary and not cheap paranoia: a
         * full-scale sample here is close to INT32_MAX, and multiplying it
         * by anything at all overflows 32 bits.
         *
         * Truncation loses the bottom bits of every sample. At 24 bits
         * that is 20 dB below the quietest thing a domestic speaker
         * reproduces, so no dither: it would cost a random-number call per
         * sample to fix something nobody can hear.
         */
        samples[i] = (int32_t)(((int64_t)samples[i] * gain_q16) / OAL_GAIN_UNITY);
    }
}
