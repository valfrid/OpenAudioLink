#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "esp_err.h"
#include "oal_channel.h"
#include "oal_eq.h"
#include "oal_output.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Playing received audio through an I²S DAC (docs/HARDWARE.md).
 *
 * Two clocks that nobody synchronised meet here. Packets arrive when the
 * radio delivers them — measured at 1.2 to 2.3 ms of jitter, occasionally
 * in bursts (LINK-MEASUREMENTS.md) — and the DAC consumes exactly 48 000
 * frames a second forever. A buffer between them is not an optimisation;
 * without one every scheduling hiccup is an audible click.
 *
 * So received packets go into a ring, and a separate task feeds the I²S
 * peripheral from it at the rate the hardware sets. The ring's depth is
 * the playout delay, and it is the price of not clicking.
 *
 * What this does not do yet is correct drift. The sender's idea of 48 kHz
 * and this DAC's differ by a few parts per million, so over hours the ring
 * slowly fills or empties. Both ends are handled — a full ring drops its
 * oldest frames, an empty one plays silence — and both are counted, so the
 * effect is visible rather than mysterious. Trimming the clock through the
 * APLL is decision 2's work and belongs with multi-speaker synchronisation
 * rather than here.
 */

typedef struct {
    int bclk_gpio;
    int ws_gpio;
    int dout_gpio;

    /** Frames per second. The RTP profile's 48 000 unless testing. */
    uint32_t sample_rate;

    /**
     * How much audio to gather before playing any of it. The whole
     * latency argument in one number: too little and ordinary jitter
     * empties the ring, too much and the speaker lags the room.
     *
     * Set against the longest gap a sender leaves rather than the
     * network's jitter — a PC waking on a 15.6 ms timer leaves a bigger
     * hole than Wi-Fi does. Zero takes the 100 ms default.
     */
    uint32_t target_ms;

    /**
     * How much audio the ring can hold at most, in milliseconds.
     *
     * Distinct from `target_ms`, and the distinction is the whole point:
     * the target is how deep the buffer normally runs, this is how much
     * room exists above and below it. The target can be changed while
     * playing; this cannot, because it is an allocation.
     *
     * Zero takes 200 ms, which is what the ring was fixed at for its whole
     * life before it became a setting. Clamped to 50-1000 ms on the way in
     * -- the value arrives from NVS, and NVS outlives a rolled-back
     * firmware that never knew about it.
     */
    uint32_t ring_ms;

    /** Which of the two channels this speaker plays (decision 10). */
    oal_channel_t channel;

    /**
     * Which output stage this board has (docs/USB-AUDIO.md).
     *
     * The GPIO fields above are I2S's and are ignored when this is USB —
     * a dongle brings its own wiring, and the only pins involved are the
     * two the USB peripheral already owns.
     */
    oal_output_t output;

    /**
     * Playback level, 0-100, applied on the way to the DAC.
     *
     * Passed in rather than read from NVS here, so this component stays
     * arithmetic and I²S and knows nothing about storage — the same reason
     * the channel profile arrives the same way.
     */
    uint8_t volume;
} oal_playout_config_t;

