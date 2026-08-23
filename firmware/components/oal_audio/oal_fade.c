#include "oal_fade.h"

/* One in Q15. */
#define FADE_ONE 32768

void oal_fade_to_silence(int32_t *at, size_t frames,
                         const int32_t *from, unsigned channels)
{
    if (at == NULL || from == NULL || channels == 0 || frames == 0) {
        return;
    }

    size_t n = frames < OAL_FADE_FRAMES ? frames : OAL_FADE_FRAMES;
    for (size_t i = 0; i < n; i++) {
        /* Ends at exactly zero on the last frame: (i+1)/n reaches one, so
         * the weight reaches nothing. A ramp that stops just short leaves
         * a small step, which is the whole thing being avoided. */
        int64_t w = FADE_ONE - (int64_t)(i + 1) * FADE_ONE / (int64_t)n;
        for (unsigned c = 0; c < channels; c++) {
            at[i * channels + c] = (int32_t)(((int64_t)from[c] * w) >> 15);
        }
    }
}

void oal_fade_from(int32_t *chunk, size_t frames,
                   const int32_t *from, unsigned channels)
{
    if (chunk == NULL || from == NULL || channels == 0
            || frames < OAL_FADE_FRAMES) {
        return;
    }

    for (size_t i = 0; i < OAL_FADE_FRAMES; i++) {
        int64_t w = (int64_t)(i + 1) * FADE_ONE / OAL_FADE_FRAMES;
        for (unsigned c = 0; c < channels; c++) {
            size_t k = i * channels + c;
            /* The last frame of the ramp is the chunk's own sample,
             * untouched: w reaches one there, so the join to the rest of
             * the chunk is continuous by construction rather than by
             * being close enough. */
            chunk[k] = (int32_t)(((int64_t)from[c] * (FADE_ONE - w)
                                  + (int64_t)chunk[k] * w) >> 15);
        }
    }
}
