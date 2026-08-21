#include "oal_playout.h"

#include <inttypes.h>
#include <string.h>

#include "oal_sink.h"
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
 * 200 ms of ring, which is not sized by the network's jitter but by the
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
 * jitter a good network adds.
 *
 * 200 ms, holding a 100 ms default target with 100 ms above it. The
 * second figure came from the same hardware once the sender was fixed:
 * with delivery smooth the ring settled around its target and swung about
 * 35 ms either way, but occasional gaps of 60 to 70 ms still emptied a
 * 60 ms target completely. A target has to be larger than the worst gap,
 * not the average one, and 15 KB of RAM is a cheap way to stop caring
 * about the difference.
 */
#define CAPACITY_PACKETS 40
#define CAPACITY_SAMPLES (CAPACITY_PACKETS * CHUNK_SAMPLES)

/*
 * How long the ring may stay empty before playout treats the stream as
 * finished rather than stumbling. 200 ms: far longer than any gap a
 * working sender leaves, far shorter than a listener would call a pause.
 */
#define STARVED_CHUNKS (200 / OAL_RTP_PTIME_MS)

/*
 * The sink holds about 20 ms on top of the ring — four I2S DMA descriptors,
 * or the USB driver's own buffer. It is part of the latency and worth
 * stating rather than discovering: with the default 100 ms target, a sample
 * spends about 120 ms between arriving and being heard.
 */

static const oal_sink_t *s_sink;
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

/* Margin found by arriving packets: the smallest this window, and the
 * threshold under which a packet is close enough to its deadline to be
 * worth counting. */
static size_t s_margin_min;
static size_t s_tight_below;

static oal_playout_state_t s_state;
static size_t s_target_samples;

/*
 * The Q16 gain the playout task multiplies by, and the percentage it came
 * from. Plain integers rather than anything guarded: the writer is a
 * control-server task and the reader is the playout task, a 32-bit aligned
 * store on this CPU is atomic, and the worst a race can do is apply the old
 * gain to one more 5 ms chunk. Taking the ring's lock to change the volume
 * would put an HTTP request in the path of the audio, which is a much worse
 * trade than a chunk of latency nobody can hear.
 */
static volatile int32_t s_gain_q16 = OAL_GAIN_UNITY;
static volatile uint8_t s_volume = OAL_VOLUME_DEFAULT;

/*
 * Above this the playout trims. Far enough above the target that ordinary
 * jitter never reaches it, far enough below the capacity to leave a burst
 * somewhere to go.
 */
static size_t s_trim_above;
static size_t s_pad_below;
static uint32_t s_pad_phase;

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

bool oal_playout_output_ready(void)
{
    return s_sink != NULL && s_sink->ready();
}

const char *oal_playout_output_arrived_as(void)
{
    if (s_sink == NULL || s_sink->arrived_as == NULL) {
        return NULL;
    }
    return s_sink->arrived_as();
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
    s_state.volume = s_volume;
    *out = s_state;
}

void oal_playout_set_volume(uint8_t percent)
{
    if (percent > 100) {
        percent = 100;
    }
    /* Gain first, then the percentage. The other order leaves a window in
     * which the reported volume is the new one and the sound is still the
     * old one, which is exactly the report a person would use to decide
     * the control is broken. */
    s_gain_q16 = oal_pcm_gain_q16(percent);
    s_volume = percent;
    ESP_LOGI(TAG, "volume %u%%", (unsigned)percent);
}

