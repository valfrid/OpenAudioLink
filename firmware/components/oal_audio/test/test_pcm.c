/*
 * Host tests for the wire-to-I²S sample conversion.
 *
 * This is the last arithmetic between a correct packet and a sound, and
 * both ways of getting it wrong are loud. Byte order reversed is static;
 * justification wrong is quiet distortion; sign extension wrong turns
 * every negative sample positive, which is a savage kind of clipping.
 * None of it is diagnosable through a speaker, so it is checked here.
 */

#include "oal_pcm.h"

#include <limits.h>
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

static int failures;
static const char *current_test;

#define CHECK(expr)                                                        \
    do {                                                                   \
        if (!(expr)) {                                                     \
            printf("  FAIL %s:%d: %s\n", current_test, __LINE__, #expr);   \
            failures++;                                                    \
        }                                                                  \
    } while (0)

#define CHECK_EQ(actual, expected)                                              \
    do {                                                                        \
        long long a_ = (long long)(actual);                                     \
        long long e_ = (long long)(expected);                                   \
        if (a_ != e_) {                                                         \
            printf("  FAIL %s:%d: %s was %lld, expected %lld\n",                \
                   current_test, __LINE__, #actual, a_, e_);                    \
            failures++;                                                         \
        }                                                                       \
    } while (0)

#define TEST(name) current_test = name; printf("%s\n", name);

static void reads_the_extremes(void)
{
    TEST("reads full scale, silence and the ends");

    const uint8_t zero[3]     = { 0x00, 0x00, 0x00 };
    const uint8_t plus_max[3] = { 0x7F, 0xFF, 0xFF };
    const uint8_t minus_one[3]= { 0xFF, 0xFF, 0xFF };
    const uint8_t minus_max[3]= { 0x80, 0x00, 0x00 };

    CHECK_EQ(oal_pcm_read_l24(zero), 0);
    CHECK_EQ(oal_pcm_read_l24(plus_max), 8388607);
    CHECK_EQ(oal_pcm_read_l24(minus_one), -1);
    CHECK_EQ(oal_pcm_read_l24(minus_max), -8388608);
}

/*
 * The whole reason this file exists. 0x123456 read the other way round is
 * 0x563412 — a completely different sample, and the difference between
 * music and noise.
 */
static void byte_order_is_big_endian(void)
{
    TEST("byte order is big-endian, most significant byte first");

    const uint8_t bytes[3] = { 0x12, 0x34, 0x56 };
    CHECK_EQ(oal_pcm_read_l24(bytes), 0x123456);

    const uint8_t swapped[3] = { 0x56, 0x34, 0x12 };
    CHECK(oal_pcm_read_l24(bytes) != oal_pcm_read_l24(swapped));
}

/*
 * Every value with the top bit set is negative. Reading it unsigned is the
 * loudest bug available here: the negative half of every waveform folds up
 * to full positive scale.
 */
static void the_top_bit_means_negative(void)
{
    TEST("the top bit means negative");

    for (uint32_t value = 0x800000u; value < 0x1000000u; value += 0x10000u) {
        uint8_t bytes[3] = {
            (uint8_t)(value >> 16), (uint8_t)(value >> 8), (uint8_t)value,
        };
        CHECK(oal_pcm_read_l24(bytes) < 0);
    }

    for (uint32_t value = 0; value < 0x800000u; value += 0x10000u) {
        uint8_t bytes[3] = {
            (uint8_t)(value >> 16), (uint8_t)(value >> 8), (uint8_t)value,
        };
        CHECK(oal_pcm_read_l24(bytes) >= 0);
    }
}

/*
 * The DAC takes the first 24 bits of each 32-bit slot, so the sample has
 * to sit at the top. Right-justified instead, the audio is 48 dB down and
 * distorted, which sounds like a hardware fault rather than a software one.
 */
static void samples_are_left_justified(void)
{
    TEST("samples are left justified into 32 bits");

    const uint8_t payload[6] = { 0x7F, 0xFF, 0xFF, 0x80, 0x00, 0x00 };
    int32_t out[2] = { 0 };

    oal_pcm_l24_to_i2s(payload, out, 2);

    CHECK_EQ(out[0], 8388607 * 256);
    CHECK_EQ(out[1], -8388608 * 256);

    /* The widest inputs land exactly on the ends of the 32-bit range,
     * which is what makes the multiply safe rather than merely untested. */
    CHECK_EQ(out[1], INT32_MIN);
    CHECK(out[0] > 2147483000);

    /* The low byte is always clear: 24 bits of data, 8 of padding. */
    CHECK_EQ(out[0] & 0xFF, 0);
    CHECK_EQ(out[1] & 0xFF, 0);
}

/*
 * Interleaving is left, right, left, right. Getting the stride wrong swaps
 * the channels, which decision 10's mono profiles would then hide — a
 * single speaker sounds fine either way, and only a stereo pair reveals it.
 */
static void interleaving_is_preserved(void)
{
    TEST("interleaving is preserved");

    const uint8_t payload[12] = {
        0x00, 0x00, 0x01,   /* left  1 */
        0x00, 0x00, 0x02,   /* right 2 */
        0x00, 0x00, 0x03,   /* left  3 */
        0x00, 0x00, 0x04,   /* right 4 */
    };
    int32_t out[4] = { 0 };

    oal_pcm_l24_to_i2s(payload, out, 4);

    CHECK_EQ(out[0], 1 * 256);
    CHECK_EQ(out[1], 2 * 256);
    CHECK_EQ(out[2], 3 * 256);
    CHECK_EQ(out[3], 4 * 256);
}

static void a_whole_packet_converts(void)
{
    TEST("a whole packet converts");

    /* 240 frames of stereo, the RTP profile's packet. */
    static uint8_t payload[240 * 2 * 3];
    static int32_t out[240 * 2];

    for (size_t i = 0; i < sizeof(payload); i++) {
        payload[i] = (uint8_t)(i * 7);
    }
    oal_pcm_l24_to_i2s(payload, out, 240 * 2);

    /* Spot-check the last sample rather than trusting the loop bound. */
    size_t last = 240 * 2 - 1;
    CHECK_EQ(out[last], oal_pcm_read_l24(payload + last * 3) * 256);
}

static void null_arguments_are_survivable(void)
{
    TEST("null arguments are survivable");

    int32_t out[2] = { 5, 6 };
    oal_pcm_l24_to_i2s(NULL, out, 2);
    CHECK_EQ(out[0], 5);

    const uint8_t payload[6] = { 0 };
    oal_pcm_l24_to_i2s(payload, NULL, 2);

    oal_pcm_l24_to_i2s(payload, out, 0);
    CHECK_EQ(out[0], 5);
}

/*
 * The capture direction. Everything above tests the wire becoming audio;
 * these test audio becoming the wire, which the Analog Source needs and
 * which fails in exactly the same silent ways.
 */
static void a_sample_survives_the_round_trip(void)
{
    TEST("a sample survives the round trip");

    /* Both extremes, zero, and values that exercise every byte. */
    const int32_t values[] = {
        0, 1, -1, 127, -128, 32767, -32768, 8388607, -8388608, 4660, -4660,
    };

    for (size_t i = 0; i < sizeof(values) / sizeof(values[0]); i++) {
        uint8_t bytes[3];
        oal_pcm_write_l24(values[i], bytes);
        CHECK_EQ(oal_pcm_read_l24(bytes), values[i]);
    }
}

static void writing_is_big_endian_too(void)
{
    TEST("writing is big endian too");

    uint8_t bytes[3];
    oal_pcm_write_l24(0x123456, bytes);
    CHECK_EQ(bytes[0], 0x12);
    CHECK_EQ(bytes[1], 0x34);
    CHECK_EQ(bytes[2], 0x56);

    /* -1 is all ones in two's complement, so every byte is 0xFF. */
    oal_pcm_write_l24(-1, bytes);
    CHECK_EQ(bytes[0], 0xFF);
    CHECK_EQ(bytes[1], 0xFF);
    CHECK_EQ(bytes[2], 0xFF);
}

static void loud_samples_clamp_rather_than_wrap(void)
{
    TEST("loud samples clamp rather than wrap");

    uint8_t bytes[3];

    oal_pcm_write_l24(8388608, bytes);
    CHECK_EQ(oal_pcm_read_l24(bytes), 8388607);

    oal_pcm_write_l24(-8388609, bytes);
    CHECK_EQ(oal_pcm_read_l24(bytes), -8388608);

    /* The failure this guards against: a wrap would turn full scale into
     * full scale of the opposite sign, which clicks on every peak. */
    oal_pcm_write_l24(INT32_MAX, bytes);
    CHECK(oal_pcm_read_l24(bytes) > 0);
    oal_pcm_write_l24(INT32_MIN, bytes);
    CHECK(oal_pcm_read_l24(bytes) < 0);
}

static void capture_undoes_playback(void)
{
    TEST("capture undoes playback");

    /* One frame of each extreme plus something asymmetric, through the
     * playback conversion and back, which is what a loopback test on real
     * hardware would prove and this proves without one. */
    const uint8_t original[12] = {
        0x7F, 0xFF, 0xFF,   /* +full scale */
        0x80, 0x00, 0x00,   /* -full scale */
        0x00, 0x00, 0x00,   /* silence */
        0x12, 0x34, 0x56,   /* arbitrary */
    };

    int32_t words[4];
    oal_pcm_l24_to_i2s(original, words, 4);

    uint8_t back[12];
    oal_pcm_i2s_to_l24(words, back, 4);

    for (size_t i = 0; i < sizeof(original); i++) {
        CHECK_EQ(back[i], original[i]);
    }
}

static void a_low_byte_from_the_adc_is_discarded_not_wrapped(void)
{
    TEST("a low byte from the ADC is discarded, not wrapped");

    /* Real converters do not always leave the low byte clear. Whatever is
     * there must fall off the bottom, never carry into the sample. */
    int32_t words[2] = {
        (8388607 * 256) | 0xFF,
        (-8388608 * 256) | 0xFF,
    };

    uint8_t payload[6];
    oal_pcm_i2s_to_l24(words, payload, 2);

    CHECK_EQ(oal_pcm_read_l24(payload), 8388607);
    CHECK_EQ(oal_pcm_read_l24(payload + 3), -8388607);
}

static void capture_null_arguments_are_survivable(void)
{
    TEST("capture null arguments are survivable");

    uint8_t payload[6] = { 9, 9, 9, 9, 9, 9 };
    const int32_t words[2] = { 0, 0 };

    oal_pcm_i2s_to_l24(NULL, payload, 2);
    CHECK_EQ(payload[0], 9);

    oal_pcm_i2s_to_l24(words, NULL, 2);

    oal_pcm_i2s_to_l24(words, payload, 0);
    CHECK_EQ(payload[0], 9);

    oal_pcm_write_l24(0, NULL);
}

static void full_volume_is_exactly_untouched(void)
{
    TEST("full volume is exactly untouched");

    /* Not "close enough": a node at 100 must be bit-identical to a node
     * with no volume control at all, or every existing measurement of the
     * audio path stops being comparable to the next one. */
    CHECK_EQ(oal_pcm_gain_q16(100), OAL_GAIN_UNITY);
    CHECK_EQ(oal_pcm_gain_q16(255), OAL_GAIN_UNITY);

    int32_t samples[3] = { INT32_MAX, INT32_MIN, -12345678 };
    oal_pcm_apply_gain(samples, 3, oal_pcm_gain_q16(100));
    CHECK_EQ(samples[0], INT32_MAX);
    CHECK_EQ(samples[1], INT32_MIN);
    CHECK_EQ(samples[2], -12345678);
}

static void zero_is_silence_not_nearly_silence(void)
{
    TEST("zero is silence, not nearly silence");

    CHECK_EQ(oal_pcm_gain_q16(0), 0);

    int32_t samples[2] = { INT32_MAX, INT32_MIN };
    oal_pcm_apply_gain(samples, 2, 0);
    CHECK_EQ(samples[0], 0);
    CHECK_EQ(samples[1], 0);
}

static void the_taper_is_cubed(void)
{
    TEST("the taper is cubed");

    /* Half travel at about -18 dB, a tenth at -60: where the detents on a
     * real volume pot are, and the whole reason this is not linear. */
    CHECK_EQ(oal_pcm_gain_q16(50), 8192);   /* 0.125    = -18.1 dB */
    CHECK_EQ(oal_pcm_gain_q16(10), 65);     /* 0.000992 = -60.1 dB */
    CHECK_EQ(oal_pcm_gain_q16(80), 33554);  /* 0.512    =  -5.8 dB */

    /* Monotonic the whole way up. The 32-bit overflow this arithmetic
     * invites would show as a gain that falls somewhere in the middle,
     * which through a speaker reads as a broken slider rather than as an
     * arithmetic fault. */
    int32_t previous = -1;
    for (int p = 0; p <= 100; p++) {
        int32_t gain = oal_pcm_gain_q16((uint8_t)p);
        CHECK(gain > previous || (p < 4 && gain == previous));
        CHECK(gain <= OAL_GAIN_UNITY);
        previous = gain;
    }
}

static void a_loud_sample_scales_without_overflowing(void)
{
    TEST("a loud sample scales without overflowing");

    /*
     * The case a 32-bit intermediate gets wrong. A full-scale sample times
     * any gain at all exceeds INT32_MAX before the division brings it
     * back, and the wrap turns the loudest sample into the quietest one of
     * the opposite sign — a click on every peak.
     */
    int32_t samples[2] = { INT32_MAX, INT32_MIN };
    oal_pcm_apply_gain(samples, 2, oal_pcm_gain_q16(50));

    CHECK(samples[0] > 0);
    CHECK(samples[1] < 0);
    CHECK_EQ(samples[0], (int32_t)(((int64_t)INT32_MAX * 8192) / 65536));
    CHECK_EQ(samples[1], (int32_t)(((int64_t)INT32_MIN * 8192) / 65536));
}

static void gain_null_arguments_are_survivable(void)
{
    TEST("gain null arguments are survivable");

    oal_pcm_apply_gain(NULL, 4, 8192);

    int32_t samples[1] = { 1000 };
    oal_pcm_apply_gain(samples, 0, 8192);
    CHECK_EQ(samples[0], 1000);
}

/*
 * The level meter. This is the only instrument that can tell a working
 * ADC from a connected one — everything else the capture path reports
 * counts frames, and frames arrive whether or not a cable does.
 */
static void the_meter_reads_full_scale_as_zero(void)
{
    TEST("the meter reads full scale as zero");

    CHECK_EQ(oal_pcm_dbfs(INT32_MAX), 0);

    /* A full-scale negative sample is INT32_MIN, which has no positive
     * counterpart — abs() on it is undefined, and it is the value most
     * likely to appear rather than least. */
    const int32_t loud[2] = { INT32_MIN, INT32_MIN };
    CHECK_EQ(oal_pcm_dbfs(oal_pcm_peak(loud, 2, 0)), 0);
    CHECK_EQ(oal_pcm_dbfs(oal_pcm_peak(loud, 2, 1)), 0);
}

static void silence_reads_as_silence(void)
{
    TEST("silence reads as silence");

    CHECK_EQ(oal_pcm_dbfs(0), OAL_DBFS_SILENT);
    CHECK_EQ(oal_pcm_dbfs(-1), OAL_DBFS_SILENT);

    const int32_t quiet[8] = { 0, 0, 0, 0, 0, 0, 0, 0 };
    CHECK_EQ(oal_pcm_peak(quiet, 8, 0), 0);
    CHECK_EQ(oal_pcm_dbfs(oal_pcm_peak(quiet, 8, 0)), OAL_DBFS_SILENT);
}

static void halving_the_signal_costs_six_decibels(void)
{
    TEST("halving the signal costs six decibels");

    CHECK_EQ(oal_pcm_dbfs(1 << 30), -6);
    CHECK_EQ(oal_pcm_dbfs(1 << 29), -12);
    CHECK_EQ(oal_pcm_dbfs(1 << 28), -18);
    CHECK_EQ(oal_pcm_dbfs(1 << 25), -36);

    /* A quiet passage of a record sits around here. The floor is a clamp,
     * not an overflow: anything below it is inaudible anyway. */
    CHECK_EQ(oal_pcm_dbfs(1), OAL_DBFS_SILENT);
}

/*
 * Against real logarithms rather than against itself. The table is small
 * enough to get subtly wrong, and a meter that is consistently three
 * decibels optimistic would send somebody looking for a wiring fault that
 * is not there.
 */
static void the_meter_agrees_with_the_arithmetic(void)
{
    TEST("the meter agrees with the arithmetic");

    for (int shift = 0; shift < 20; shift++) {
        for (int step = 0; step < 16; step++) {
            int64_t value = (int64_t)(0x40000000 + step * 0x04000000) >> shift;
            if (value <= 0) {
                continue;
            }
            double exact = 20.0 * log10((double)value / 2147483648.0);
            int reported = oal_pcm_dbfs((int32_t)value);
            /* Within a decibel of the truth, which is finer than the
             * question "is the turntable connected" can use. */
            CHECK(reported - exact < 1.0 && exact - reported < 1.0);
        }
    }
}

static void the_two_channels_are_measured_apart(void)
{
    TEST("the two channels are measured apart");

    /* A dead right channel: one lifted ground on an RCA, which is the
     * ordinary turntable failure and reads as "quieter than expected" if
     * both channels are folded into one number. */
    const int32_t one_sided[8] = {
        1 << 30, 0, -(1 << 30), 0, 1 << 29, 0, 1 << 30, 0,
    };

    CHECK_EQ(oal_pcm_dbfs(oal_pcm_peak(one_sided, 8, 0)), -6);
    CHECK_EQ(oal_pcm_dbfs(oal_pcm_peak(one_sided, 8, 1)), OAL_DBFS_SILENT);

    /* An out-of-range channel index must not read off the end. */
    CHECK_EQ(oal_pcm_peak(one_sided, 8, 2), 0);
    CHECK_EQ(oal_pcm_peak(NULL, 8, 0), 0);
}

/*
 * The boost is the microphone's, and it must saturate. A 24-bit sample
 * that wraps does not sound loud, it sounds torn -- the sign inverting
 * mid-waveform -- so this is the property worth pinning down.
 */
static void test_the_boost_amplifies_by_decibels(void)
{
    TEST("the boost amplifies by whole decibels");

    CHECK_EQ(oal_pcm_boost_q16(0), OAL_GAIN_UNITY);

    /* 6 dB is a shade *under* double -- 6.02 dB is exactly double -- and
     * getting that backwards is what this line is here to catch. 20 dB is
     * ten times, 40 dB a hundred, and those are exact. */
    CHECK(oal_pcm_boost_q16(6) < 2 * OAL_GAIN_UNITY);
    CHECK(oal_pcm_boost_q16(6) > 199 * OAL_GAIN_UNITY / 100);
    CHECK_EQ(oal_pcm_boost_q16(20), 10 * OAL_GAIN_UNITY);
    CHECK_EQ(oal_pcm_boost_q16(40), 100 * OAL_GAIN_UNITY);

    /* Monotonic, and clamped rather than read off the end. */
    for (uint8_t db = 1; db <= OAL_BOOST_DB_MAX; db++) {
        CHECK(oal_pcm_boost_q16(db) > oal_pcm_boost_q16((uint8_t)(db - 1)));
    }
    CHECK_EQ(oal_pcm_boost_q16(200), oal_pcm_boost_q16(OAL_BOOST_DB_MAX));
}

static void test_the_boost_saturates_rather_than_wrapping(void)
{
    TEST("the boost saturates rather than wrapping");

    uint8_t payload[3 * 4];
    oal_pcm_write_l24(0x400000, payload + 0);   /* half scale, positive */
    oal_pcm_write_l24(-0x400000, payload + 3);  /* half scale, negative */
    oal_pcm_write_l24(0x7FFFFF, payload + 6);   /* full scale */
    oal_pcm_write_l24(-0x800000, payload + 9);

    /* 20 dB on half scale is five times over: every one must peg. */
    oal_pcm_boost_l24(payload, 4, oal_pcm_boost_q16(20));

    CHECK_EQ(oal_pcm_read_l24(payload + 0), 0x7FFFFF);
    CHECK_EQ(oal_pcm_read_l24(payload + 3), -0x800000);
    CHECK_EQ(oal_pcm_read_l24(payload + 6), 0x7FFFFF);
    CHECK_EQ(oal_pcm_read_l24(payload + 9), -0x800000);
}

static void test_the_boost_scales_what_fits(void)
{
    TEST("the boost scales what fits");

    uint8_t payload[3 * 2];
    oal_pcm_write_l24(1000, payload + 0);
    oal_pcm_write_l24(-1000, payload + 3);

    oal_pcm_boost_l24(payload, 2, oal_pcm_boost_q16(20));

    CHECK_EQ(oal_pcm_read_l24(payload + 0), 10000);
    CHECK_EQ(oal_pcm_read_l24(payload + 3), -10000);
}

static void test_unity_and_below_leave_the_payload_alone(void)
{
    TEST("unity and below leave the payload alone");

    uint8_t payload[3];
    oal_pcm_write_l24(12345, payload);
    oal_pcm_boost_l24(payload, 1, oal_pcm_boost_q16(0));
    CHECK_EQ(oal_pcm_read_l24(payload), 12345);

    oal_pcm_boost_l24(payload, 1, OAL_GAIN_UNITY / 2);
    CHECK_EQ(oal_pcm_read_l24(payload), 12345);
}

int main(void)
{
    test_the_boost_amplifies_by_decibels();
    test_the_boost_saturates_rather_than_wrapping();
    test_the_boost_scales_what_fits();
    test_unity_and_below_leave_the_payload_alone();
    reads_the_extremes();
    byte_order_is_big_endian();
    the_top_bit_means_negative();
    samples_are_left_justified();
    interleaving_is_preserved();
    a_whole_packet_converts();
    null_arguments_are_survivable();
    a_sample_survives_the_round_trip();
    writing_is_big_endian_too();
    loud_samples_clamp_rather_than_wrap();
    capture_undoes_playback();
    a_low_byte_from_the_adc_is_discarded_not_wrapped();
    capture_null_arguments_are_survivable();
    full_volume_is_exactly_untouched();
    zero_is_silence_not_nearly_silence();
    the_taper_is_cubed();
    a_loud_sample_scales_without_overflowing();
    gain_null_arguments_are_survivable();
    the_meter_reads_full_scale_as_zero();
    silence_reads_as_silence();
    halving_the_signal_costs_six_decibels();
    the_meter_agrees_with_the_arithmetic();
    the_two_channels_are_measured_apart();

    if (failures > 0) {
        printf("\n%d check(s) failed\n", failures);
        return 1;
    }
    printf("\nall PCM conversion checks passed\n");
    return 0;
}
