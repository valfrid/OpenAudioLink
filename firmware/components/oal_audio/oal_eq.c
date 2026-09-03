#include "oal_eq.h"

#include <ctype.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Not M_PI: a POSIX extension, absent under a strict C11 dialect, and this
 * file is compiled both by the host test build and ESP-IDF. */
#define OAL_EQ_PI 3.14159265358979323846f

static float clampf(float value, float low, float high)
{
    if (value < low) {
        return low;
    }
    if (value > high) {
        return high;
    }
    return value;
}

/**
 * One number, ending at '/' or whitespace.
 *
 * strtof rather than sscanf so the cursor can be advanced exactly: a
 * vector is a sequence and the parser has to know where each number
 * stopped, not merely that one was found.
 */
static bool take_number(const char **at, float *out)
{
    while (**at != '\0' && (isspace((unsigned char)**at) || **at == '/')) {
        (*at)++;
    }
    if (**at == '\0') {
        return false;
    }

    char *end = NULL;
    float value = strtof(*at, &end);
    if (end == *at || !isfinite(value)) {
        return false;
    }
    *at = end;
    return (*out = value, true);
}

bool oal_eq_parse(const char *text, oal_eq_curve_t *out)
{
    if (out == NULL) {
        return false;
    }

    oal_eq_curve_t parsed = { 0 };
    if (text == NULL) {
        *out = parsed;
        return true;
    }

    const char *at = text;
    while (true) {
        while (*at != '\0' && isspace((unsigned char)*at)) {
            at++;
        }
        if (*at == '\0') {
            break;
        }

        if (parsed.count >= OAL_EQ_MAX_BANDS) {
            /* More bands than the chain can run. Refused rather than
             * truncated: a vector that is quietly half applied is a
             * loudspeaker doing something nobody asked for. */
            return false;
        }

        float hz, q, gain;
        if (!take_number(&at, &hz) || !take_number(&at, &q) || !take_number(&at, &gain)) {
            return false;
        }

        /*
         * Clamped, not rejected. A Q of 25 is a typing slip worth
         * correcting; dropping every band after it would be a worse
         * answer than fixing it, and refusing the lot would lose a
         * correction because of one character.
         */
        parsed.bands[parsed.count].hz = clampf(hz, OAL_EQ_MIN_HZ, OAL_EQ_MAX_HZ);
        parsed.bands[parsed.count].q = clampf(q, OAL_EQ_MIN_Q, OAL_EQ_MAX_Q);
        parsed.bands[parsed.count].gain_db =
            clampf(gain, -OAL_EQ_MAX_GAIN_DB, OAL_EQ_MAX_GAIN_DB);
        parsed.count++;
    }

    *out = parsed;
    return true;
}

int oal_eq_format(const oal_eq_curve_t *curve, char *out, size_t size)
{
    if (curve == NULL || out == NULL || size == 0) {
        return -1;
    }

    size_t written = 0;
    out[0] = '\0';

    for (uint8_t i = 0; i < curve->count && i < OAL_EQ_MAX_BANDS; i++) {
        int wrote = snprintf(out + written, size - written, "%s%.1f/%.2f/%.1f",
                             i == 0 ? "" : " ",
                             (double)curve->bands[i].hz,
                             (double)curve->bands[i].q,
                             (double)curve->bands[i].gain_db);
        if (wrote < 0 || (size_t)wrote >= size - written) {
            out[0] = '\0';
            return -1;
        }
        written += (size_t)wrote;
    }
    return (int)written;
}

