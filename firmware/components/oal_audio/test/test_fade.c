/*
 * The ramps that turn a click into a dip.
 *
 * Worth testing on the host rather than by ear: a sign error or an
 * overflow here is not a build failure, it is a noise on a speaker, and the
 * events these shape are rare enough that a fault could go months without
 * being reproduced deliberately.
 */
#include "oal_fade.h"

#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define CH 2
#define FRAMES 240

static void fill(int32_t *buf, size_t frames, int32_t value)
{
    for (size_t i = 0; i < frames * CH; i++) {
        buf[i] = value;
    }
}

static void ends_at_exactly_silence(void)
{
    int32_t buf[FRAMES * CH];
    const int32_t from[CH] = { 1 << 30, -(1 << 30) };
    memset(buf, 0, sizeof(buf));

    oal_fade_to_silence(buf, FRAMES, from, CH);

    /* The last frame of the ramp is silence, not nearly silence: a ramp
     * that stops short leaves the step it was meant to remove. */
    for (unsigned c = 0; c < CH; c++) {
        assert(buf[(OAL_FADE_FRAMES - 1) * CH + c] == 0);
    }
    /* And it started close to where the audio was. */
    const int32_t most = (int32_t)((int64_t)(1 << 30) * 9 / 10);
    assert(buf[0] > most);
    assert(buf[1] < -most);
    puts("a fade to silence reaches exactly zero");
}

static void falls_without_reversing(void)
{
    int32_t buf[FRAMES * CH];
    const int32_t from[CH] = { 1 << 29, 1 << 29 };
    memset(buf, 0, sizeof(buf));

    oal_fade_to_silence(buf, FRAMES, from, CH);

    for (size_t i = 1; i < OAL_FADE_FRAMES; i++) {
        assert(buf[i * CH] <= buf[(i - 1) * CH]);
    }
    /* Past the ramp it is silent and stays silent. */
    for (size_t i = OAL_FADE_FRAMES; i < FRAMES; i++) {
        assert(buf[i * CH] == 0 && buf[i * CH + 1] == 0);
    }
    puts("it falls monotonically and leaves the rest alone");
}

static void crossfade_lands_on_the_signal(void)
{
    int32_t buf[FRAMES * CH];
    const int32_t from[CH] = { -(1 << 28), 1 << 28 };
    fill(buf, FRAMES, 1 << 27);

    oal_fade_from(buf, FRAMES, from, CH);

    /* The last frame of the ramp is the chunk's own sample, so the join to
     * the untouched remainder is continuous by construction. */
    for (unsigned c = 0; c < CH; c++) {
        assert(buf[(OAL_FADE_FRAMES - 1) * CH + c] == (1 << 27));
        assert(buf[OAL_FADE_FRAMES * CH + c] == (1 << 27));
    }
    /* And the first frame is nearer where the speaker was than where the
     * new audio is, which is the point of crossfading rather than
     * fading up from nothing. */
    assert(buf[0] < 0);
    puts("a crossfade starts at the old signal and lands on the new one");
}

static void silence_to_audio_is_a_plain_fade_in(void)
{
    int32_t buf[FRAMES * CH];
    const int32_t from[CH] = { 0, 0 };
    fill(buf, FRAMES, 1 << 26);

    oal_fade_from(buf, FRAMES, from, CH);

    assert(buf[0] > 0 && buf[0] < (1 << 26));
    for (size_t i = 1; i < OAL_FADE_FRAMES; i++) {
        assert(buf[i * CH] >= buf[(i - 1) * CH]);
    }
    puts("resuming from silence rises without stepping");
}

static void extremes_do_not_overflow(void)
{
    int32_t buf[FRAMES * CH];
    const int32_t from[CH] = { INT32_MAX, INT32_MIN };

    memset(buf, 0, sizeof(buf));
    oal_fade_to_silence(buf, FRAMES, from, CH);
    /* Full scale must stay inside full scale. A 32-bit intermediate would
     * have wrapped here and inverted the sample, which is the loudest
     * possible version of the fault this code exists to prevent. */
    assert(buf[0] > 0);
    assert(buf[1] < 0);

    fill(buf, FRAMES, INT32_MIN);
    oal_fade_from(buf, FRAMES, from, CH);
    assert(buf[0] > 0);          /* still dominated by from[0] = INT32_MAX */
    assert(buf[1] < 0);
    puts("full-scale samples survive both ramps");
}

static void short_regions_are_handled(void)
{
    int32_t buf[FRAMES * CH];
    const int32_t from[CH] = { 1 << 30, 1 << 30 };

    /* Less room than a full ramp: it must still finish at silence inside
     * the space it was given rather than running past it. */
    memset(buf, 0, sizeof(buf));
    oal_fade_to_silence(buf, 10, from, CH);
    assert(buf[9 * CH] == 0);
    assert(buf[10 * CH] == 0);

    /* Too short to crossfade: left untouched, because a partial crossfade
     * would end mid-glide and step at the join. */
    fill(buf, FRAMES, 1234);
    oal_fade_from(buf, OAL_FADE_FRAMES - 1, from, CH);
    assert(buf[0] == 1234);
    puts("short regions ramp within themselves or not at all");
}

static void null_arguments_are_survivable(void)
{
    int32_t buf[FRAMES * CH];
    const int32_t from[CH] = { 1, 2 };
    memset(buf, 0, sizeof(buf));

    oal_fade_to_silence(NULL, FRAMES, from, CH);
    oal_fade_to_silence(buf, FRAMES, NULL, CH);
    oal_fade_to_silence(buf, 0, from, CH);
    oal_fade_to_silence(buf, FRAMES, from, 0);
    oal_fade_from(NULL, FRAMES, from, CH);
    oal_fade_from(buf, FRAMES, NULL, CH);
    puts("null and empty arguments are survivable");
}

int main(void)
{
    ends_at_exactly_silence();
    falls_without_reversing();
    crossfade_lands_on_the_signal();
    silence_to_audio_is_a_plain_fade_in();
    extremes_do_not_overflow();
    short_regions_are_handled();
    null_arguments_are_survivable();
    puts("\nall fade checks passed");
    return 0;
}
