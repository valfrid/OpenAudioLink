#include "oal_playout.h"

#include <inttypes.h>
#include <string.h>

#include "driver/i2s_std.h"
#include "esp_log.h"
#include "esp_timer.h"
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
 * 160 ms of ring, which is not sized by the network's jitter but by the
 * sender's cadence.
 *
 * The first hardware test made the distinction obvious. A Windows sender
 * cannot wake every 5 ms: the system timer runs at 15.6 ms unless a
 * process asks for better, so packets leave in clumps of three with a
 * ~15.6 ms gap between clumps. Measured Wi-Fi jitter of 1-2 ms sits on top
 * of that. Against the original 20 ms of ring the gap alone consumed most
 * of the buffer, the ring reached zero several times a second, and every
 * time it did the playout re-primed — which is what a listener hears.
 *
 * So the ring has to cover the largest gap a sender can leave, not the
 * jitter a good network adds. 160 ms holds a 60 ms default target with
 * 100 ms above it for bursts.
 */
#define CAPACITY_PACKETS 32
#define CAPACITY_SAMPLES (CAPACITY_PACKETS * CHUNK_SAMPLES)

/*
 * How long the ring may stay empty before playout treats the stream as
 * finished rather than stumbling. 200 ms: far longer than any gap a
 * working sender leaves, far shorter than a listener would call a pause.
 */
#define STARVED_CHUNKS (200 / OAL_RTP_PTIME_MS)

/*
 * DMA holds 20 ms on top of the ring. It is part of the latency and worth
 * stating rather than discovering: with the default 60 ms target, a sample
 * spends about 80 ms between arriving and being heard.
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
static uint32_t s_starved_chunks;
static uint32_t s_reported_drop_seconds;

/*
 * A periodic trace, because the counters alone cannot tell a buffer that
 * swings from one that drifts. Both show as silence and drops; only their
 * shape over time separates them, and only the two rates side by side say
 * which end is wrong.
 *
 * The two rates are the point. Frames arriving per second is the sender's
 * idea of 48 kHz; frames written to the DAC per second is this board's.
 * Everything about a playout buffer follows from their difference, and
 * until they are both measured every explanation for a dropout is a
 * guess.
 */
#define TRACE_INTERVAL_US 5000000

