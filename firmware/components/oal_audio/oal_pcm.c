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

int32_t oal_pcm_peak(const int32_t *samples, size_t count, unsigned channel)
{
    if (samples == NULL || channel >= OAL_PCM_CHANNELS) {
        return 0;
    }

    int32_t peak = 0;
    for (size_t i = channel; i < count; i += OAL_PCM_CHANNELS) {
        /*
         * Negated rather than abs()ed. INT32_MIN has no positive
         * counterpart, so abs() on it is undefined — and INT32_MIN is
         * exactly what a full-scale negative sample becomes here, which
         * makes it the value most likely to appear rather than least.
         */
        int32_t value = samples[i];
        if (value == INT32_MIN) {
            return INT32_MAX;
        }
        if (value < 0) {
            value = -value;
        }
        if (value > peak) {
            peak = value;
        }
    }
    return peak;
}

int oal_pcm_dbfs(int32_t peak)
{
    if (peak <= 0) {
        return OAL_DBFS_SILENT;
    }

    /*
     * 20*log10(peak / 2^31), by repeated halving rather than logf.
     *
     * Six decibels per halving gets within 3 dB, and a small table
     * corrects the remainder. That is coarse, deliberately: this is read
     * as "silent", "quiet", "about right" or "clipping", and a tenth of a
     * decibel would be false precision on a measurement taken over a few
     * hundred milliseconds of a record.
     *
     * Integer throughout so it behaves identically on the node and in a
     * host test, and so nothing in the capture path needs an FPU — the
     * task computing this has a hardware deadline every 5 ms.
     */
    /* 20*log10((16+i)/32) for i in 0..15, rounded: where inside the
     * doubling the value fell, once it has been normalised into
     * [2^30, 2^31). */
    static const int OCTAVE_DB[16] = {
        -6, -5, -5, -5, -4, -4, -3, -3, -2, -2, -2, -1, -1, -1, -1, 0,
    };

    int db = 0;
    uint32_t value = (uint32_t)peak;

    /* Double until the value sits in the top octave. Each doubling means
     * the original was another 6 dB below it. */
    while (value < 0x40000000u) {
        value <<= 1;
        db -= 6;
    }

    db += OCTAVE_DB[(value - 0x40000000u) / 0x04000000u];

    return db < OAL_DBFS_SILENT ? OAL_DBFS_SILENT : db;
}

/*
 * Q16 multipliers for 0..OAL_BOOST_DB_MAX decibels, rounded from
 * 65536 * 10^(dB/20). Precomputed because the audio path must not call
 * pow() and because a table is checkable by eye: entry 6 is a shade under
 * double (6.02 dB is exactly double), entry 20 is ten times, entry 40 is
 * a hundred.
 */
static const int32_t BOOST_Q16[OAL_BOOST_DB_MAX + 1] = {
    65536, 73533, 82505, 92572,
    103868, 116541, 130762, 146717,
    164619, 184706, 207243, 232531,
    260904, 292739, 328458, 368536,
    413504, 463959, 520571, 584090,
    655360, 735326, 825049, 925721,
    1038676, 1165413, 1307615, 1467168,
    1646190, 1847055, 2072430, 2325305,
    2609035, 2927386, 3284581, 3685360,
    4135042, 4639593, 5205710, 5840902,
    6553600, 7353260, 8250494, 9257206,
    10386756, 11654132, 13076151, 14671683,
    16461899, 18470554, 20724303, 23253050,
    26090352, 29273856, 32845807, 36853601,
    41350421, 46395935, 52057095, 58409021,
    65536000,
};

int32_t oal_pcm_boost_q16(uint8_t db)
{
    if (db > OAL_BOOST_DB_MAX) {
        db = OAL_BOOST_DB_MAX;
    }
    return BOOST_Q16[db];
}

void oal_pcm_boost_l24(uint8_t *payload, size_t samples, int32_t gain_q16)
{
    /* Unity is the common case -- every node that is not a microphone --
     * and skipping it costs nothing rather than multiplying by one. */
    if (payload == NULL || gain_q16 <= OAL_GAIN_UNITY) {
        return;
    }

    for (size_t i = 0; i < samples; i++) {
        uint8_t *at = payload + i * 3u;
        /*
         * 64-bit intermediate, then saturate. A 24-bit sample near full
         * scale times a hundred overflows 32 bits several times over, and
         * a wrapped sample is not a loud sample -- it is the sign
         * inverting mid-waveform, which sounds like tearing rather than
         * like clipping.
         */
        int64_t scaled = ((int64_t)oal_pcm_read_l24(at) * gain_q16) / OAL_GAIN_UNITY;
        if (scaled > 0x7FFFFF) {
            scaled = 0x7FFFFF;
        } else if (scaled < -0x800000) {
            scaled = -0x800000;
        }
        oal_pcm_write_l24((int32_t)scaled, at);
    }
}
