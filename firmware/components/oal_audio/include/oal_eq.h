#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Room correction on the node: a short chain of peaking filters per output
 * channel (docs/ROOM-CALIBRATION.md).
 *
 * **The stored form is the design, not the coefficients.** What lives in
 * NVS is a list of "frequency / Q / gain" triples in plain text, and the
 * five biquad coefficients are derived from it at boot. Storing b0..a2
 * would be smaller and faster to load and would make the setting
 * unreadable: nobody can look at 0.9976, -1.9952, 0.9976, -1.9952, 0.9952
 * and say what it does, let alone nudge it. A person has to be able to read
 * what their speaker is doing and change it by hand — that is the whole
 * reason for the format.
 *
 *     "104.0/3.78/-9.0 151.2/5.01/-4.8 220.2/7.02/-3.5"
 *      ^     ^    ^
 *      Hz    Q    dB
 *
 * One vector per **output** channel, because the correction belongs to a
 * loudspeaker rather than to a stream: a stereo node drives two speakers in
 * two corners and they do not measure the same. A node playing one channel
 * on both outputs normally carries the same vector twice, but nothing here
 * requires that — two different speakers wired to one node is a real
 * arrangement.
 *
 * Free of ESP-IDF headers so the parser and the filter can be tested on a
 * host. A wrong coefficient does not fail; it changes what a room sounds
 * like, quietly.
 */

/**
 * Bands per channel.
 *
 * The Hub's fitter stops at six on purpose — a long tail of small
 * corrections is where a room correction stops being conservative — and
 * this leaves two more for a person adding one by hand.
 */
#define OAL_EQ_MAX_BANDS 8

/** Longest stored vector: eight bands of "20000.0/20.00/-15.0 ". */
#define OAL_EQ_TEXT_MAX 192

/*
 * What a band is allowed to be, checked here rather than trusted.
 *
 * Wider than the Hub will ever fit, because hand tuning is a supported use
 * and the limits exist to stop a typing slip from destroying a tweeter, not
 * to enforce the fitting policy. The policy lives on the Hub where it can
 * be explained; this is the fence.
 */
#define OAL_EQ_MIN_HZ 10.0f
#define OAL_EQ_MAX_HZ 20000.0f
#define OAL_EQ_MIN_Q 0.1f
#define OAL_EQ_MAX_Q 20.0f
#define OAL_EQ_MAX_GAIN_DB 15.0f

/** One peaking filter, as a person writes it. */
typedef struct {
    float hz;
    float q;
    float gain_db;
} oal_eq_band_t;

/** One channel's vector. */
typedef struct {
    uint8_t count;
    oal_eq_band_t bands[OAL_EQ_MAX_BANDS];
} oal_eq_curve_t;

/**
 * Reads a stored vector.
 *
 * Whitespace separates bands and '/' separates the three numbers, so
 * "104/3.8/-9" and "104.0 / 3.80 / -9.0" both work — the second is what
 * somebody editing by hand tends to type.
 *
 * Values outside the fence above are clamped rather than rejected: a Q of
 * 25 is a slip worth correcting, not a reason to silently drop every filter
 * after it. Anything that is not three numbers is a refusal, because a
 * half-understood vector applied to a loudspeaker is worse than none.
 *
 * An empty or all-whitespace string is a valid empty vector, which is how
 * a correction is cleared.
 *
 * @return false if the text is not a vector, leaving @p out untouched.
 */
bool oal_eq_parse(const char *text, oal_eq_curve_t *out);

/**
 * Writes a vector back out, in the form oal_eq_parse reads.
 *
 * Round-trips: what comes out of this parses to the same thing, so the
 * value shown in the control API is the value stored rather than a
 * rendering of it.
 *
 * @return characters written, or -1 if the buffer is too small.
 */
int oal_eq_format(const oal_eq_curve_t *curve, char *out, size_t size);

/** One second-order section, ready to run. */
typedef struct {
    float b0, b1, b2, a1, a2;
    float z1, z2;
} oal_eq_section_t;

/** A channel's filters and their state. */
typedef struct {
    uint8_t count;
    oal_eq_section_t sections[OAL_EQ_MAX_BANDS];
} oal_eq_chain_t;

/**
 * Turns a vector into coefficients, clearing the filter state.
 *
 * Robert Bristow-Johnson's cookbook peaking filter, the same formulas the
 * Hub uses to predict what the correction will do — so the curve drawn
 * beside a measurement is the curve the speaker produces, rather than an
 * approximation of it.
 */
void oal_eq_chain_build(oal_eq_chain_t *chain, const oal_eq_curve_t *curve, int sample_rate);

/**
 * Forgets what the filters were ringing with.
 *
 * Needed wherever the audio jumps rather than continues — a re-prime, a
 * stream change, a new correction — because a biquad's state is the last
 * two samples it saw, and carrying that across a discontinuity rings for
 * as long as the filter's decay.
 */
void oal_eq_chain_reset(oal_eq_chain_t *chain);

/** Whether this chain would do anything. */
bool oal_eq_chain_active(const oal_eq_chain_t *chain);

/**
 * Runs one channel of interleaved audio in place.
 *
 * @param samples first sample of this channel
 * @param frames  frames, not samples
 * @param stride  samples between one frame and the next: 2 for stereo
 * @param gain    the correction's headroom, as a linear factor at or below
 *                one. See below — this is not a convenience.
 *
 * Full-scale int32, as the playout carries it. Saturates rather than
 * wrapping: a correction that overflows should sound loud, not inverted.
 *
 * **The headroom is applied here, inside, and that is the whole point of
 * passing it in.** A filter that boosts a band pushes material already
 * mastered near full scale past it, and the sample is clipped at the
 * moment it is written back as an int32. Attenuating afterwards scales a
 * value that has already lost its peaks — the headroom would be paid for
 * in loudness and buy nothing. Applied before the write it prevents the
 * clip, which is what it exists to do.
 *
 * Placing it at the output rather than the input is exactly equivalent: a
 * biquad is linear, so scaling before and scaling after differ only in the
 * size of the intermediate values, and those are floats with room to
 * spare.
 */
void oal_eq_chain_run(
    oal_eq_chain_t *chain, int32_t *samples, size_t frames, size_t stride, float gain);

#ifdef __cplusplus
}
#endif
