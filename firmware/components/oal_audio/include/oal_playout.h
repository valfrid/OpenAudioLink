#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "esp_err.h"
#include "oal_channel.h"
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
    uint32_t trimmed_frames;  /* single frames dropped to walk the fill back down */
    uint32_t padded_frames;   /* single frames repeated to walk it back up */
    uint32_t write_errors;    /* the I²S driver refused a write */

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
 *
 * Safe to call before the DAC has been started; it does nothing.
 */
void oal_playout_submit(uint8_t *payload, size_t frames);

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
