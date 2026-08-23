#pragma once

#include <stddef.h>
#include <stdint.h>

/**
 * Short ramps that turn a discontinuity into a dip.
 *
 * Every way a playout buffer fails ends in a step. An overflow moves the
 * read pointer to samples unrelated to the last one played; an underrun
 * cuts full-scale audio to zero in a single sample; resuming jumps back up
 * again. A step in a waveform is a broadband transient — a click — and it
 * is markedly more audible than the audio it replaced. Five milliseconds of
 * missing music is close to nothing. The click at each edge of it is what a
 * listener actually hears, and describes as distortion rather than as a
 * dropout.
 *
 * These are separate from the playout, and pure, because they are the part
 * that can be got wrong quietly: a sign error or an overflow in the
 * arithmetic is not a build failure, it is a noise on somebody's speaker.
 * Everything here is host-testable and tested.
 *
 * Q15 fixed point rather than floating point. This runs 200 times a second
 * inside a 5 ms deadline, the samples are 24-bit left-justified in 32, and
 * an int64 intermediate covers the worst case with room to spare.
 */

/** Frames in a full ramp: 2 ms at 48 kHz. */
#define OAL_FADE_FRAMES 96

/**
 * Walks `frames` frames of `at` from `from` down to silence, over
 * OAL_FADE_FRAMES or `frames`, whichever is shorter.
 *
 * The region must already be silent — this shapes the beginning of it and
 * leaves the rest alone.
 *
 * @param at     interleaved frames to shape, `channels` samples each.
 * @param frames how many frames are available to ramp within.
 * @param from   one frame: the last thing the sink was handed.
 */
void oal_fade_to_silence(int32_t *at, size_t frames,
                         const int32_t *from, unsigned channels);

/**
 * Crossfades the first OAL_FADE_FRAMES of `chunk` from `from` into whatever
 * the chunk holds.
 *
 * A crossfade, not a fade up from zero, because `from` is usually
 * mid-signal: after an overflow the audio did not stop, it jumped. Gliding
 * from where the speaker actually was is shorter and quieter than dipping
 * to silence and back for no reason. When `from` is silent this reduces to
 * an ordinary fade-in.
 *
 * Does nothing if `frames` is shorter than a full ramp — a partial
 * crossfade would end mid-glide and step at the join, which is the fault
 * being fixed.
 */
void oal_fade_from(int32_t *chunk, size_t frames,
                   const int32_t *from, unsigned channels);
