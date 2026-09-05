#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Where a speaker is on the sender's timeline.
 *
 * **The problem this exists for.** Until now the playout knew how *much*
 * audio it held and not *where* that audio sat, and those answer different
 * questions. A burst of packets raises the depth by 60 ms without moving
 * the sound at all, so a loop steering on depth must tolerate a swing that
 * is not an error -- which is why the tolerance ended up 120 ms wide, and
 * why a speaker could sit 100 ms behind its partner with every counter
 * healthy and no correction path able to see it. The log of 2026-09-04 has
 * twelve minutes of exactly that: the audible offset and the depth
 * difference agreeing at r = 0.949, and nothing with a trigger that fired.
 *
 * **Why the sender's timestamp is the right reference.** It is one clock,
 * stamped on every packet, and every consumer of a stream sees the same
 * one. So two speakers can be brought into step without either of them
 * being told anything by anybody -- no coordinator, no Hub, no messages
 * between the nodes. That matters beyond tidiness: decision 4 says the Hub
 * is not required and standalone mode is the test that no hidden dependency
 * on it survived. A turntable ESP streaming to two speakers with no Hub on
 * the network is the case sync matters most in, and it is the case a
 * Hub-hosted correction could not serve at all.
 *
 * **The quantity.**
 *
 *     phase error = (local clock - position playing) - (least delay + target)
 *
 * `local clock - position playing` is how far behind the sender's timeline
 * this speaker is running, in its own units. `least delay` is the smallest
 * arrival-minus-timestamp seen recently, which is the least-delayed path
 * from the sender -- the standard minimum filter, because the minimum is
 * the sample least polluted by queueing. Subtracting it removes the two
 * nodes' arbitrary clock epochs and the network's fixed share, and leaves
 * the part that is this node's own doing.
 *
 * **Why it cannot swing with a burst**, which is the whole reason it is
 * worth computing rather than reading the depth. A burst advances the
 * newest sample held and the depth by the same number of frames, and the
 * position is the first minus the second, so the two cancel exactly.
 * `Bursts_do_not_move_the_phase` in the test asserts it on the arithmetic
 * rather than leaving it as an argument.
 *
 * Free of ESP-IDF, so it can be linked into a host test. That is not
 * incidental: the arithmetic here is full of uint32 wraparound, the file it
 * would otherwise live in cannot be compiled on a host, and the project's
 * other answer to that -- copying the sums into the test and a comment
 * saying to keep them in step -- has already shipped a threshold nobody
 * had checked.
 */

/**
 * How long one minimum-delay window lasts, microseconds.
 *
 * Two of these are kept and the smaller is used, so the effective window is
 * ten to twenty seconds. Long enough that a quiet moment lands in it --
 * every packet queued behind a Wi-Fi retry over a whole window would leave
 * the estimate too high -- and short enough to follow the drift between
 * this board's crystal and the sender's, which at 50 ppm moves half a
 * millisecond in ten seconds.
 */
#define OAL_PHASE_WINDOW_US 10000000ull

/** Tracker state. Zeroed is a valid starting point; prefer oal_phase_reset. */
typedef struct {
    uint32_t write_rtp;        /* one past the newest frame the ring holds */
    bool     write_rtp_known;

    /*
     * The least delay seen, kept as an offset from the first sample taken.
     *
     * Relative rather than absolute because the raw figure is the
     * difference between two unrelated clock epochs -- an arbitrary 32-bit
     * number that may sit anywhere, including right beside the wrap. Two
     * such numbers cannot be compared with `<`. Differences from a base
     * can, and they stay small: an hour of 50 ppm drift is 180 ms.
     */
    uint32_t delay_base;
    bool     delay_base_known;
    int32_t  delay_now;        /* least of the current window */
    bool     delay_now_known;
    int32_t  delay_prev;       /* least of the one before */
    bool     delay_prev_known;
    uint64_t window_started_us;

    /*
     * Frames of pre-break audio still to be played.
     *
     * After a timeline break the ring holds audio from before the jump,
     * and its position is not described by the new `write_rtp`. Rather than
     * report a number that is wrong for a fifth of a second, the position
     * is withheld until the old audio has been played out.
     */
    uint32_t settling_frames;

    uint32_t breaks;           /* times the sender's timeline jumped */
} oal_phase_t;

/** Forgets everything. Use when the stream stops or the ring is emptied. */
void oal_phase_reset(oal_phase_t *phase);

/**
 * Records one arriving packet.
 *
 * @param rtp_timestamp the sender's stamp for the packet's first frame
 * @param frames        frames in the packet
 * @param arrival_rtp   local clock at arrival, in RTP units (48 kHz)
 * @param now_us        local clock in microseconds, for window rotation
 * @param frames_held   frames already in the ring, needed only to know how
 *                      much pre-break audio a break leaves behind
 *
 * @return true if this packet did not continue the timeline -- a stream
 *         restart, a seek, or a hole left by loss. The caller is expected to
 *         count it; the tracker re-seats itself either way.
 *
 * A break resets the delay estimate as well as the position. It has to: a
 * restart gives the stream a new random timestamp base, so every delay
 * sample taken against the old one describes a timeline that no longer
 * exists, and keeping them would hold the estimate at an arbitrary number
 * for a whole window.
 */
bool oal_phase_on_packet(oal_phase_t *phase, uint32_t rtp_timestamp, uint32_t frames,
                         uint32_t arrival_rtp, uint64_t now_us, uint32_t frames_held);

/** Tells the tracker frames have left the ring, so a break can settle. */
void oal_phase_on_played(oal_phase_t *phase, uint32_t frames);

/**
 * The sender's timestamp of the sample about to be played.
 *
 * @param frames_held frames currently in the ring
 * @return false when there is nothing to say yet, leaving @p out untouched.
 *
 * This is the figure that makes two speakers directly comparable: it is on
 * a timeline they share, so the difference between two of them is the
 * offset between them, in samples, without either node's clock entering
 * into it.
 */
bool oal_phase_position(const oal_phase_t *phase, uint32_t frames_held, uint32_t *out);

/**
 * How far this speaker is from where it should be, in frames. **Positive is
 * late** — playing audio older than it should be, which against a partner
 * that is not late is a slap echo.
 *
 * @param now_rtp       local clock now, in RTP units
 * @param target_frames the intended depth, which is the delay this design
 *                      means to run at
 * @return false while the estimate is not yet trustworthy.
 */
bool oal_phase_error(const oal_phase_t *phase, uint32_t frames_held, uint32_t now_rtp,
                     uint32_t target_frames, int32_t *out);

#ifdef __cplusplus
}
#endif
