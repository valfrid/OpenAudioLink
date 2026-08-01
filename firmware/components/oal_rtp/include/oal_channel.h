#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Which of the stream's two channels a Consumer plays (decision 10).
 *
 * The stream is stereo and byte-identical to every destination, because
 * that identity is what lets several speakers stay in step. What a given
 * node does with the two channels is a property of the node — a single
 * mono speaker in a kitchen is a first-class case for this project, not a
 * degraded stereo one.
 *
 * Free of ESP-IDF headers so the arithmetic can be tested on a host. It is
 * worth testing: a sum that clips or a sign extension that wraps produces
 * audible damage that a listener would blame on the network.
 */

typedef enum {
    OAL_CHANNEL_STEREO = 0,  /* both, as they arrive */
    OAL_CHANNEL_MONO,        /* (L+R)/2 in both slots */
    OAL_CHANNEL_LEFT,        /* L in both slots */
    OAL_CHANNEL_RIGHT,       /* R in both slots */
} oal_channel_t;

/** Unconfigured nodes play stereo; it discards nothing. */
#define OAL_CHANNEL_DEFAULT OAL_CHANNEL_STEREO

/** Wire name: "stereo", "mono", "left", "right". */
const char *oal_channel_name(oal_channel_t channel);

/**
 * Parses a wire name. Returns false for anything unrecognised rather than
 * falling back to stereo, so a typo is refused instead of silently giving
 * a mono speaker both channels.
 */
bool oal_channel_parse(const char *name, oal_channel_t *out);

/**
 * Rewrites an L24 stereo payload in place so both slots carry the chosen
 * signal. `stereo` leaves it untouched.
 *
 * Both slots on purpose. The DAC then presents the same signal on its left
 * and right outputs, so one node drives one speaker or two identical ones
 * with no further configuration — and no assembly can wire the silent
 * output by mistake.
 *
 * @param payload L24 stereo, interleaved left then right
 * @param frames  frames present, not bytes
 */
void oal_channel_apply(uint8_t *payload, size_t frames, oal_channel_t channel);

#ifdef __cplusplus
}
#endif