typedef struct {
    bool running;             /* the I²S peripheral is up */
    bool playing;             /* the ring is primed and feeding it */
    uint32_t buffered_frames;
    uint32_t target_frames;
    uint32_t capacity_frames;
    uint64_t frames_played;
    uint32_t silence_frames;  /* inserted because the ring ran dry */
    uint32_t dropped_frames;  /* discarded because the ring was full */
    uint32_t underruns;       /* times it ran dry, however long each lasted */
    /*
     * Where this node started, and how much burst it threw away doing it.
     *
     * Two speakers are in step when their buffers hold the same amount --
     * nothing here says when a sample is due, so equal depth is the whole
     * of it. `steer_frames` is the line both nodes aim at and is identical
     * on nodes with the same ring and delay, so a node sitting far from it
     * is the one to look at.
     *
     * `prime_discarded_frames` is the overshoot dropped at the last prime.
     * It used to be silently kept, and whatever a burst happened to
     * deliver became that node's playback phase for the session. Reported
     * because a large value on one node and a small one on the other is
     * exactly the fault, and it is invisible from every other counter.
     */
    uint32_t primed_frames;           /* depth at the last prime */
    uint32_t prime_discarded_frames;  /* overshoot dropped at that prime */

    /*
     * How many times the ring was re-primed after running dry, which is
     * the largest single move playback phase can make and was the only one
     * of the four not being counted.
     *
     * Phase moves exactly four ways: a pad, a trim, an overflow discard
     * and a re-prime. The first three each have a counter, so "if no
     * counter moved, nothing moved" has been the rule for reading this
     * project's numbers -- and the rule had a hole in it. `underruns`
     * counts the ring going dry; whether that starve ran the full
     * STARVED_CHUNKS and repositioned the read pointer wholesale, or
     * recovered in one chunk with nothing but a little silence, was not
     * recorded anywhere. Run 39 could see 78 underruns and 3.28 s of
     * silence on one node and could not say how many of them moved the
     * phase.
     */
    uint32_t reprimes;
    uint32_t steer_frames;            /* the line the fill is steered to */
    uint32_t trimmed_frames;  /* single frames dropped to walk the fill back down */
    uint32_t padded_frames;   /* single frames repeated to walk it back up */
    uint32_t write_errors;    /* the I²S driver refused a write */

    /*
     * Times the fill was put back to the setpoint in one step rather than
     * walked there.
     *
     * The creep moves phase at about 1 ms a second, so a speaker left
     * 170 ms out of step with its partner takes nearly three minutes to
     * rejoin it — and if disturbances arrive faster than that it never
     * does. A jump costs one audible discontinuity on one speaker; being
     * a tenth of a second apart from the other speaker costs a slap echo
     * for the whole three minutes, and only one of those is worth having.
     *
     * Counted because it is a phase shift, and this project's rule is
     * that nothing may move the audio without leaving a trace. A node
     * doing this repeatedly is a node whose link keeps knocking it out,
     * which is a different fault from the one this fixes.
     */
    uint32_t resyncs;         /* fill put back to the setpoint in one step */

    /*
     * How close the ring came to empty, and to full, over the last
     * completed trace window.
     *
     * `buffered_frames` is an instant, and an instant is the wrong shape
     * for this: a poll every fifteen seconds samples one moment in three
     * thousand, and the moments that matter are the ones nobody was
     * looking at. A ring that reads a healthy 5760 on every poll may still
     * be touching zero between them.
     *
     * These are the low- and high-water marks the playout task already
     * computes for its own trace line — which on a USB node goes nowhere,
     * because the output stage owns the peripheral the console would use.
     * Publishing them costs two assignments and turns a log message the
     * affected hardware cannot print into a number the Hub can poll.
     *
     * Zero until the first window completes.
     */
    uint32_t fill_min_frames;
    uint32_t fill_max_frames;

    /*
     * Whether packets arrived in time, which is the only question that
     * decides what a listener hears.
     *
     * Measured where it is exact: the ring's fill when a packet is
     * submitted is that packet's margin, because everything already queued
     * in front of it is what the speaker plays before reaching it. No
     * timestamps, no clock comparison, nothing to drift.
     *
     * `late_packets` is unambiguous — the ring was empty, so the speaker
     * was already inserting silence when this payload arrived, and no
     * buffer anywhere can un-play that. `tight_packets` is the warning
     * population: arrived with under a quarter of the intended cushion,
     * caused nothing yet, one bad moment from causing something.
     *
     * Loss, bursts, jitter, clock drift and buffer depth are five ways of
     * arriving with less margin. Runs 23 to 33 chased each of them
     * separately without ever measuring the quantity they all reduce.
     */
    uint64_t packets_submitted;
    uint32_t late_packets;    /* arrived to find the ring already dry */
    uint32_t tight_packets;   /* arrived with under a quarter of target left */
    uint32_t margin_min_frames; /* the tightest arrival of the last window */

    /*
     * The tightest since the stream began.
     *
     * `margin_min_frames` is the right shape for watching a link live, and
     * the wrong one for judging it: measured on hardware it jumps between
     * 30 and 70 ms from window to window, because delivery pauses and
     * catches up rather than drifting, so every reading is a fresh sample
     * of a fluctuating quantity and none of them says how close the design
     * has come to failing.
     *
     * This only falls -- exactly wrong for a live indicator, exactly right
     * for sizing a buffer. "In three hours the closest this came to silence
     * was 22 ms" is what decides whether a 100 ms target is generous or
     * barely enough.
     */
    uint32_t margin_worst_frames;

    /*
     * How the margins were distributed, because a minimum is one draw.
     *
     * Measured on hardware the window minimum lands anywhere between 1 and
     * 100 ms with no visible pattern, which makes any single reading a
     * sample rather than a measurement -- the next poll would have said
     * something else. A minimum cannot tell "almost every packet arrives
     * with the full cushion and once a minute one does not" from "half of
     * them arrive with nothing to spare". Those are a healthy link and a
     * failing one, and they produce identical minima.
     *
     * Five buckets of arrival margin, as a fraction of the playout target:
     *
     *   [0] under 10%   -- a hair from silence
     *   [1] 10 to 25%
     *   [2] 25 to 50%
     *   [3] 50 to 75%
     *   [4] 75% and over -- arrived with the cushion intact
     *
     * Cumulative, so ratios between them are what to read. A link whose
     * packets sit overwhelmingly in [4] is healthy however low its worst
     * moment went; one with a fat [0] is living on the buffer whatever its
     * loss says.
     */
    uint32_t margin_buckets[5];

    /*
     * Where this speaker is on the sender's timeline, and how far that is
     * from where it should be.
     *
     * **This is the observable the buffer depth was standing in for, and
     * the reason it is a separate number is that depth answers a different
     * question.** Depth mixes what has arrived with what is playing: a
     * burst of packets raises it by 60 ms without moving the sound at all,
     * and a steering loop reading depth has to tolerate that swing. Which
     * is exactly why the tolerance is 120 ms wide, and exactly why a
     * speaker can sit 100 ms behind its partner with every counter healthy
     * and nothing able to see it. The log of 2026-09-04 has twelve minutes
     * of it: offset and depth difference agreeing at r = 0.949, and no
     * correction path with a trigger that could fire.
     *
     * `phase_error_frames` cannot swing with a burst. It is
     *
     *     (local clock - position being played) - (least delay seen + target)
     *
     * and a burst moves the newest sample and the depth by the same amount,
     * so the two cancel exactly. What is left moves only when the sound
     * moves. See the host test that asserts precisely this.
     *
     * Signed, and the sign is the useful part: **positive means this
     * speaker is late** -- playing audio older than it should be, the state
     * that produces a slap echo against a partner that is not. Negative is
     * early. Frames, so 4800 is 100 ms.
     *
     * The comparison it makes possible is between *nodes*, and it needs no
     * coordinator to make it: every consumer of one stream measures against
     * the same sender's clock, so two of them agree when their phase errors
     * agree, whether the sender is a Hub or another ESP with a turntable on
     * it, and whether or not anything else is on the network.
     *
     * `play_timestamp` is the raw position, published because it is what
     * makes two nodes comparable directly rather than through this
     * derived figure.
     */
    uint32_t play_timestamp;      /* RTP stamp of the sample being played */
    bool     play_timestamp_known;
    int32_t  phase_error_frames;  /* + is late; valid only when known */
    bool     phase_known;

    /*
     * Times the sender's timeline stopped being continuous with what the
     * ring holds -- a stream restart, a seek, enough loss to leave a hole.
     *
     * Counted rather than hidden because it is the one event that
     * invalidates the anchor: the position and the delay estimate are both
     * measured against a timeline that just moved, so both are re-seated
     * and the figures above mean nothing across the boundary. A node
     * showing many of these has a link that keeps breaking the stream, not
     * a sync problem.
     */
    uint32_t timeline_breaks;

    oal_channel_t channel;
    uint8_t volume;           /* 0-100, as last set */
} oal_playout_state_t;

