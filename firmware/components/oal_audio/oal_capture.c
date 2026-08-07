#include "oal_capture.h"

#include <inttypes.h>
#include <stdio.h>
#include <string.h>

#include "driver/i2s_std.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "oal_pcm.h"
#include "oal_rtp.h"

static const char *TAG = "oal_capture";

#define CHUNK_FRAMES   OAL_RTP_FRAMES_PER_PACKET
#define CHUNK_SAMPLES  (CHUNK_FRAMES * OAL_RTP_CHANNELS)

/*
 * 40 ms of ring, which is deliberately far smaller than the playout side's
 * 200 ms.
 *
 * They are sized against opposite things. A playout buffer covers the
 * largest gap a *sender* can leave, and a PC can leave a long one. This
 * covers only the jitter of a producer task on the same chip, sending on a
 * radio it also owns — tens of milliseconds at worst. Making it bigger
 * would not add safety, it would add latency: every frame sitting here is
 * delay between the needle and the speaker, and unlike playout delay there
 * is nothing on the other end willing to absorb it.
 */
#define CAPACITY_PACKETS 8
#define CAPACITY_SAMPLES (CAPACITY_PACKETS * CHUNK_SAMPLES)

/* Reads of one packet each, so the read and the send are the same size. */
#define DMA_DESCRIPTORS 4

#define TRACE_INTERVAL_US 5000000

static i2s_chan_handle_t s_rx;
static SemaphoreHandle_t s_lock;

static int32_t s_ring[CAPACITY_SAMPLES];
static size_t s_read;
static size_t s_write;
static size_t s_available;

static oal_capture_state_t s_state;

/* Only the capture task converts into this, and only under the lock. */
static int32_t s_scratch[CHUNK_SAMPLES];

static uint64_t s_trace_at_us;
static uint64_t s_trace_captured;
static size_t s_fill_min;
static size_t s_fill_max;

/*
 * The level meter.
 *
 * Its window is far shorter than the trace's five seconds, because this is
 * the one number somebody watches while moving a cable: five seconds of lag
 * between plugging something in and seeing it turns a working meter into an
 * apparently broken one. Half a second is fast enough to follow a hand and
 * slow enough that a phone polling every few seconds never catches a gap
 * between records and reports silence.
 */
#define METER_WINDOW_US 500000

static int32_t s_peak_left;
static int32_t s_peak_right;
static uint64_t s_meter_at_us;

/*
 * Whether anything has ever taken a packet from this ring.
 *
 * Idle capture fills the ring and then discards its oldest frames forever,
 * which is correct — live audio should stay current — but it makes the
 * dropped counter climb by a second every second from boot. Read without
 * this flag, a node that has simply never been asked to stream looks like
 * one that is losing all of its audio.
 */
static bool s_ever_read;

bool oal_capture_running(void)
{
    return s_state.running;
}

void oal_capture_get(oal_capture_state_t *out)
{
    if (out == NULL) {
        return;
    }
    if (s_lock != NULL && xSemaphoreTake(s_lock, pdMS_TO_TICKS(20)) == pdTRUE) {
        s_state.buffered_frames = (uint32_t)(s_available / OAL_RTP_CHANNELS);
        xSemaphoreGive(s_lock);
    }
    *out = s_state;
}

