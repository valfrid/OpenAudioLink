#include "oal_playout.h"

#include <inttypes.h>
#include <string.h>

#include "driver/i2s_std.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "oal_pcm.h"
#include "oal_rtp.h"

static const char *TAG = "oal_playout";

/* One packet at a time, so the write to the DAC and the packet on the wire
 * are the same size and neither has to be split. */
#define CHUNK_FRAMES   OAL_RTP_FRAMES_PER_PACKET
#define CHUNK_SAMPLES  (CHUNK_FRAMES * OAL_RTP_CHANNELS)

/*
 * 60 ms of ring. Three times the default playout delay, which leaves room
 * for a burst to be absorbed rather than trimmed — the measured loss shape
 * is a few packets at a time, not a steady trickle.
 */
#define CAPACITY_PACKETS 12
#define CAPACITY_SAMPLES (CAPACITY_PACKETS * CHUNK_SAMPLES)

/*
 * DMA holds 20 ms on top of the ring. It is part of the latency and worth
 * stating rather than discovering: with the default 20 ms target, a sample
 * spends about 40 ms between arriving and being heard.
 */
#define DMA_DESCRIPTORS 4

static i2s_chan_handle_t s_tx;
static SemaphoreHandle_t s_lock;
static TaskHandle_t s_task;

static int32_t s_ring[CAPACITY_SAMPLES];
static size_t s_read;
static size_t s_write;
static size_t s_available;
static bool s_primed;

static oal_playout_state_t s_state;
static size_t s_target_samples;

/*
 * Only the consumer task calls oal_playout_submit, so one scratch buffer
 * serves it. Converting straight into the ring would mean doing the wrap
 * arithmetic twice — once for the copy and once for the conversion — and
 * this is 2 KB.
 */
static int32_t s_scratch[CHUNK_SAMPLES];

bool oal_playout_running(void)
{
    return s_state.running;
}

void oal_playout_get(oal_playout_state_t *out)
{
    if (out == NULL) {
        return;
    }
    if (s_lock != NULL && xSemaphoreTake(s_lock, pdMS_TO_TICKS(20)) == pdTRUE) {
        s_state.buffered_frames = (uint32_t)(s_available / OAL_RTP_CHANNELS);
        s_state.playing = s_primed;
        xSemaphoreGive(s_lock);
    }
    *out = s_state;
}