void oal_eq_chain_build(oal_eq_chain_t *chain, const oal_eq_curve_t *curve, int sample_rate)
{
    if (chain == NULL) {
        return;
    }

    memset(chain, 0, sizeof(*chain));
    if (curve == NULL || sample_rate <= 0) {
        return;
    }

    for (uint8_t i = 0; i < curve->count && i < OAL_EQ_MAX_BANDS; i++) {
        float hz = clampf(curve->bands[i].hz, OAL_EQ_MIN_HZ, (float)sample_rate * 0.45f);
        float q = clampf(curve->bands[i].q, OAL_EQ_MIN_Q, OAL_EQ_MAX_Q);
        float gain = clampf(curve->bands[i].gain_db, -OAL_EQ_MAX_GAIN_DB, OAL_EQ_MAX_GAIN_DB);

        /* A band that does nothing still costs a multiply per sample, and
         * a chain of them costs eight. Dropped. */
        if (gain > -0.05f && gain < 0.05f) {
            continue;
        }

        float a = powf(10.0f, gain / 40.0f);
        float w = 2.0f * OAL_EQ_PI * hz / (float)sample_rate;
        float alpha = sinf(w) / (2.0f * q);
        float cosw = cosf(w);

        float b0 = 1.0f + alpha * a;
        float b1 = -2.0f * cosw;
        float b2 = 1.0f - alpha * a;
        float a0 = 1.0f + alpha / a;
        float a1 = -2.0f * cosw;
        float a2 = 1.0f - alpha / a;

        oal_eq_section_t *section = &chain->sections[chain->count++];
        section->b0 = b0 / a0;
        section->b1 = b1 / a0;
        section->b2 = b2 / a0;
        section->a1 = a1 / a0;
        section->a2 = a2 / a0;
        section->z1 = 0.0f;
        section->z2 = 0.0f;
    }
}

void oal_eq_chain_reset(oal_eq_chain_t *chain)
{
    if (chain == NULL) {
        return;
    }
    for (uint8_t i = 0; i < chain->count; i++) {
        chain->sections[i].z1 = 0.0f;
        chain->sections[i].z2 = 0.0f;
    }
}

bool oal_eq_chain_active(const oal_eq_chain_t *chain)
{
    return chain != NULL && chain->count > 0;
}

void oal_eq_chain_run(
    oal_eq_chain_t *chain, int32_t *samples, size_t frames, size_t stride, float gain)
{
    if (chain == NULL || samples == NULL || chain->count == 0 || stride == 0) {
        return;
    }
    /* Attenuation only. A gain above one here is not headroom, and there is
     * a volume control for the other direction. */
    if (!(gain > 0.0f) || gain > 1.0f) {
        gain = 1.0f;
    }

    for (size_t frame = 0; frame < frames; frame++) {
        float x = (float)samples[frame * stride];

        for (uint8_t i = 0; i < chain->count; i++) {
            oal_eq_section_t *s = &chain->sections[i];

            /*
             * Transposed direct form II.
             *
             * Not the textbook direct form I, and the reason is the band
             * this filter exists for. A 30 Hz section at 48 kHz has its
             * poles a thousandth of the way from the unit circle, where
             * single precision runs out of resolution — and the ESP32-S3's
             * floating point unit is single precision only, so double is
             * software emulation and far too slow for eight sections at
             * two hundred thousand samples a second. Of the float forms
             * this one holds up best down there, and test_eq.c measures
             * exactly how well against a double reference rather than
             * taking that on trust.
             */
            float y = s->b0 * x + s->z1;
            s->z1 = s->b1 * x - s->a1 * y + s->z2;
            s->z2 = s->b2 * x - s->a2 * y;
            x = y;
        }

        /*
         * The headroom, applied before the value becomes an int32.
         *
         * After the write it would be scaling a sample whose peaks have
         * already been clipped off, which is the whole failure it exists to
         * prevent -- paid for in loudness, buying nothing.
         */
        x *= gain;

        /* Saturate. A correction that overflows should sound loud rather
         * than inverted, which is what wrapping sounds like. */
        if (x >= 2147483520.0f) {
            samples[frame * stride] = INT32_MAX;
        } else if (x <= -2147483648.0f) {
            samples[frame * stride] = INT32_MIN;
        } else {
            samples[frame * stride] = (int32_t)x;
        }
    }
}
