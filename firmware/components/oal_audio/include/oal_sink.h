#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "esp_err.h"
#include "oal_output.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Where playout's samples finally go.
 *
 * `oal_playout.c` is a ring buffer, a state machine, a gain stage and a
 * drift story. None of that cares whether the last step is an I²S
 * peripheral or a USB DAC the node hosts, and until now it was welded to
 * the first: three call sites, and the rest of the file backend-agnostic
 * without anybody having said so.
 *
 * This is the seam. It is decision 8's claim made structural — how a
 * Consumer emits audio is a property of the board — and it is also the
 * separation `ROADMAP.md` already wanted for a different reason: the
 * playout state machine has no host test, and it cannot get one while it
 * calls `i2s_channel_write` directly.
 *
 * Deliberately narrow. Anything a backend needs beyond open, write and
 * close belongs inside that backend, not in an interface every backend has
 * to satisfy.
 */

typedef struct {
    /** Frames per second. The RTP profile's 48 000 unless testing. */
    uint32_t sample_rate;

    /** I²S only, ignored by anything else. */
    int bclk_gpio;
    int ws_gpio;
    int dout_gpio;
} oal_sink_config_t;

typedef struct {
    /** For log lines. "I2S" or "USB". */
    const char *name;

    /**
     * Prepare the hardware. Called once, before the playout task exists.
     *
     * A sink may return ESP_OK without being able to play yet — see
     * `ready` — which is the difference between a soldered DAC and a
     * dongle somebody has not plugged in.
     */
    esp_err_t (*open)(const oal_sink_config_t *config);

    /** Release it. Only called when `open` succeeded. */
    void (*close)(void);

    /**
     * Whether samples written now would reach a converter.
     *
     * I²S is ready the moment it is open: the pins exist whether or not a
     * DAC is soldered to them, and this project cannot tell the difference.
     * USB is not — a device arrives when somebody plugs it in, which may be
     * minutes after boot or never.
     *
     * Playout uses this to decide whether to consume from the ring at all.
     * Draining it into nowhere would count frames as played that no
     * converter saw, and "the DAC is consuming faster than it plays" is
     * exactly the symptom this project spent a session chasing once
     * already.
     */
    bool (*ready)(void);

    /**
     * Write PCM, blocking up to @p timeout_ms.
     *
     * Samples are 32-bit slots carrying 24-bit values left-justified, which
     * is what the ring holds and what both backends want. A short write is
     * legitimate; the caller loops.
     */
    esp_err_t (*write)(const void *data, size_t bytes, size_t *written,
                       uint32_t timeout_ms);
} oal_sink_t;

/** I²S to a soldered DAC. Always available. */
const oal_sink_t *oal_sink_i2s(void);

/**
 * USB Audio Class 2.0 to a dongle this node hosts.
 *
 * Returns NULL when the firmware was built without USB host support, so a
 * caller can fall back rather than fail to link.
 */
const oal_sink_t *oal_sink_usb(void);

/** The sink for a configured output stage, or NULL if it is unavailable. */
const oal_sink_t *oal_sink_for(oal_output_t output);

#ifdef __cplusplus
}
#endif
