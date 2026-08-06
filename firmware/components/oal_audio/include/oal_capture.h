#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Capturing line input through an I²S ADC — the Analog Source (decision 3,
 * ROADMAP phase 4). The mirror image of oal_playout, and it fails in mirror
 * ways: there the DAC consumes forever and the network may be late, here the
 * ADC produces forever and the network may be slow to take it.
 *
 * The same shape answers both. A task reads the peripheral at the rate the
 * hardware sets and puts frames in a ring; the producer takes one packet's
 * worth whenever it sends. A ring that fills means the sender is not
 * keeping up, one that empties means it is asking too often, and both are
 * counted rather than guessed at — which is the single most useful thing
 * the playout path taught this project.
 *
 * Which end owns the clock is a property of the board, not a preference.
 * A bare PCM1808 has no oscillator and must be given one, so the ESP32 is
 * master. The common "ANA TO I2S" modules carry their own 24.576 MHz
 * oscillator and generate BCK and LRCK themselves — their headers expose
 * MCLK as an *output* — so the ESP32 has to follow.
 *
 * Both are supported and slave is the default, because that is the board
 * this was first built against. Getting it wrong is not subtle: two
 * masters driving one clock line produce nothing usable, and the symptom
 * is silence.
 *
 * Being the slave costs the thing decision 12 wanted from being master —
 * capture and playback on one clock — so a node doing both from a
 * self-clocked ADC has two clock domains inside it. That is a real cost
 * and it is the board's to impose, not ours to choose.
 */

typedef struct {
    int bclk_gpio;
    int ws_gpio;
    int din_gpio;

    /**
     * Master clock out to the ADC's SCKI, used only when this end is
     * master: a bare PCM1808 has no oscillator and stays silent without
     * one. Ignored, and best left unwired, when the module clocks itself.
     */
    int mclk_gpio;

    /**
     * True when the ADC generates BCK and LRCK and this end follows.
     *
     * The module's own oscillator then sets the sample rate, and it is
     * whatever that crystal divides to rather than whatever was asked
     * for — 24.576 MHz gives 48 kHz at 512fs and 96 kHz at 256fs, and the
     * strapping decides which. The capture trace measures what actually
     * arrives, which is the only trustworthy answer.
     */
    bool slave;

    /** Frames per second. The RTP profile's 48 000 unless testing. */
    uint32_t sample_rate;
} oal_capture_config_t;

typedef struct {
    bool running;
    uint32_t buffered_frames;
    uint32_t capacity_frames;
    uint64_t frames_captured;
    uint32_t dropped_frames;  /* the ring was full: the sender fell behind */
    uint32_t silence_frames;  /* the ring was empty when a packet was due */
    uint32_t underruns;       /* times it was empty, however long each lasted */
    uint32_t read_errors;     /* the I²S driver refused a read */
} oal_capture_state_t;

/**
 * Brings up the I²S input and starts reading.
 *
 * Reading starts immediately and never stops, whether or not anything is
 * streaming. An ADC that is started when a stream starts would deliver its
 * first packet from a peripheral that has not settled, and the settling is
 * audible.
 */
esp_err_t oal_capture_start(const oal_capture_config_t *config);

bool oal_capture_running(void);

/**
 * Fills one packet's payload with big-endian L24, padding with silence if
 * the ring is short.
 *
 * Signature matches oal_stream_source_t so it can be registered directly
 * with the producer, which keeps oal_rtp knowing nothing about ADCs — the
 * same seam, and the same reason, as the consumer's sink.
 *
 * @return frames of real audio, which is less than requested only when the
 *         ring ran dry. The rest of the payload is silence either way.
 */
size_t oal_capture_read(uint8_t *payload, size_t frames);

void oal_capture_get(oal_capture_state_t *out);

#ifdef __cplusplus
}
#endif