size_t oal_capture_read(uint8_t *payload, size_t frames)
{
    if (payload == NULL || frames == 0) {
        return 0;
    }

    size_t samples = frames * OAL_RTP_CHANNELS;
    if (samples > CHUNK_SAMPLES) {
        samples = CHUNK_SAMPLES;
    }

    if (!s_state.running || s_lock == NULL
        || xSemaphoreTake(s_lock, pdMS_TO_TICKS(20)) != pdTRUE) {
        memset(payload, 0, frames * OAL_RTP_CHANNELS * OAL_RTP_BYTES_PER_SAMPLE);
        return 0;
    }

    s_ever_read = true;
    size_t copied = s_available < samples ? s_available : samples;

    if (s_available < s_fill_min) {
        s_fill_min = s_available;
    }

    /*
     * Converted straight out of the ring, one wrap at a time. The ring
     * holds I²S words and the wire wants L24, and doing that here rather
     * than on the way in keeps the capture task — the one with a hardware
     * deadline — doing as little as possible.
     */
    size_t first = CAPACITY_SAMPLES - s_read;
    if (first > copied) {
        first = copied;
    }
    oal_pcm_i2s_to_l24(&s_ring[s_read], payload, first);
    if (first < copied) {
        oal_pcm_i2s_to_l24(&s_ring[0], payload + first * OAL_RTP_BYTES_PER_SAMPLE,
                           copied - first);
    }
    s_read = (s_read + copied) % CAPACITY_SAMPLES;
    s_available -= copied;

    if (copied < samples) {
        /*
         * Silence rather than a short packet. Every packet on this profile
         * is the same size, and a receiver that has to handle two lengths
         * is a receiver with a second code path nobody exercises.
         */
        memset(payload + copied * OAL_RTP_BYTES_PER_SAMPLE, 0,
               (samples - copied) * OAL_RTP_BYTES_PER_SAMPLE);
        s_state.silence_frames += (uint32_t)((samples - copied) / OAL_RTP_CHANNELS);
        s_state.underruns++;
    }

    xSemaphoreGive(s_lock);
    return copied / OAL_RTP_CHANNELS;
}

/**
 * Reports what the input is doing, on the same five-second cadence and for
 * the same reason as the playout trace: a rate and a fill together say
 * which end is wrong, where either alone only says that something is.
 */
static void trace(void)
{
    uint64_t now = (uint64_t)esp_timer_get_time();
    if (now - s_trace_at_us < TRACE_INTERVAL_US) {
        return;
    }

    size_t fill_min, fill_max, fill_now;
    if (xSemaphoreTake(s_lock, pdMS_TO_TICKS(20)) != pdTRUE) {
        return;
    }
    fill_min = s_fill_min > CAPACITY_SAMPLES ? s_available : s_fill_min;
    fill_max = s_fill_max;
    fill_now = s_available;
    s_fill_min = CAPACITY_SAMPLES + 1;
    s_fill_max = 0;
    xSemaphoreGive(s_lock);

    uint64_t elapsed_us = now - s_trace_at_us;
    uint64_t captured = s_state.frames_captured - s_trace_captured;
    s_trace_at_us = now;
    s_trace_captured = s_state.frames_captured;

    uint32_t in_hz = (uint32_t)(captured * 1000000ULL / elapsed_us);
    s_state.measured_hz = in_hz;

    /*
     * With a self-clocked ADC the rate is the board's to choose, and the
     * same 24.576 MHz oscillator gives 48 kHz or 96 kHz depending on how
     * the module is strapped. Sending 96 kHz frames down a 48 kHz profile
     * plays back at half speed, which is obvious once heard and baffling
     * until the number is in front of you. Five per cent of tolerance,
     * because the two clocks are unrelated and a short window is coarse.
     */
    if (in_hz > OAL_RTP_SAMPLE_RATE + OAL_RTP_SAMPLE_RATE / 20
        || in_hz < OAL_RTP_SAMPLE_RATE - OAL_RTP_SAMPLE_RATE / 20) {
        ESP_LOGW(TAG, "ADC is running at about %" PRIu32 " Hz, not %d — check the module's "
                      "rate strapping, or this stream plays at the wrong speed",
                 in_hz, OAL_RTP_SAMPLE_RATE);
    }

    /*
     * The level first, because it is the answer to the only question this
     * trace gets asked before a stream is running. Everything after it
     * describes the buffer, and the buffer looks identical whether or not
     * a cable is plugged in.
     */
    char level[48];
    if (s_state.peak_left_dbfs <= OAL_DBFS_SILENT
        && s_state.peak_right_dbfs <= OAL_DBFS_SILENT) {
        snprintf(level, sizeof(level), "level SILENT — nothing on the input");
    } else {
        snprintf(level, sizeof(level), "level L %+d R %+d dBFS",
                 s_state.peak_left_dbfs, s_state.peak_right_dbfs);
    }

    if (!s_ever_read) {
        /*
         * Idle. The ring fills and then discards its oldest frames forever,
         * which is right, but reporting that as ten minutes of dropped
         * audio reads as a fault rather than as nothing having asked for it
         * yet.
         */
        ESP_LOGI(TAG, "input idle, %s, %" PRIu32 " Hz, read errors %" PRIu32
                      " — no stream is taking it",
                 level, in_hz, s_state.read_errors);
        return;
    }

    ESP_LOGI(TAG,
             "input %u/%u/%u ms (min/now/max), %s, %" PRIu32 " Hz, dropped %" PRIu32
             " ms, silence %" PRIu32 " ms, underruns %" PRIu32 ", read errors %" PRIu32,
             (unsigned)(fill_min / OAL_RTP_CHANNELS * 1000 / OAL_RTP_SAMPLE_RATE),
             (unsigned)(fill_now / OAL_RTP_CHANNELS * 1000 / OAL_RTP_SAMPLE_RATE),
             (unsigned)(fill_max / OAL_RTP_CHANNELS * 1000 / OAL_RTP_SAMPLE_RATE),
             level, in_hz,
             s_state.dropped_frames / (OAL_RTP_SAMPLE_RATE / 1000),
             s_state.silence_frames / (OAL_RTP_SAMPLE_RATE / 1000),
             s_state.underruns, s_state.read_errors);
}