/**
 * Brings up the I²S peripheral and starts the playout task. The task runs
 * and writes silence whether or not anything is arriving: a DAC needs a
 * continuous bit clock to stay locked, and one that is started and stopped
 * with the audio clicks on every track.
 */
esp_err_t oal_playout_start(const oal_playout_config_t *config);

/**
 * Hands one packet's payload to the ring, applying the channel profile on
 * the way.
 *
 * @param payload L24 big-endian stereo, **modified in place** by the
 *                channel profile. The consumer has already verified it by
 *                the time this is called.
 * @param frames  frames, not bytes.
 * @param rtp_timestamp the sender's stamp for the first frame of @p payload.
 *
 * The timestamp is what lets this ring know *where* the audio it holds sits
 * on the sender's timeline, rather than only how much of it there is. Two
 * speakers fed by one sender can then be compared without either of them
 * being told anything by anybody — see `phase_error_frames`.
 *
 * A stamp that is not contiguous with what the ring already holds is a
 * timeline break: the stream restarted, or seeked, or lost enough to leave
 * a hole. The ring re-seats onto the new timeline and counts it.
 *
 * Safe to call before the DAC has been started; it does nothing.
 */
void oal_playout_submit(uint8_t *payload, size_t frames, uint32_t rtp_timestamp);

void oal_playout_get(oal_playout_state_t *out);