void oal_playout_submit(uint8_t *payload, size_t frames)
{
    if (!s_state.running || payload == NULL || frames == 0 || s_lock == NULL) {
        return;
    }
    if (frames > CHUNK_FRAMES) {
        frames = CHUNK_FRAMES;
    }

    /* Applied before conversion, on the L24 payload the tested code
     * expects. Stereo leaves it untouched. */
    oal_channel_apply(payload, frames, s_state.channel);

    size_t samples = frames * OAL_RTP_CHANNELS;
    oal_pcm_l24_to_i2s(payload, s_scratch, samples);

    if (xSemaphoreTake(s_lock, pdMS_TO_TICKS(20)) != pdTRUE) {
        return;
    }

    /*
     * A full ring drops its oldest frames, not its newest. Live audio
     * should stay current: dropping what is about to be played costs one
     * glitch, while dropping what just arrived costs the same glitch and
     * leaves the delay permanently longer.
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

    xSemaphoreGive(s_lock);
}

/**
 * Fills one chunk from the ring, padding with silence, and reports how
 * many real samples it found.
 */
static size_t take_chunk(int32_t *chunk)
{
    size_t copied = 0;

    if (xSemaphoreTake(s_lock, pdMS_TO_TICKS(20)) != pdTRUE) {
        memset(chunk, 0, CHUNK_SAMPLES * sizeof(int32_t));
        return 0;
    }

    if (!s_primed) {
        /*
         * Still filling. Silence rather than the first few frames: playing
         * them immediately would empty the ring again at once and click
         * through the whole first second.
         */
        if (s_available >= s_target_samples) {
            s_primed = true;
            ESP_LOGI(TAG, "primed with %u frames; playing",
                     (unsigned)(s_available / OAL_RTP_CHANNELS));
        } else {
            xSemaphoreGive(s_lock);
            memset(chunk, 0, CHUNK_SAMPLES * sizeof(int32_t));
            return 0;
        }
    }

    if (s_available == 0) {
        /*
         * Nothing at all, which means the stream stopped rather than
         * stumbled. Going back to unprimed avoids counting a silent
         * evening as millions of underruns, and re-fills properly when the
         * music comes back.
         */
        s_primed = false;
        xSemaphoreGive(s_lock);
        memset(chunk, 0, CHUNK_SAMPLES * sizeof(int32_t));
        return 0;
    }

    copied = s_available < CHUNK_SAMPLES ? s_available : CHUNK_SAMPLES;

    size_t first = CAPACITY_SAMPLES - s_read;
    if (first > copied) {
        first = copied;
    }
    memcpy(chunk, &s_ring[s_read], first * sizeof(int32_t));
    if (first < copied) {
        memcpy(&chunk[first], &s_ring[0], (copied - first) * sizeof(int32_t));
    }
    s_read = (s_read + copied) % CAPACITY_SAMPLES;
    s_available -= copied;

    if (copied < CHUNK_SAMPLES) {
        memset(&chunk[copied], 0, (CHUNK_SAMPLES - copied) * sizeof(int32_t));
        s_state.silence_frames += (uint32_t)((CHUNK_SAMPLES - copied) / OAL_RTP_CHANNELS);
    }

    xSemaphoreGive(s_lock);
    return copied;
}

static void playout_task(void *arg)
{
    (void)arg;
    static int32_t chunk[CHUNK_SAMPLES];

    for (;;) {
        take_chunk(chunk);

        size_t written = 0;
        esp_err_t err = i2s_channel_write(
            s_tx, chunk, sizeof(chunk), &written, pdMS_TO_TICKS(200));
        if (err != ESP_OK) {
            s_state.write_errors++;
            continue;
        }
        s_state.frames_played += written / (OAL_RTP_CHANNELS * sizeof(int32_t));
    }
}

esp_err_t oal_playout_start(const oal_playout_config_t *config)
{
    if (config == NULL) {
        return ESP_ERR_INVALID_ARG;
    }
    if (s_state.running) {
        return ESP_OK;
    }

    uint32_t rate = config->sample_rate ? config->sample_rate : OAL_RTP_SAMPLE_RATE;
    uint32_t target_ms = config->target_ms ? config->target_ms : 20;

    s_target_samples = (size_t)rate * target_ms / 1000 * OAL_RTP_CHANNELS;
    if (s_target_samples > CAPACITY_SAMPLES / 2) {
        /* Leave room to absorb a burst above the target; a target equal to
         * the capacity means the ring is full whenever it is working. */
        s_target_samples = CAPACITY_SAMPLES / 2;
    }

    s_lock = xSemaphoreCreateMutex();
    if (s_lock == NULL) {
        return ESP_ERR_NO_MEM;
    }

    i2s_chan_config_t channel_config = I2S_CHANNEL_DEFAULT_CONFIG(I2S_NUM_AUTO, I2S_ROLE_MASTER);
    channel_config.dma_desc_num = DMA_DESCRIPTORS;
    channel_config.dma_frame_num = CHUNK_FRAMES;
    /* Underrun should be silence, not the last buffer played again — a
     * repeated 5 ms of audio is a buzz, and a recognisable one. */
    channel_config.auto_clear = true;

    esp_err_t err = i2s_new_channel(&channel_config, &s_tx, NULL);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "i2s_new_channel failed: %s", esp_err_to_name(err));
        return err;
    }

    i2s_std_config_t std_config = {
        .clk_cfg = I2S_STD_CLK_DEFAULT_CONFIG(rate),
        /* 32-bit slots carrying 24-bit samples left-justified. The PCM5102A
         * and the MAX98357A both take the leading 24 bits and ignore the
         * padding, so one configuration drives either board. */
        .slot_cfg = I2S_STD_PHILIPS_SLOT_DEFAULT_CONFIG(
            I2S_DATA_BIT_WIDTH_32BIT, I2S_SLOT_MODE_STEREO),
        .gpio_cfg = {
            /* No master clock. The PCM5102A runs its internal PLL from the
             * bit clock when its SCK pin is grounded, and the MAX98357A
             * never wanted one — which is why both are wired with three
             * signals instead of four (docs/HARDWARE.md). */
            .mclk = I2S_GPIO_UNUSED,
            .bclk = config->bclk_gpio,
            .ws   = config->ws_gpio,
            .dout = config->dout_gpio,
            .din  = I2S_GPIO_UNUSED,
            .invert_flags = {
                .mclk_inv = false,
                .bclk_inv = false,
                .ws_inv   = false,
            },
        },
    };

    err = i2s_channel_init_std_mode(s_tx, &std_config);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "i2s_channel_init_std_mode failed: %s", esp_err_to_name(err));
        i2s_del_channel(s_tx);
        s_tx = NULL;
        return err;
    }

    err = i2s_channel_enable(s_tx);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "i2s_channel_enable failed: %s", esp_err_to_name(err));
        i2s_del_channel(s_tx);
        s_tx = NULL;
        return err;
    }

    s_state.running = true;
    s_state.channel = config->channel;
    s_state.target_frames = (uint32_t)(s_target_samples / OAL_RTP_CHANNELS);
    s_state.capacity_frames = CAPACITY_SAMPLES / OAL_RTP_CHANNELS;

    /*
     * Above the consumer. The DAC has a deadline every 5 ms and missing it
     * is audible, while a packet taken from the socket a moment late is
     * not — and the socket has a buffer for exactly that.
     */
    if (xTaskCreate(playout_task, "oal_playout", 4096, NULL, 7, &s_task) != pdPASS) {
        i2s_channel_disable(s_tx);
        i2s_del_channel(s_tx);
        s_tx = NULL;
        s_state.running = false;
        return ESP_ERR_NO_MEM;
    }

    ESP_LOGI(TAG, "I2S out on BCLK=%d WS=%d DOUT=%d, %" PRIu32 " Hz, %s, %" PRIu32 " ms buffer",
             config->bclk_gpio, config->ws_gpio, config->dout_gpio, rate,
             oal_channel_name(config->channel), target_ms);
    return ESP_OK;
}
