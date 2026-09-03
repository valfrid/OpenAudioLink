/*
 * Room-correction filters: the stored format, and whether the arithmetic
 * survives the bottom of the band.
 *
 * The second one is the reason this file exists. Everything else here is a
 * parser, and a parser fails loudly; a biquad that has run out of precision
 * does not fail at all, it just stops doing what the Hub predicted, and the
 * only way to notice is to measure the room again and wonder.
 */

#include "oal_eq.h"

#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

#define RATE 48000

/* Its own, rather than M_PI: that is a POSIX extension and absent under the
 * strict C11 dialect CI compiles these with. The same reason oal_rtp.c and
 * oal_eq.c each define one. */
#define PI 3.14159265358979323846

static int failures;

static void check(bool ok, const char *what)
{
    if (!ok) {
        printf("FAIL: %s\n", what);
        failures++;
    }
}

static void check_close(double got, double want, double tolerance, const char *what)
{
    if (fabs(got - want) > tolerance) {
        printf("FAIL: %s (got %.4f, wanted %.4f +- %.4f)\n", what, got, want, tolerance);
        failures++;
    }
}

/* ---------------------------------------------------------------- format */

static void test_parses_a_vector(void)
{
    oal_eq_curve_t curve;
    check(oal_eq_parse("104.0/3.78/-9.0 151.2/5.01/-4.8", &curve), "parses");
    check(curve.count == 2, "two bands");
    check_close(curve.bands[0].hz, 104.0, 0.01, "first frequency");
    check_close(curve.bands[0].q, 3.78, 0.01, "first Q");
    check_close(curve.bands[0].gain_db, -9.0, 0.01, "first gain");
    check_close(curve.bands[1].hz, 151.2, 0.01, "second frequency");
}

/* Somebody editing by hand types spaces around the slashes. */
static void test_forgives_a_persons_spacing(void)
{
    oal_eq_curve_t curve;
    check(oal_eq_parse("  104 / 3.8 / -9\n\t63/1.5/2  ", &curve), "parses spaced out");
    check(curve.count == 2, "still two bands");
    check_close(curve.bands[1].hz, 63.0, 0.01, "second frequency");
    check_close(curve.bands[1].gain_db, 2.0, 0.01, "second gain");
}

static void test_an_empty_vector_is_how_a_correction_is_cleared(void)
{
    oal_eq_curve_t curve;
    check(oal_eq_parse("", &curve) && curve.count == 0, "empty string");
    check(oal_eq_parse("   \n ", &curve) && curve.count == 0, "whitespace");
    check(oal_eq_parse(NULL, &curve) && curve.count == 0, "nothing at all");
}

/*
 * A slip is corrected rather than obeyed: the fence exists to stop a typing
 * mistake destroying a tweeter, and it is deliberately wider than anything
 * the Hub's fitter will ever produce, because hand tuning is the point of
 * this format.
 */
static void test_a_wild_band_is_brought_inside_the_fence(void)
{
    oal_eq_curve_t curve;
    check(oal_eq_parse("100/50/-40 5/1/60", &curve), "parses");
    check_close(curve.bands[0].q, OAL_EQ_MAX_Q, 0.001, "Q clamped");
    check_close(curve.bands[0].gain_db, -OAL_EQ_MAX_GAIN_DB, 0.001, "cut clamped");
    check_close(curve.bands[1].hz, OAL_EQ_MIN_HZ, 0.001, "frequency clamped up");
    check_close(curve.bands[1].gain_db, OAL_EQ_MAX_GAIN_DB, 0.001, "boost clamped");
}

/* Half a vector applied to a loudspeaker is worse than none. */
static void test_something_that_is_not_a_vector_is_refused(void)
{
    oal_eq_curve_t curve = { .count = 7 };
    check(!oal_eq_parse("104/3.78", &curve), "two numbers is not a band");
    check(curve.count == 7, "and the old vector is left alone");
    check(!oal_eq_parse("boom", &curve), "words are not numbers");
    check(!oal_eq_parse("100/1/-3 junk/2/1", &curve), "one bad band fails the lot");
    check(!oal_eq_parse("1/1/1 2/1/1 3/1/1 4/1/1 5/1/1 6/1/1 7/1/1 8/1/1 9/1/1", &curve),
          "more bands than the chain can run");
}