static void capture_task(void *arg)
{
    (void)arg;

    for (;;) {
        size_t got = 0;
        esp_err_t err = i2s_channel_read(
            s_rx, s_scratch, sizeof(s_scratch), &got, pdMS_TO_TICKS(200));
        if (err != ESP_OK || got == 0) {
            s_state.read_errors++;
            continue;
        }

        size_t samples = got / sizeof(int32_t);

        /*
         * Metered here, on the way past, rather than anywhere downstream.
         * This is the only point that sees every sample the ADC produced:
         * the ring drops its oldest frames while nothing is streaming, so
         * a meter reading the ring would go quiet exactly when nobody is
         * listening — which is when somebody is most likely to be standing
         * at the turntable wondering whether it is wired correctly.
         */
        int32_t left = oal_pcm_peak(s_scratch, samples, 0);
        int32_t right = oal_pcm_peak(s_scratch, samples, 1);
        if (left > s_peak_left) {
            s_peak_left = left;
        }
        if (right > s_peak_right) {
            s_peak_right = right;
        }

        uint64_t now_us = (uint64_t)esp_timer_get_time();
        if (now_us - s_meter_at_us >= METER_WINDOW_US) {
            s_state.peak_left_dbfs = oal_pcm_dbfs(s_peak_left);
            s_state.peak_right_dbfs = oal_pcm_dbfs(s_peak_right);
            s_peak_left = 0;
            s_peak_right = 0;
            s_meter_at_us = now_us;
        }

        if (xSemaphoreTake(s_lock, pdMS_TO_TICKS(20)) != pdTRUE) {
            s_state.dropped_frames += (uint32_t)(samples / OAL_RTP_CHANNELS);
            continue;
        }

        /*
         * A full ring drops its oldest frames, matching the playout side.
         * Live audio should stay current: the newest samples are the ones
         * the room is about to hear, and holding them back to preserve
         * older ones only makes the delay permanent.
         */
        if (s_available + samples > CAPACITY_SAMPLES) {
            size_t overflow = s_available + samples - CAPACITY_SAMPLES;
            s_read = (s_read + overflow) % CAPACITY_SAMPLES;
            s_available -= overflow;
            s_state.dropped_frames += (uint32_t)(overflow / OAL_RTP_CHANNELS);
        }

        size_t first = CAPACITY_SAMPLES - s_write;
        if (first > samples) {
            first = samples;
        }
        memcpy(&s_ring[s_write], s_scratch, first * sizeof(int32_t));
        if (first < samples) {
            memcpy(&s_ring[0], &s_scratch[first], (samples - first) * sizeof(int32_t));
        }
        s_write = (s_write + samples) % CAPACITY_SAMPLES;
        s_available += samples;
        s_state.frames_captured += samples / OAL_RTP_CHANNELS;

        if (s_available > s_fill_max) {
            s_fill_max = s_available;
        }

        xSemaphoreGive(s_lock);

        trace();
    }
}

