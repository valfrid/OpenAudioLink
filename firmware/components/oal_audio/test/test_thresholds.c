/*
 * The playout's threshold arithmetic, checked on the host.
 *
 * These formulas decide where the buffer sits, and where it sits is what
 * puts two speakers in step: the offset between two consumers *is* the
 * difference in their depths, because nothing in this design says when a
 * sample is due. So the ordering below is not tidiness, it is the sync
 * behaviour.
 *
 * MIRRORS apply_target() in oal_playout.c and must be changed with it.
 * That file needs ESP-IDF to compile, so the sums cannot be linked here;
 * they are copied. The point is to catch an arithmetic mistake across the
 * whole settable range rather than on the one ring the author tried --
 * a `#define` that was never added and a trim line that scaled with
 * capacity have both cost hardware sessions before.
 */

#include <assert.h>
#include <stdio.h>
#include <stdlib.h>

#define OAL_RTP_CHANNELS 2
#define OAL_RTP_SAMPLE_RATE 48000u
#define OAL_RING_MS_MIN 50u
#define OAL_RING_MS_MAX 1000u
#define OAL_PLAYOUT_MS 100u
#define OAL_DELAY_MS_MAX 650u

#define TARGET_FRACTION_NUM 3
#define TARGET_FRACTION_DEN 4

typedef struct {
    size_t capacity;
    size_t target;
    size_t trim_above;
    size_t pad_below;
    size_t converge_below;
} thresholds_t;

static thresholds_t compute(unsigned ring_ms, unsigned delay_ms)
{
    thresholds_t t;
    const unsigned rate = OAL_RTP_SAMPLE_RATE;

    t.capacity = (size_t)rate * ring_ms / 1000 * OAL_RTP_CHANNELS;

    unsigned target_ms = OAL_PLAYOUT_MS + delay_ms;
    t.target = (size_t)rate * target_ms / 1000 * OAL_RTP_CHANNELS;

    size_t ceiling = t.capacity / TARGET_FRACTION_DEN * TARGET_FRACTION_NUM;
    if (t.target > ceiling) {
        t.target = ceiling;
    }

    t.trim_above = t.target + t.target / 2;
    size_t rim = t.capacity - t.capacity / 8;
    if (t.trim_above > rim) {
        t.trim_above = rim;
    }

    t.pad_below = t.target * 3 / 4;

    t.converge_below = t.trim_above - t.target / 16;
    if (t.converge_below < t.pad_below) {
        t.converge_below = t.pad_below;
    }
    /* The sixteenth truncates odd once the target is clamped to the
     * capacity ceiling; see apply_target(). */
    t.converge_below -= t.converge_below % OAL_RTP_CHANNELS;
    return t;
}

static size_t ms_of(size_t samples)
{
    return samples * 1000 / (OAL_RTP_SAMPLE_RATE * OAL_RTP_CHANNELS);
}

int main(void)
{
    /*
     * The invariants, over every ring and delay the control surface allows.
     *
     * The delay ceiling is itself derived from the ring (three quarters of
     * capacity, less the default target), so the loop asks for more than
     * the node would ever accept and simply checks that clamping keeps the
     * order sane rather than that the request was reasonable.
     */
    for (unsigned ring = OAL_RING_MS_MIN; ring <= OAL_RING_MS_MAX; ring++) {
        for (unsigned delay = 0; delay <= OAL_DELAY_MS_MAX; delay += 5) {
            thresholds_t t = compute(ring, delay);

            /* Every line has to fit inside the ring, or the buffer is
             * steered at a depth it can never hold. */
            assert(t.trim_above < t.capacity);
            assert(t.converge_below < t.capacity);
            assert(t.target <= t.capacity);

            /* Steering below the trim line and above the short line: if
             * these ever cross, pad and trim fight and the fill oscillates
             * instead of settling -- which two speakers hear as wander. */
            assert(t.pad_below <= t.converge_below);
            assert(t.converge_below <= t.trim_above);

            /* The pad line stays under the target and the trim line over
             * it, so an ordinary swing triggers neither. */
            assert(t.pad_below <= t.target);
            assert(t.trim_above >= t.target);

            /* Priming now waits for converge_below, so a ring that cannot
             * hold it would never start playing at all. */
            assert(t.converge_below <= t.capacity);

            /* Whole frames throughout. The trim and pad move the read
             * pointer by exactly one frame, so a threshold on an odd
             * sample would let the pointer sit mid-frame and swap left for
             * right. Every one of these reduces to an even multiple of the
             * millisecond figure -- except converge_below, which is
             * rounded down because its sixteenth does not. */
            assert(t.capacity % OAL_RTP_CHANNELS == 0);
            assert(t.target % OAL_RTP_CHANNELS == 0);
            assert(t.trim_above % OAL_RTP_CHANNELS == 0);
            assert(t.pad_below % OAL_RTP_CHANNELS == 0);
            assert(t.converge_below % OAL_RTP_CHANNELS == 0);
        }
    }
    printf("thresholds hold across %u-%u ms rings and 0-%u ms delay\n",
           OAL_RING_MS_MIN, OAL_RING_MS_MAX, OAL_DELAY_MS_MAX);

    /*
     * The configuration actually in the house: two I²S speakers, 400 ms
     * ring, 100 ms delay. Written out because these are the numbers the
     * sync fault was diagnosed against, and a change that moves them
     * should have to say so here.
     */
    thresholds_t t = compute(400, 100);
    printf("  ring 400 / delay 100 -> target %zu ms, pad %zu, steer %zu, trim %zu, cap %zu\n",
           ms_of(t.target), ms_of(t.pad_below), ms_of(t.converge_below),
           ms_of(t.trim_above), ms_of(t.capacity));
    assert(ms_of(t.target) == 200);
    assert(ms_of(t.pad_below) == 150);
    assert(ms_of(t.trim_above) == 300);
    /* 300 - 200/16 = 287.5, truncating to 287 */
    assert(ms_of(t.converge_below) == 287);

    /*
     * The band the two speakers settle inside. This is the fault's whole
     * story: it used to be pad_below..trim_above, 150 ms wide, with no
     * force anywhere in it, so two nodes could rest 150 ms apart forever.
     */
    size_t band = ms_of(t.trim_above) - ms_of(t.converge_below);
    printf("  settling band %zu ms (was %zu ms before steering)\n",
           band, ms_of(t.trim_above) - ms_of(t.pad_below));
    assert(band <= 15);

    printf("\nall threshold checks passed\n");
    return 0;
}