static void test_it_round_trips(void)
{
    oal_eq_curve_t curve, again;
    char text[OAL_EQ_TEXT_MAX];

    check(oal_eq_parse("104.0/3.78/-9.0 151.2/5.01/-4.8 220.2/7.02/-3.5", &curve), "parses");
    check(oal_eq_format(&curve, text, sizeof(text)) > 0, "formats");
    check(strcmp(text, "104.0/3.78/-9.0 151.2/5.01/-4.8 220.2/7.02/-3.5") == 0, "exactly");

    check(oal_eq_parse(text, &again), "and parses back");
    check(again.count == curve.count, "to the same count");
    for (uint8_t i = 0; i < curve.count; i++) {
        check_close(again.bands[i].hz, curve.bands[i].hz, 0.05, "same frequency");
        check_close(again.bands[i].q, curve.bands[i].q, 0.005, "same Q");
        check_close(again.bands[i].gain_db, curve.bands[i].gain_db, 0.05, "same gain");
    }
}

static void test_a_buffer_too_small_says_so(void)
{
    oal_eq_curve_t curve;
    char text[8];
    check(oal_eq_parse("104.0/3.78/-9.0 151.2/5.01/-4.8", &curve), "parses");
    check(oal_eq_format(&curve, text, sizeof(text)) < 0, "refuses a short buffer");
    check(text[0] == '\0', "and leaves nothing half written");
}

static void test_the_longest_vector_fits_the_stored_size(void)
{
    oal_eq_curve_t curve = { .count = OAL_EQ_MAX_BANDS };
    for (uint8_t i = 0; i < OAL_EQ_MAX_BANDS; i++) {
        curve.bands[i].hz = 19999.9f;
        curve.bands[i].q = 19.99f;
        curve.bands[i].gain_db = -14.9f;
    }
    char text[OAL_EQ_TEXT_MAX];
    check(oal_eq_format(&curve, text, sizeof(text)) > 0,
          "the widest vector fits OAL_EQ_TEXT_MAX");
}

/* ----------------------------------------------------------------- filter */

/** The same filter in double, as the definition to measure against. */
static double reference(double hz, double q, double gain_db, double at)
{
    double a = pow(10.0, gain_db / 40.0);
    double w = 2.0 * PI * hz / RATE;
    double alpha = sin(w) / (2.0 * q);
    double cosw = cos(w);

    double b0 = (1 + alpha * a), b1 = -2 * cosw, b2 = (1 - alpha * a);
    double a0 = (1 + alpha / a), a1 = -2 * cosw, a2 = (1 - alpha / a);
    b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;

    double w2 = 2.0 * PI * at / RATE;
    double c1 = cos(w2), c2 = cos(2 * w2);
    double num = b0 * b0 + b1 * b1 + b2 * b2 + 2 * (b0 * b1 + b1 * b2) * c1 + 2 * b0 * b2 * c2;
    double den = 1 + a1 * a1 + a2 * a2 + 2 * (a1 + a1 * a2) * c1 + 2 * a2 * c2;
    return 10.0 * log10(num / den);
}

/** What the chain actually does to a sine, in dB. */
static double measured(oal_eq_chain_t *chain, double hz)
{
    oal_eq_chain_reset(chain);

    /* Long enough for a 30 Hz section to have stopped ringing, then a whole
     * number of seconds of measurement. */
    const size_t settle = RATE * 2;
    const size_t count = RATE * 2;
    double energy = 0;

    for (size_t n = 0; n < settle + count; n++) {
        double value = sin(2.0 * PI * hz * (double)n / RATE) * 100000000.0;
        int32_t sample = (int32_t)value;
        oal_eq_chain_run(chain, &sample, 1, 1);
        if (n >= settle) {
            energy += (double)sample * (double)sample;
        }
    }
    double amplitude = sqrt(2.0 * energy / (double)count);
    return 20.0 * log10(amplitude / 100000000.0);
}