static uint64_t s_trace_at_us;
static uint64_t s_trace_played;
static uint64_t s_submitted_frames;
static uint64_t s_trace_submitted;
static size_t s_fill_min;
static size_t s_fill_max;

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

        /*
         * One line per second of audio lost. Overflow is the quiet
         * failure of the pair — starving announces itself by re-priming,
         * while a ring trimmed on every burst just sounds slightly wrong
         * — and on the first hardware test it was only visible by asking
         * the node for its counters. Rate-limited by the amount lost
         * rather than by time, so a steady trickle stays legible and a
         * flood does not fill the log.
         */
        uint32_t lost_seconds = s_state.dropped_frames / OAL_RTP_SAMPLE_RATE;
        if (lost_seconds != s_reported_drop_seconds) {
            s_reported_drop_seconds = lost_seconds;
            ESP_LOGW(TAG, "ring full, oldest frames dropped; %" PRIu32
                          " s of audio lost so far", lost_seconds);
        }
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
    s_submitted_frames += frames;

    if (s_available > s_fill_max) {
        s_fill_max = s_available;
    }

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
            s_starved_chunks = 0;
            /*
             * Warn rather than inform once this has happened before. A
             * first prime is the stream starting; a later one means the
             * ring ran dry, and the count is the number the listener
             * heard. Logging both the same way hid exactly that.
             */
            if (s_state.underruns == 0) {
                ESP_LOGI(TAG, "primed with %u frames; playing",
                         (unsigned)(s_available / OAL_RTP_CHANNELS));
            } else {
                ESP_LOGW(TAG, "re-primed with %u frames after underrun %u",
                         (unsigned)(s_available / OAL_RTP_CHANNELS),
                         (unsigned)s_state.underruns);
            }
        } else {
            xSemaphoreGive(s_lock);
            memset(chunk, 0, CHUNK_SAMPLES * sizeof(int32_t));
            return 0;
        }
    }

    if (s_available == 0) {
        /*
         * The ring ran dry. One empty chunk does not say why: a sender
         * that leaves a gap, a Wi-Fi retry and a stream that ended all
         * look identical for the first 5 ms.
         *
         * So play silence and wait. Only sustained emptiness means the
         * music stopped, and going unprimed on the first empty chunk was
         * the bug behind the very first hardware test's dropouts — a 5 ms
         * gap in arrival became a whole target's worth of silence while
         * the ring refilled, several times a second.
         *
         * Re-priming is still the right recovery when the stream really
         * has stopped, because sender and DAC run at the same average
         * rate: once the margin is spent, only not playing rebuilds it.
         */
        s_state.silence_frames += CHUNK_FRAMES;
        if (s_starved_chunks++ == 0) {
            s_state.underruns++;
        }
        if (s_starved_chunks >= STARVED_CHUNKS) {
            s_primed = false;
            s_starved_chunks = 0;
        }
        xSemaphoreGive(s_lock);
        memset(chunk, 0, CHUNK_SAMPLES * sizeof(int32_t));
        return 0;
    }

    s_starved_chunks = 0;

    if (s_available < s_fill_min) {
        s_fill_min = s_available;
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

/**
 * Reports the buffer's shape and both clocks, every TRACE_INTERVAL_US.
 *
 * Read `in` against `out`: equal means the buffer is only absorbing
 * jitter, and any silence or drops came from bursts. Different means one
 * clock is wrong relative to the other, the difference in ppm says by how
 * much, and the sign says which way the ring will eventually fail — up
 * into drops, down into silence.
 *
 * `min` and `max` are the swing across the interval. A ring that lives
 * near its target with a wide swing has a bursty sender; one that walks
 * steadily from target to an edge has a rate mismatch. The counters alone
 * cannot tell those apart, which is what made the first hardware test
 * slow to diagnose.
 */
static void trace(void)
{
    uint64_t now = (uint64_t)esp_timer_get_time();
    if (now - s_trace_at_us < TRACE_INTERVAL_US) {
        return;
    }

    size_t fill_min, fill_max, fill_now;
    uint64_t submitted;
    if (xSemaphoreTake(s_lock, pdMS_TO_TICKS(20)) != pdTRUE) {
        return;
    }
    fill_min = s_fill_min > CAPACITY_SAMPLES ? s_available : s_fill_min;
    fill_max = s_fill_max;
    fill_now = s_available;
    submitted = s_submitted_frames;
    s_fill_min = CAPACITY_SAMPLES + 1; /* re-armed above any real fill */
    s_fill_max = 0;
    xSemaphoreGive(s_lock);

    uint64_t elapsed_us = now - s_trace_at_us;
    uint64_t played = s_state.frames_played - s_trace_played;
    uint64_t arrived = submitted - s_trace_submitted;

    s_trace_at_us = now;
    s_trace_played = s_state.frames_played;
    s_trace_submitted = submitted;

    uint32_t in_hz = (uint32_t)(arrived * 1000000ULL / elapsed_us);
    uint32_t out_hz = (uint32_t)(played * 1000000ULL / elapsed_us);
    int32_t ppm = out_hz ? (int32_t)(((int64_t)in_hz - out_hz) * 1000000 / out_hz) : 0;

    ESP_LOGI(TAG,
             "buffer %u/%u/%u ms (min/now/max), in %" PRIu32 " Hz, out %" PRIu32
             " Hz, %+" PRId32 " ppm, underruns %" PRIu32 ", dropped %" PRIu32
             " ms, silence %" PRIu32 " ms",
             (unsigned)(fill_min / OAL_RTP_CHANNELS * 1000 / OAL_RTP_SAMPLE_RATE),
             (unsigned)(fill_now / OAL_RTP_CHANNELS * 1000 / OAL_RTP_SAMPLE_RATE),
             (unsigned)(fill_max / OAL_RTP_CHANNELS * 1000 / OAL_RTP_SAMPLE_RATE),
             in_hz, out_hz, ppm, s_state.underruns,
             s_state.dropped_frames / (OAL_RTP_SAMPLE_RATE / 1000),
             s_state.silence_frames / (OAL_RTP_SAMPLE_RATE / 1000));
}

static void playout_task(void *arg)
{
    (void)arg;
    static int32_t chunk[CHUNK_SAMPLES];

    for (;;) {
        take_chunk(chunk);

        /*
         * Write the whole chunk, not as much of it as the driver felt
         * like taking. A short write returns ESP_OK, and treating it as
         * done silently discarded the tail: samples that had already been
         * removed from the ring, so nothing counted them and the DAC
         * appeared to be consuming faster than it plays. That is
         * indistinguishable from a fast clock, which is exactly the
         * question this file is currently being asked.
         */
        size_t offset = 0;
        while (offset < sizeof(chunk)) {
            size_t written = 0;
            esp_err_t err = i2s_channel_write(
                s_tx, (const uint8_t *)chunk + offset, sizeof(chunk) - offset,
                &written, pdMS_TO_TICKS(200));
            if (err != ESP_OK) {
                s_state.write_errors++;
                break;
            }
            if (written == 0) {
                s_state.write_errors++;
                break;
            }
            offset += written;
            s_state.frames_played += written / (OAL_RTP_CHANNELS * sizeof(int32_t));
        }

        trace();
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
    uint32_t target_ms = config->target_ms ? config->target_ms : 60;

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

    s_fill_min = CAPACITY_SAMPLES + 1;
    s_trace_at_us = (uint64_t)esp_timer_get_time();

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