/**
 * Sets the playback level, 0-100. Values above 100 are clamped.
 *
 * Takes effect on the next 5 ms chunk, which is what separates this from
 * every other setting on a node: roles and the channel profile apply at
 * reboot, and a volume control that needed a reboot would not be a volume
 * control. Persisting it is the caller's business — this changes what is
 * coming out of the speaker, nothing more.
 *
 * Safe from any task, and safe before the DAC has started.
 */
void oal_playout_set_volume(uint8_t percent);

/** The level last set, 0-100. */
uint8_t oal_playout_volume(void);

/**
 * Hands the playout a room correction (docs/ROOM-CALIBRATION.md).
 *
 * One vector per output, because a correction belongs to a loudspeaker and
 * a stereo node drives two of them standing in different corners.
 *
 * No headroom is passed: it is a function of the filters, so the playout
 * works it out from them (oal_eq_headroom). A separate figure could only
 * ever be a second opinion about the same arithmetic, and the moment
 * somebody edits a vector by hand it would be the wrong one.
 *
 * Staged rather than applied. The filters belong to the playout task -- a
 * biquad's state is the last two samples it saw, and swapping coefficients
 * underneath a half-filtered chunk rings for as long as the filter decays
 * -- so this leaves a request and the task adopts it between chunks.
 *
 * @param enabled whether to run it. The vectors are kept either way, so
 *                corrected and uncorrected can be compared by ear without
 *                losing the profile to get back to.
 */
void oal_playout_set_eq(const oal_eq_curve_t *left, const oal_eq_curve_t *right,
                        bool enabled);

/**
 * How much output the running correction is giving up, in dB, negative.
 *
 * Zero when the correction is off, because it is not taking anything then —
 * which is what makes an on/off comparison a comparison of tone rather than
 * of loudness.
 */
float oal_playout_eq_headroom_db(void);

/**
 * The largest target this node's ring will honour, in milliseconds, or 0
 * before the output stage has started.
 *
 * Three quarters of the ring: something above the target has to stay free
 * to absorb a burst, and a target equal to capacity means the ring is full
 * whenever it is working.
 *
 * A function rather than a constant, and that is the important part. This
 * was `#define OAL_PLAYOUT_MAX_TARGET_MS 150` with a static assertion tying
 * it to a fixed array -- the right shape for a fixed ring, impossible for a
 * settable one. The assertion existed because the Hub once offered 0-200 ms
 * in a dialog against a real ceiling of 50, and the lesson it encoded still
 * applies: one limit, one source. Everything that needs it -- the delay
 * clamp, `/status`, the Hub's dialog -- asks here rather than knowing a
 * number, so a node with a 1000 ms ring reports 750 without anyone editing
 * a second place.
 */
uint32_t oal_playout_max_target_ms(void);

/** The ring's size in milliseconds, as allocated, or 0 before start. */
uint32_t oal_playout_ring_ms(void);

/**
 * Changes the playout target while running, in milliseconds.
 *
 * Two speakers playing one stream through different output stages do not
 * come out together. A USB dongle carries the host driver's ring, 1 ms USB
 * frames and its own internal buffering on top of the playout; an I²S DAC
 * carries four DMA descriptors. The difference is tens of milliseconds and
 * it is obvious the moment both play in one room.
 *
 * Only ever *added*, and always to the early node: nothing can play a
 * sample before it arrives, so alignment means holding the fast one back
 * to meet the slow one.
 *
 * Takes effect gradually -- the pad/trim servo walks the ring to the new
 * depth at a tenth of a percent, about a second of delay per thirty
 * seconds of music, under two cents of pitch error. Deliberate: this is
 * tuned by ear, and a value that jumped would have to be re-judged after
 * every nudge.
 *
 * Capped at OAL_PLAYOUT_MAX_TARGET_MS.
 */
esp_err_t oal_playout_set_target_ms(uint32_t target_ms);

/** True once the I²S peripheral is up. */
bool oal_playout_running(void);

/**
 * Whether the output stage can currently take samples.
 *
 * Always true for I²S once started — the pins exist whether or not a DAC
 * is soldered to them. Meaningful for USB, where it is false until a
 * dongle has been plugged in and its stream opened. A node reporting
 * running-but-not-ready is receiving audio and playing none of it, which
 * is worth being able to see from the Hub rather than from a speaker.
 */
bool oal_playout_output_ready(void);

/**
 * What the output stage was found holding, or NULL when it has nothing to
 * report. See `oal_sink_t::arrived_as` — this is how a node with no
 * console explains a silent speaker.
 */
const char *oal_playout_output_arrived_as(void);

#ifdef __cplusplus
}
#endif
