#include "oal_phase.h"

#include <string.h>

void oal_phase_reset(oal_phase_t *phase)
{
    if (phase == NULL) {
        return;
    }
    uint32_t breaks = phase->breaks;
    memset(phase, 0, sizeof(*phase));
    /* Kept across a reset: it counts what the link has done to this stream,
     * and zeroing it here would hide the very events that caused the reset. */
    phase->breaks = breaks;
}

/** Starts a fresh pair of delay windows, forgetting both. */
static void restart_delay(oal_phase_t *phase, uint32_t delay, uint64_t now_us)
{
    phase->delay_base = delay;
    phase->delay_base_known = true;
    phase->delay_now = 0;
    phase->delay_now_known = true;
    phase->delay_prev = 0;
    phase->delay_prev_known = false;
    phase->window_started_us = now_us;
}

bool oal_phase_on_packet(oal_phase_t *phase, uint32_t rtp_timestamp, uint32_t frames,
                         uint32_t arrival_rtp, uint64_t now_us, uint32_t frames_held)
{
    if (phase == NULL || frames == 0) {
        return false;
    }

    /*
     * Did this packet continue the timeline?
     *
     * Exact equality, not a tolerance. The consumer has already dropped
     * duplicates and packets too late to use, so what arrives here should
     * begin exactly where the last one ended; anything else means audio is
     * missing or the sender started again, and both invalidate the position
     * by the size of the discrepancy. A tolerance here would let a small
     * hole through as if it were nothing, and a small hole is tens of
     * milliseconds — which is the whole quantity being measured.
     */
    bool broke = false;
    if (phase->write_rtp_known && rtp_timestamp != phase->write_rtp) {
        broke = true;
        phase->breaks++;
        /*
         * The ring still holds audio from before the jump, and the new
         * timeline says nothing about where that audio sits. Withhold the
         * position until it has all been played rather than publish a
         * number that is wrong by the size of the break.
         */
        phase->settling_frames = frames_held;
    }

    phase->write_rtp = rtp_timestamp + frames;
    phase->write_rtp_known = true;

    /*
     * This packet's delay: how far behind the sender's stamp it arrived,
     * on this node's clock. The absolute value is meaningless — it contains
     * the difference between two unrelated epochs — and the differences
     * between successive samples are exactly what is wanted.
     */
    uint32_t delay = arrival_rtp - rtp_timestamp;

    if (broke || !phase->delay_base_known) {
        /* A restart hands the stream a new random timestamp base, so every
         * sample taken against the old one describes a timeline that no
         * longer exists. Keeping them would hold the estimate at an
         * arbitrary number for a whole window. */
        restart_delay(phase, delay, now_us);
        return broke;
    }

    if (now_us - phase->window_started_us >= OAL_PHASE_WINDOW_US) {
        phase->delay_prev = phase->delay_now;
        phase->delay_prev_known = phase->delay_now_known;
        phase->delay_now_known = false;
        phase->window_started_us = now_us;
    }

    int32_t relative = (int32_t)(delay - phase->delay_base);
    if (!phase->delay_now_known || relative < phase->delay_now) {
        phase->delay_now = relative;
        phase->delay_now_known = true;
    }

    return broke;
}

void oal_phase_on_played(oal_phase_t *phase, uint32_t frames)
{
    if (phase == NULL) {
        return;
    }
    if (phase->settling_frames > frames) {
        phase->settling_frames -= frames;
    } else {
        phase->settling_frames = 0;
    }
}

bool oal_phase_position(const oal_phase_t *phase, uint32_t frames_held, uint32_t *out)
{
    if (phase == NULL || out == NULL || !phase->write_rtp_known
            || phase->settling_frames > 0) {
        return false;
    }
    /* Everything the ring holds is played before the newest frame in it, so
     * the sample at the read pointer is that many frames earlier. */
    *out = phase->write_rtp - frames_held;
    return true;
}

/** The least delay across both windows, as an absolute RTP-unit figure. */
static bool least_delay(const oal_phase_t *phase, uint32_t *out)
{
    if (!phase->delay_base_known) {
        return false;
    }
    bool have = false;
    int32_t least = 0;
    if (phase->delay_now_known) {
        least = phase->delay_now;
        have = true;
    }
    if (phase->delay_prev_known && (!have || phase->delay_prev < least)) {
        least = phase->delay_prev;
        have = true;
    }
    if (!have) {
        return false;
    }
    *out = phase->delay_base + (uint32_t)least;
    return true;
}

bool oal_phase_error(const oal_phase_t *phase, uint32_t frames_held, uint32_t now_rtp,
                     uint32_t target_frames, int32_t *out)
{
    if (phase == NULL || out == NULL) {
        return false;
    }

    uint32_t position;
    if (!oal_phase_position(phase, frames_held, &position)) {
        return false;
    }

    uint32_t least;
    if (!least_delay(phase, &least)) {
        return false;
    }

    /*
     * All of it in uint32 and cast once at the end.
     *
     * `now_rtp - position` is the difference between this node's clock and
     * the sender's, which is a large arbitrary number; so is `least`. Only
     * their difference is small, and doing the arithmetic in unsigned means
     * the wrap cancels rather than having to be reasoned about.
     */
    uint32_t behind = now_rtp - position;
    *out = (int32_t)(behind - least - target_frames);
    return true;
}