esp_err_t oal_capture_start(const oal_capture_config_t *config)
{
    if (config == NULL) {
        return ESP_ERR_INVALID_ARG;
    }
    if (s_state.running) {
        return ESP_OK;
    }

    uint32_t rate = config->sample_rate ? config->sample_rate : OAL_RTP_SAMPLE_RATE;

    s_lock = xSemaphoreCreateMutex();
    if (s_lock == NULL) {
        return ESP_ERR_NO_MEM;
    }

    i2s_chan_config_t channel_config = I2S_CHANNEL_DEFAULT_CONFIG(
        I2S_NUM_AUTO, config->slave ? I2S_ROLE_SLAVE : I2S_ROLE_MASTER);
    channel_config.dma_desc_num = DMA_DESCRIPTORS;
    channel_config.dma_frame_num = CHUNK_FRAMES;

    esp_err_t err = i2s_new_channel(&channel_config, NULL, &s_rx);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "i2s_new_channel failed: %s", esp_err_to_name(err));
        return err;
    }

    i2s_std_config_t std_config = {
        .clk_cfg = I2S_STD_CLK_DEFAULT_CONFIG(rate),
        /* 32-bit slots holding the PCM1808's 24 bits, left-justified —
         * the same arrangement the playout uses, so one conversion pair
         * serves both directions. */
        .slot_cfg = I2S_STD_PHILIPS_SLOT_DEFAULT_CONFIG(
            I2S_DATA_BIT_WIDTH_32BIT, I2S_SLOT_MODE_STEREO),
        .gpio_cfg = {
            /* Only when this end is master. A module with its own
             * oscillator drives MCLK as an output, and connecting two
             * drivers to one line is how boards get damaged. */
            .mclk = config->slave ? I2S_GPIO_UNUSED : config->mclk_gpio,
            .bclk = config->bclk_gpio,
            .ws   = config->ws_gpio,
            .dout = I2S_GPIO_UNUSED,
            .din  = config->din_gpio,
            .invert_flags = {
                .mclk_inv = false,
                .bclk_inv = false,
                .ws_inv   = false,
            },
        },
    };

    err = i2s_channel_init_std_mode(s_rx, &std_config);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "i2s_channel_init_std_mode failed: %s", esp_err_to_name(err));
        i2s_del_channel(s_rx);
        s_rx = NULL;
        return err;
    }

    err = i2s_channel_enable(s_rx);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "i2s_channel_enable failed: %s", esp_err_to_name(err));
        i2s_del_channel(s_rx);
        s_rx = NULL;
        return err;
    }

    s_fill_min = CAPACITY_SAMPLES + 1;
    s_trace_at_us = (uint64_t)esp_timer_get_time();
    s_meter_at_us = s_trace_at_us;
    /* Silent until measured. Zero would mean full scale. */
    s_state.peak_left_dbfs = OAL_DBFS_SILENT;
    s_state.peak_right_dbfs = OAL_DBFS_SILENT;
    s_state.running = true;
    s_state.capacity_frames = CAPACITY_SAMPLES / OAL_RTP_CHANNELS;

    /*
     * Above the producer that drains it, below nothing else. Missing an
     * I²S read loses samples that no longer exist anywhere; a packet sent
     * a moment late is still the right audio.
     */
    if (xTaskCreate(capture_task, "oal_capture", 4096, NULL, 7, NULL) != pdPASS) {
        i2s_channel_disable(s_rx);
        i2s_del_channel(s_rx);
        s_rx = NULL;
        s_state.running = false;
        return ESP_ERR_NO_MEM;
    }

    if (config->slave) {
        ESP_LOGI(TAG, "I2S in on BCLK=%d WS=%d DIN=%d, slave — the ADC sets the rate",
                 config->bclk_gpio, config->ws_gpio, config->din_gpio);
    } else {
        ESP_LOGI(TAG, "I2S in on BCLK=%d WS=%d DIN=%d MCLK=%d, master at %" PRIu32 " Hz",
                 config->bclk_gpio, config->ws_gpio, config->din_gpio,
                 config->mclk_gpio, rate);
    }
    return ESP_OK;
}