/*
 * The test this file exists for.
 *
 * The correction band is 30-300 Hz, which at 48 kHz puts a biquad's poles
 * within a thousandth of the unit circle — the region where single
 * precision runs out and a filter quietly stops being the filter that was
 * designed. The ESP32-S3's floating point unit is single precision only, so
 * double is software emulation and far too slow for eight sections at two
 * hundred thousand samples a second; the question is not which is nicer but
 * whether the fast one is good enough. Measured, not assumed.
 */
static void test_single_precision_survives_the_bottom_of_the_band(void)
{
    const struct { double hz, q, gain; } cases[] = {
        { 30, 2.0, -9.0 },     /* the very bottom of what may be corrected */
        { 40, 4.0, +3.0 },
        { 63, 1.5, -6.0 },
        { 104, 3.78, -9.0 },   /* the mode both loudspeakers here showed */
        { 300, 8.0, -4.0 },    /* the top of the band, at the narrowest Q */
    };

    for (size_t i = 0; i < sizeof(cases) / sizeof(cases[0]); i++) {
        oal_eq_curve_t curve = { .count = 1 };
        curve.bands[0].hz = (float)cases[i].hz;
        curve.bands[0].q = (float)cases[i].q;
        curve.bands[0].gain_db = (float)cases[i].gain;

        oal_eq_chain_t chain;
        oal_eq_chain_build(&chain, &curve, RATE);
        check(chain.count == 1, "one section built");

        /* At the centre, and half an octave either side where the skirt is
         * steepest and an error in the pole position shows most. */
        const double at[] = { cases[i].hz / 1.41, cases[i].hz, cases[i].hz * 1.41 };
        for (size_t k = 0; k < 3; k++) {
            char what[96];
            snprintf(what, sizeof(what), "%.0f Hz filter at %.0f Hz",
                     cases[i].hz, at[k]);
            check_close(measured(&chain, at[k]),
                        reference(cases[i].hz, cases[i].q, cases[i].gain, at[k]),
                        0.15, what);
        }
    }
}

static void test_a_chain_of_bands_is_the_sum_of_them(void)
{
    oal_eq_curve_t curve = { .count = 3 };
    curve.bands[0] = (oal_eq_band_t){ 60.0f, 2.0f, -6.0f };
    curve.bands[1] = (oal_eq_band_t){ 104.0f, 3.78f, -9.0f };
    curve.bands[2] = (oal_eq_band_t){ 220.0f, 5.0f, -3.5f };

    oal_eq_chain_t chain;
    oal_eq_chain_build(&chain, &curve, RATE);
    check(chain.count == 3, "three sections");

    for (double hz = 40; hz < 400; hz *= 1.5) {
        double want = reference(60, 2, -6, hz)
                    + reference(104, 3.78, -9, hz)
                    + reference(220, 5, -3.5, hz);
        char what[64];
        snprintf(what, sizeof(what), "chain at %.0f Hz", hz);
        check_close(measured(&chain, hz), want, 0.2, what);
    }
}

static void test_a_band_that_does_nothing_is_not_run(void)
{
    oal_eq_curve_t curve = { .count = 2 };
    curve.bands[0] = (oal_eq_band_t){ 100.0f, 2.0f, 0.0f };
    curve.bands[1] = (oal_eq_band_t){ 200.0f, 2.0f, -4.0f };

    oal_eq_chain_t chain;
    oal_eq_chain_build(&chain, &curve, RATE);
    check(chain.count == 1, "the flat band costs nothing");
    check(oal_eq_chain_active(&chain), "and the chain still does something");
}