uint8_t oal_playout_volume(void)
{
    return s_volume;
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
     * Was this packet in time?
     *
     * The ring answers it exactly, with no clock comparison and no
     * timestamp arithmetic: whatever is already queued in front of this
     * payload is what the speaker will play before reaching it, so the
     * fill at this instant *is* this packet's margin. 4800 frames means it
     * arrived 100 ms early. Zero means it arrived to find the speaker
     * already playing silence, and no buffer anywhere can un-play that.
     *
     * This is the measurement the others were proxies for. Loss, bursts,
     * jitter, clock drift and buffer depth are five ways of arriving with
     * less margin, and each was chased separately across runs 23 to 33
     * while the quantity they all reduce went unmeasured.
     *
     * Counted before the overflow trim below, so the margin recorded is
     * the one this packet actually found.
     */
    s_state.packets_submitted++;
    size_t margin = s_available;
    if (margin < s_margin_min) {
        s_margin_min = margin;
    }
    if (margin == 0) {
        s_state.late_packets++;
    } else if (margin < s_tight_below) {
        s_state.tight_packets++;
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

    /*
     * Walk the fill back down towards the target, one frame at a time.
     *
     * Nothing else does. Sender and DAC run at the same average rate, so
     * wherever a burst leaves the ring is where it stays — measured on
     * hardware creeping from 105 ms to 190 ms of a 200 ms ring over a few
     * minutes and staying there. That is not extra safety: with 10 ms left
     * above it the next burst overflows, while the deep gaps empty it
     * anyway, so the ring manages to drop *and* starve at once. At its
     * target it did neither for minutes together.
     *
     * One frame per 5 ms chunk is about 4000 ppm — 100 ms of excess gone
     * in half a minute, and a single frame dropped at 48 kHz is not
     * audible. This is the crudest form of decision 12's rate matching,
     * doing one job only: keeping the headroom on both sides of the fill
     * instead of all on one.
     */
    if (s_available > s_trim_above) {
        s_read = (s_read + OAL_RTP_CHANNELS) % CAPACITY_SAMPLES;
        s_available -= OAL_RTP_CHANNELS;
        s_state.trimmed_frames++;
    }
    /*
     * And the other direction, which was missing and turned out to matter
     * more than everything it was mistaken for.
     *
     * A lost packet costs the ring 5 ms of depth, and nothing gave it back:
     * sender and sink run at the same average rate, so once the margin is
     * spent only *not playing* rebuilds it — as the comment above the
     * underrun path has said all along. The natural surplus is about one
     * frame a second, so recovering a 50 ms gap takes forty minutes.
     *
     * Measured on two nodes losing the same 2 000 ppm on the same access
     * point: the one whose ring happened to ride high underran once every
     * 380 seconds, the one whose ring sat low underran every 10.5. Same
     * loss, thirty-six times the dropouts, and the difference was entirely
     * how much margin each had left. Below a threshold it is a vicious
     * circle — each underrun inserts silence, silence is extra
     * consumption, and the ring sinks further.
     *
     * So: repeat one frame occasionally while the fill is short. Repeating
     * a frame is consuming slower, which is the only thing that rebuilds
     * margin, and it is exactly what the trim does in reverse.
     *
     * Two speeds, because the cost is a pitch error while it converges.
     * One frame per chunk is 200 a second — 0.42 %, about seven cents,
     * audible on a held note and worth it when the ring is nearly empty.
     * One frame per four chunks is 0.10 %, under two cents, which is not,
     * and is fifty times faster than the natural surplus.
     */
    if (s_available < s_pad_below) {
        bool urgent = s_available < s_target_samples / 2;
        if (urgent || (s_pad_phase++ & 3) == 0) {
            s_read = (s_read + CAPACITY_SAMPLES - OAL_RTP_CHANNELS) % CAPACITY_SAMPLES;
            s_available += OAL_RTP_CHANNELS;
            s_state.padded_frames++;
        }
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

    size_t fill_min, fill_max, fill_now, margin_min;
    uint64_t submitted;
    if (xSemaphoreTake(s_lock, pdMS_TO_TICKS(20)) != pdTRUE) {
        return;
    }
    fill_min = s_fill_min > CAPACITY_SAMPLES ? s_available : s_fill_min;
    fill_max = s_fill_max;
    /* No packet arrived this window, so the honest answer is the fill
     * rather than the re-armed sentinel. */
    margin_min = s_margin_min > CAPACITY_SAMPLES ? s_available : s_margin_min;
    s_margin_min = CAPACITY_SAMPLES + 1;
    fill_now = s_available;
    submitted = s_submitted_frames;
    s_fill_min = CAPACITY_SAMPLES + 1; /* re-armed above any real fill */
    s_fill_max = 0;
    xSemaphoreGive(s_lock);

    /* Published as well as logged. The log line is unreadable on a USB
     * node -- its output stage owns the peripheral the console would use --
     * and the low-water mark is exactly what says whether a ring that
     * polls healthy is touching zero between polls. */
    s_state.fill_min_frames = (uint32_t)(fill_min / OAL_RTP_CHANNELS);
    s_state.fill_max_frames = (uint32_t)(fill_max / OAL_RTP_CHANNELS);
    s_state.margin_min_frames = (uint32_t)(margin_min / OAL_RTP_CHANNELS);

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
        /*
         * A sink that cannot play yet must not be fed. USB dongles arrive
         * when somebody plugs them in, and taking chunks meanwhile would
         * drain the ring into nowhere while counting the frames as played
         * — the exact symptom of a fast clock, and this project has
         * already spent a session chasing that once.
         *
         * Leaving the ring alone lets it fill and drop its oldest, which
         * is what it already does for a full ring and is already counted.
         */
        if (!s_sink->ready()) {
            vTaskDelay(pdMS_TO_TICKS(100));
            continue;
        }

        take_chunk(chunk);

        /*
         * Volume goes here, not into the ring on the way in. Two reasons,
         * and both are audible:
         *
         * The ring holds up to 200 ms, so attenuating on submit would mean
         * a fifth of a second between moving the slider and hearing it —
         * long enough that a person moves it again, overshoots, and
         * decides the control is broken.
         *
         * And the ring then holds full-scale audio. Turning down and back
         * up again returns the original samples rather than samples that
         * were quantised at whatever the level happened to be, so a volume
         * control cannot slowly grind the resolution away.
         */
        oal_pcm_apply_gain(chunk, CHUNK_SAMPLES, s_gain_q16);

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
            esp_err_t err = s_sink->write(
                (const uint8_t *)chunk + offset, sizeof(chunk) - offset,
                &written, 200);
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
    uint32_t target_ms = config->target_ms ? config->target_ms : 100;

    /* Before the task exists, so the first chunk out of the DAC is already
     * at the stored level. Coming up at full scale and correcting a moment
     * later is a jump every time the node reboots, in a house where a node
     * reboots for an update. */
    oal_playout_set_volume(config->volume);

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

    s_sink = oal_sink_for(config->output);
    if (s_sink == NULL) {
        ESP_LOGE(TAG, "no sink for output stage %s — this build cannot drive it",
                 oal_output_name(config->output));
        return ESP_ERR_NOT_SUPPORTED;
    }

    oal_sink_config_t sink_config = {
        .sample_rate = rate,
        .bclk_gpio = config->bclk_gpio,
        .ws_gpio = config->ws_gpio,
        .dout_gpio = config->dout_gpio,
    };

    esp_err_t err = s_sink->open(&sink_config);
    if (err != ESP_OK) {
        return err;
    }

    s_fill_min = CAPACITY_SAMPLES + 1;
    s_trace_at_us = (uint64_t)esp_timer_get_time();

    s_state.running = true;
    s_state.channel = config->channel;
    s_trim_above = s_target_samples + (CAPACITY_SAMPLES - s_target_samples) / 4;
    /* Three quarters of the target: far enough down to mean the margin has
     * really been eroded, not a normal swing, and above the level where a
     * single ordinary gap empties the ring. */
    s_pad_below = s_target_samples * 3 / 4;

    /*
     * A quarter of the target: 25 ms against a 100 ms goal.
     *
     * Not a fraction of capacity, because capacity is a buffer-sizing
     * decision and this is a deadline question. A packet arriving with a
     * quarter of the intended cushion left has not caused a glitch and is
     * one bad moment from causing one, which is exactly the population
     * worth counting before it becomes audible.
     */
    s_tight_below = s_target_samples / 4;
    s_margin_min = CAPACITY_SAMPLES + 1;

    s_state.target_frames = (uint32_t)(s_target_samples / OAL_RTP_CHANNELS);
    s_state.capacity_frames = CAPACITY_SAMPLES / OAL_RTP_CHANNELS;

    /*
     * Above the consumer. The DAC has a deadline every 5 ms and missing it
     * is audible, while a packet taken from the socket a moment late is
     * not — and the socket has a buffer for exactly that.
     */
    if (xTaskCreate(playout_task, "oal_playout", 4096, NULL, 7, &s_task) != pdPASS) {
        s_sink->close();
        s_sink = NULL;
        s_state.running = false;
        return ESP_ERR_NO_MEM;
    }

    ESP_LOGI(TAG, "%s out, %" PRIu32 " Hz, %s, %" PRIu32 " ms buffer",
             s_sink->name, rate, oal_channel_name(config->channel), target_ms);
    return ESP_OK;
}