static void test_an_empty_chain_leaves_the_audio_alone(void)
{
    oal_eq_curve_t curve = { 0 };
    oal_eq_chain_t chain;
    oal_eq_chain_build(&chain, &curve, RATE);

    check(!oal_eq_chain_active(&chain), "nothing to do");

    int32_t samples[4] = { 1000, -2000, 3000, -4000 };
    int32_t before[4] = { 1000, -2000, 3000, -4000 };
    oal_eq_chain_run(&chain, samples, 4, 1);
    check(memcmp(samples, before, sizeof(samples)) == 0, "and it did nothing");
}

/* Only one half of an interleaved frame belongs to this chain. */
static void test_it_touches_only_its_own_channel(void)
{
    oal_eq_curve_t curve = { .count = 1 };
    curve.bands[0] = (oal_eq_band_t){ 100.0f, 1.0f, -10.0f };

    oal_eq_chain_t chain;
    oal_eq_chain_build(&chain, &curve, RATE);

    int32_t frames[8] = { 1000000, 7, 1000000, 7, 1000000, 7, 1000000, 7 };
    oal_eq_chain_run(&chain, frames, 4, 2);

    for (size_t i = 0; i < 4; i++) {
        check(frames[i * 2 + 1] == 7, "the other channel is untouched");
    }
    check(frames[0] != 1000000, "and its own channel was filtered");
}

/*
 * A biquad's state is the last two samples it saw. Carrying that across a
 * discontinuity — a re-prime, a new correction — rings for as long as the
 * filter decays, which at 30 Hz is a noticeable while.
 */
static void test_state_can_be_forgotten(void)
{
    oal_eq_curve_t curve = { .count = 1 };
    curve.bands[0] = (oal_eq_band_t){ 100.0f, 4.0f, 9.0f };

    oal_eq_chain_t chain;
    oal_eq_chain_build(&chain, &curve, RATE);

    int32_t loud[64];
    for (size_t i = 0; i < 64; i++) {
        loud[i] = 100000000;
    }
    oal_eq_chain_run(&chain, loud, 64, 1);
    check(chain.sections[0].z1 != 0.0f, "the filter is ringing");

    oal_eq_chain_reset(&chain);
    check(chain.sections[0].z1 == 0.0f && chain.sections[0].z2 == 0.0f, "and now it is not");

    int32_t quiet = 0;
    oal_eq_chain_run(&chain, &quiet, 1, 1);
    check(quiet == 0, "silence in, silence out");
}

/* Wrapping sounds like inversion; saturation sounds like loud. */
static void test_it_saturates_rather_than_wrapping(void)
{
    oal_eq_curve_t curve = { .count = 1 };
    curve.bands[0] = (oal_eq_band_t){ 100.0f, 1.0f, 12.0f };

    oal_eq_chain_t chain;
    oal_eq_chain_build(&chain, &curve, RATE);

    for (size_t n = 0; n < 4000; n++) {
        int32_t sample = (int32_t)(sin(2.0 * PI * 100.0 * (double)n / RATE) * 2100000000.0);
        int32_t was = sample;
        oal_eq_chain_run(&chain, &sample, 1, 1);
        if (was > 1500000000) {
            check(sample > 0, "a loud positive sample never came back negative");
        }
    }
}

int main(void)
{
    test_parses_a_vector();
    test_forgives_a_persons_spacing();
    test_an_empty_vector_is_how_a_correction_is_cleared();
    test_a_wild_band_is_brought_inside_the_fence();
    test_something_that_is_not_a_vector_is_refused();
    test_it_round_trips();
    test_a_buffer_too_small_says_so();
    test_the_longest_vector_fits_the_stored_size();

    test_single_precision_survives_the_bottom_of_the_band();
    test_a_chain_of_bands_is_the_sum_of_them();
    test_a_band_that_does_nothing_is_not_run();
    test_an_empty_chain_leaves_the_audio_alone();
    test_it_touches_only_its_own_channel();
    test_state_can_be_forgotten();
    test_it_saturates_rather_than_wrapping();

    if (failures != 0) {
        printf("%d check(s) failed\n", failures);
        return 1;
    }
    printf("oal_eq: all checks passed\n");
    return 0;
}
