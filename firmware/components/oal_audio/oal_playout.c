#include "oal_playout.h"

#include <inttypes.h>
#include <string.h>

#include "oal_sink.h"
#include "esp_heap_caps.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include <math.h>

#include "oal_eq.h"
#include "oal_fade.h"
#include "oal_pcm.h"
#include "oal_phase.h"
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
/*
 * The ring is sized at start, from configuration, and lives in PSRAM.
 *
 * It was a static int32 array in internal DRAM: 200 ms, 75 kB, fixed at
 * compile time. It was briefly doubled, and that was a mistake worth
 * keeping written down -- the commit justified 150 kB as "38 kB on a board
 * with 8 MB of PSRAM" and was wrong on both counts, the arithmetic and the
 * memory, because PSRAM was not enabled in this build at all. The node that
 * paid was the USB one: `usb_host_install` and the driver's transfer
 * buffers come from the same internal heap, and a dongle that cannot be
 * opened is a node that is online, joined, streaming and silent.
 *
 * Both halves of that are now fixed rather than worked around. PSRAM is
 * enabled (sdkconfig.defaults), so the ring no longer competes with the USB
 * host stack for internal DRAM -- 200 ms of ring gives 75 kB *back* to the
 * heap that was starving. And the size is a setting rather than a constant,
 * because the useful range is not knowable from here: this project runs a
 * 100 ms buffer where Snapcast runs 1000, and which is right for a house
 * with 900 ms delivery stalls is an experiment, not an opinion.
 *
 * MALLOC_CAP_SPIRAM is deliberate, not MALLOC_CAP_DEFAULT. Default would
 * fall back to internal DRAM and quietly recreate the exact failure above
 * on the one node that cannot afford it. If PSRAM is not there, this must
 * fail loudly and be seen.
 */
#define RING_MS_MIN 50u
#define RING_MS_MAX 1000u
#define RING_MS_DEFAULT 200u

/*
 * The target may use three quarters of whatever the ring is.
 *
 * Something above the target has to stay free to absorb a burst: a target
 * equal to capacity means the ring is full whenever it is working. This
 * used to be a compile-time assertion tying a published constant to a fixed
 * array, which was the right idea for a fixed ring and is impossible for a
 * settable one.
 *
 * So the relationship moved rather than disappeared. `oal_playout_max_target_ms()`
 * computes it from the live capacity and everything that needs the limit --
 * the delay clamp, `/status`, the Hub's dialog -- asks that one function.
 * Nothing hardcodes a number. That is the same lesson the assertion was
 * written for: the Hub offered 0-200 ms against a real maximum of 50
 * because two numbers described one limit.
 */
#define TARGET_FRACTION_NUM 3
#define TARGET_FRACTION_DEN 4

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

static int32_t *s_ring;
static size_t s_capacity;      /* samples, both channels; 0 until started */
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

/*
 * The worst margin since the stream began, in samples.
 *
 * Its own variable rather than a test against the published frame count:
 * a genuinely late packet drives the published value to zero, and any
 * "or it is still zero" special case then lets the next healthy packet
 * overwrite the worst reading with a good one. Sentinel above every real
 * fill, compared once, no special cases.
 */
static size_t s_margin_worst;

/*
 * Two milliseconds, and what it is for.
 *
 * Every way this buffer fails ends in a step: an overflow jumps the read
 * pointer to unrelated samples, an underrun cuts full-scale audio to zero
 * in one sample, and resuming jumps back up again. A step in a waveform is
 * a broadband transient -- a click -- and it is far more audible than the
 * audio it replaces. Five milliseconds of missing music is nearly nothing;
 * the click at each edge of it is the part a listener actually hears, and
 * reports as distortion rather than as a dropout.
 *
 * So no edge is a step any more. The signal is walked to or from silence
 * across 96 frames, which is long enough to put the transient's energy
 * below the audio and short enough that the ramp itself cannot be heard as
 * a gap. It is a quarter of a chunk, so a fade always fits inside one.
 */

/*
 * The last frame handed to the sink, held so a discontinuity has somewhere
 * to be walked from. Without it there is nothing to ramp *between*: the
 * chunk after a splice begins at an arbitrary sample value and the only
 * alternatives are to jump to it or to fade up from zero, and fading up
 * from zero throws away audio that arrived perfectly well.
 */
static int32_t s_last_frame[OAL_RTP_CHANNELS];

/* Set wherever continuity was broken; makes the next chunk fade in. */
static bool s_splice_pending;

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

/*
 * Room correction, one chain per output (docs/ROOM-CALIBRATION.md).
 *
 * Owned by the playout task and touched by nobody else. A biquad's state is
 * the last two samples it saw, so a chain rebuilt from another task
 * mid-chunk would leave the filter running on half of one correction and
 * half of another -- audible, and impossible to explain afterwards. Instead
 * a change is left here as a request and picked up between chunks.
 */
static oal_eq_chain_t s_eq[OAL_RTP_CHANNELS];
static bool s_eq_enabled;
static float s_eq_preamp = 1.0f;

/*
 * What has been asked for but not yet picked up.
 *
 * The playout reads no configuration of its own -- it is given values, the
 * way the volume is -- because oal_config already depends on this component
 * and the dependency cannot run both ways. So a correction arrives here as
 * a request from whoever read NVS, and the task adopts it between chunks.
 */
static oal_eq_curve_t s_eq_staged[OAL_RTP_CHANNELS];
static volatile bool s_eq_staged_enabled;
static volatile bool s_eq_pending;
static volatile uint8_t s_volume = OAL_VOLUME_DEFAULT;

/*
 * Above this the playout trims. Far enough above the target that ordinary
 * jitter never reaches it, far enough below the capacity to leave a burst
 * somewhere to go.
 */
static size_t s_trim_above;
static size_t s_pad_below;

/*
 * Where two speakers are made to agree, and why it is an average.
 *
 * A node whose crystal runs fast against the sender drains and comes to
 * rest on the pad line; one that runs slow accumulates and rests on the
 * trim line. Two nodes straddling the sender's rate therefore settle a
 * whole quiet band apart -- measured at 137 ms on hardware, with one node
 * showing 0 trims and the other 58 970, which is what a pair of opposite
 * clock errors looks like.
 *
 * Nothing else closes that. The urgent pad and trim below are about not
 * running dry and not overflowing; inside the band they never fire, which
 * is exactly the tolerance that lets the buffer absorb a burst.
 *
 * 0.34.0 tried to close it on the *instantaneous* fill and made everything
 * worse: the fill swings ±150 ms on this network, so every burst crossed
 * the line and spent a frame, and a spent frame is a phase shift. The
 * average is the whole difference. Over about forty seconds a burst is
 * nothing and a clock error is everything, so this corrects the one and
 * cannot see the other.
 */
static int64_t s_fill_avg;      /* EWMA of s_available, shifted by AVG_SHIFT */
static bool s_fill_avg_known;
static size_t s_steer_to;       /* the centre of the quiet band */
static size_t s_steer_slack;    /* no steering inside this, so it settles */
static uint32_t s_steer_phase;
static size_t s_resync_above;   /* fill beyond this, in samples, is a step back */
static uint32_t s_resync_held;  /* consecutive chunks the fill has been out */

/* 8192 chunks of 5 ms is about 41 seconds. Long against the worst stall
 * measured here (3 s) and short against a listener's patience. */
#define AVG_SHIFT 13

/*
 * Where this speaker is on the sender's timeline (oal_phase.h).
 *
 * **Measured, and not yet steered on.** Everything below still decides on
 * the fill, exactly as it did, and this changes no audio at all. That is
 * deliberate: the fill loop's tolerances are the way they are because
 * 0.34.0 tightened them on an observable that swings with every burst and
 * made two speakers worse rather than better, and the whole argument for
 * this observable is that it does not swing. An argument is not a hardware
 * evening. So the number is computed, published and logged first, and the
 * loop moves onto it once a night's listening says the two agree.
 *
 * What it is *for* is the fault of 2026-09-04: a speaker sitting 100 ms
 * behind its partner with a fill of 260-330 ms, inside the quiet band where
 * neither trim nor pad fires and 16 ms short of the resync threshold, for
 * twelve minutes. Nothing in this file could see it, because everything in
 * this file was looking at how much audio was held rather than at which
 * audio it was.
 */
static oal_phase_t s_phase;

/*
 * The phase's swing across a trace window, the twins of s_fill_min/max.
 *
 * They exist to settle one question with a single row instead of luck in
 * the sampling. The claim this whole observable rests on is that a burst
 * moves the fill and does not move the sound; a poll every thirty seconds
 * can only catch that by chance, because the burst is over in
 * milliseconds. Min and max over the window catch it every time: a row
 * showing the fill swinging 137 ms while the phase swings 5 is the
 * property, demonstrated, from one line.
 *
 * Signed, so no sentinel is available above every real value the way
 * s_fill_min uses one -- hence the separate flag.
 */
static int32_t s_phase_min;
static int32_t s_phase_max;
static bool s_phase_span_seen;

/** The local clock in RTP units, the same conversion the consumer uses. */
static uint32_t local_rtp_now(void)
{
    int64_t us = esp_timer_get_time();
    return (uint32_t)((us * OAL_RTP_SAMPLE_RATE) / 1000000);
}

/*
 * How far out of step a speaker may be before it stops walking back and
 * simply steps back, in milliseconds of fill.
 *
 * Above the sender's catch-up cap, and that is the whole reason for the
 * number. A stalled send loop releases up to 100 ms in one lump, and that
 * lump reaches *every* speaker in the same instant — so it lifts both
 * fills equally, leaves the difference between them untouched, and is
 * absorbed by the ring with nothing audible happening. Jumping on that
 * would put a click in both speakers to fix a disagreement that does not
 * exist. So the threshold sits above it: 120 ms is reached only by
 * something one-sided, which is what a re-prime after a dropout is.
 *
 * The cost of a jump is one discontinuity on one speaker. The cost of not
 * jumping is 1 ms a second of creep — nearly three minutes at 170 ms out,
 * with the two speakers a slap echo apart for all of it. Below the
 * threshold the creep is still the right tool and is left alone.
 */
#define RESYNC_MS 120

/* Confirm before stepping: a jump is not something to do on one chunk's
 * reading. Two hundred chunks is a second of the fill genuinely sitting
 * out there, which no burst does — the emergency trim is already eating
 * one by then. */
#define RESYNC_CONFIRM_CHUNKS 200


/** The rate the target was computed against, so it can be recomputed. */
static uint32_t s_rate;

/*
 * Everything the target implies, in one place.
 *
 * Four levels are derived from it — where to trim, where to pad, what
 * counts as a tight arrival, and what /stream reports as the goal — and
 * they were computed inline at startup. That was fine while the target was
 * fixed at boot. It is not fine now that a node can be told to hold back
 * to meet a slower speaker, because a target changed without its
 * dependants is a servo aiming at one number and judging itself by four
 * others.
 */
static void apply_target(uint32_t rate, uint32_t target_ms)
{
    s_rate = rate;
    s_target_samples = (size_t)rate * target_ms / 1000 * OAL_RTP_CHANNELS;
    /*
     * Three quarters, not half.
     *
     * Something above the target has to stay free to absorb a burst -- a
     * target equal to capacity means the ring is full whenever it is
     * working. Half left 100 ms of headroom above a 100 ms target and made
     * 100 ms the ceiling too, so no node could be delayed at all.
     *
     * Three quarters allows a 150 ms target in the default 200 ms ring,
     * which covers the 20-40 ms an output stage differs by with room to
     * spare, and keeps 50 ms above it. Using the delay therefore *spends*
     * burst headroom, which is a real cost and a visible one: the margin
     * buckets say how much of it was ever needed.
     *
     * The fraction is what stayed constant when the ring became settable.
     * A 1000 ms ring allows a 750 ms target by the same rule, and the node
     * publishes that rather than anyone assuming it.
     */
    size_t ceiling = s_capacity / TARGET_FRACTION_DEN * TARGET_FRACTION_NUM;
    if (s_target_samples > ceiling) {
        s_target_samples = ceiling;
    }

    /*
     * Half again the target -- and measured against the target, not the
     * capacity, which is the whole point of this line.
     *
     * It used to be `target + (capacity - target) / 4`, and that quietly
     * made the ring's size a latency control. The fill does not sit at the
     * target; it floats up to just under wherever trimming begins and stays
     * there, because bursts push it up faster than a servo removing one
     * frame per 5 ms chunk can pull it down. So raising the capacity raised
     * the trim line, and the buffer got deeper without anyone asking.
     *
     * That was measured, not reasoned: going from a 200 ms ring to a 400 ms
     * one moved the trim line from 125 ms to 175 ms and left the fill
     * sitting at 221 ms against a 100 ms target. Real latency more than
     * doubled as a side effect of buying burst headroom. The burst headroom
     * was worth having -- overflow drops fell sixteenfold -- but the two
     * should not have been the same knob.
     *
     * Now they are not. `ringMs` buys room to absorb a burst and nothing
     * else; `delayMs` moves the target and is the only thing that decides
     * how far the speaker lags the room. A 400 ms ring at a 100 ms target
     * trims at 150 ms whether the ring is 200 ms or 1000.
     *
     * Fifty per cent above, because the fill was measured swinging about
     * 35 ms either side of its mark on working hardware. Trimming has to
     * start above the ordinary swing or the servo fights the weather.
     */
    s_trim_above = s_target_samples + s_target_samples / 2;

    /*
     * ...but never so close to the rim that the ring overflows before it
     * has begun trimming. Reachable with a small ring and a large delay --
     * a 150 ms target in a 200 ms ring would otherwise put the trim line at
     * 225 ms, above a capacity of 200 -- and an overflow discards the
     * *oldest* audio, which is a jump rather than a stretch.
     */
    size_t rim = s_capacity - s_capacity / 8;
    if (s_trim_above > rim) {
        s_trim_above = rim;
    }

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

    /*
     * Reported as where the fill settles, which is the trim line: it is
     * the only value two nodes with the same ring and delay share, and the
     * only one anything pushes back from. Not converge_below, which no
     * longer exists.
     */
    /*
     * The centre of the quiet band, which is the one depth both nodes can
     * share: the pad line is where a fast crystal lands and the trim line
     * where a slow one does, so the midpoint belongs to neither and is
     * reachable from both. Identical on any two nodes with the same ring
     * and delay, which is what makes it an agreement rather than a
     * coincidence.
     */
    s_steer_to = (s_pad_below + s_trim_above) / 2;
    s_steer_to -= s_steer_to % OAL_RTP_CHANNELS;

    /*
     * A fortieth of the target either side -- 5 ms at the usual 200.
     *
     * The slack stops the loop dithering across its own setpoint, and it
     * also *bounds the residual offset at twice its width*, because two
     * nodes resting anywhere inside it are never corrected toward each
     * other. That second property is the one that decides the number, and
     * a first attempt at a tenth of the target left up to 40 ms on the
     * table -- measured in simulation, not reasoned about.
     *
     * A tenth gave 10-34 ms across seeds, a fortieth 4-10, an eightieth
     * 2-5 with a third more frames spent. A fortieth is the knee: inside
     * what two speakers hear as one source, at about 13 frames a second of
     * correction, which is 0.027 % and nowhere near audible.
     *
     * Narrow is affordable here only because the input is a forty-second
     * average. On the instantaneous fill this width would be catastrophic,
     * and 0.34.0 is the proof.
     */
    s_steer_slack = s_target_samples / 40;
    s_steer_slack -= s_steer_slack % OAL_RTP_CHANNELS;

    s_resync_above = (size_t)rate * RESYNC_MS / 1000 * OAL_RTP_CHANNELS;
    /*
     * A floor, not a constant. The quiet band grows with the target -- at
     * 650 ms of delay it is nearly six hundred milliseconds wide -- and a
     * fixed 120 would then fire on swings that are ordinary there. So take
     * whichever is larger: far enough out to be a real disagreement at any
     * depth, and never closer than the sender's burst at the shallow end.
     * The host test asserts both, and caught this the first time round.
     */
    {
        /* Rounded up to a whole frame: the trim to even below
         * would otherwise land one sample under the floor. */
        size_t half = (s_trim_above - s_pad_below) / 2;
        half = (half + OAL_RTP_CHANNELS - 1)
             / OAL_RTP_CHANNELS * OAL_RTP_CHANNELS;
        if (s_resync_above < half) {
            s_resync_above = half;
        }
    }
    s_resync_above -= s_resync_above % OAL_RTP_CHANNELS;
    s_resync_held = 0;

    s_state.steer_frames = (uint32_t)(s_steer_to / OAL_RTP_CHANNELS);

    s_state.target_frames = (uint32_t)(s_target_samples / OAL_RTP_CHANNELS);
}
static uint32_t s_pad_phase;
static uint32_t s_trim_phase;


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

/*
 * Move the target while the music plays.
 *
 * The servo already knows how: it pads a frame when the fill is below
 * target and trims one when it is above, so raising the target makes the
 * node consume very slightly slower until the ring is deeper, and lowering
 * it does the reverse. Convergence is about a second of delay per thirty
 * seconds of music, at a tenth of a percent of pitch error -- under two
 * cents, inaudible, which is why the servo was built that way.
 *
 * The gradualness is the feature. Alignment gets tuned by ear against
 * another speaker, and a setting that jumped would have to be re-judged
 * from scratch after every nudge; one that slides lets you hear it close.
 *
 * Under the lock, because the playout task compares against every level
 * this recomputes.
 */
esp_err_t oal_playout_set_target_ms(uint32_t target_ms)
{
    if (!s_state.running || s_lock == NULL) {
        return ESP_ERR_INVALID_STATE;
    }
    if (xSemaphoreTake(s_lock, pdMS_TO_TICKS(20)) != pdTRUE) {
        return ESP_ERR_TIMEOUT;
    }
    apply_target(s_rate ? s_rate : OAL_RTP_SAMPLE_RATE, target_ms);
    size_t frames = s_target_samples / OAL_RTP_CHANNELS;
    xSemaphoreGive(s_lock);
    ESP_LOGI(TAG, "playout target now %" PRIu32 " ms (%u frames)",
             target_ms, (unsigned)frames);
    return ESP_OK;
}

/**
 * Adopts a staged correction between chunks.
 *
 * Only ever called from the playout task, which is what makes the chains
 * safe to rebuild: a filter's state is the last two samples it saw, and
 * replacing coefficients underneath a half-filtered chunk is a
 * discontinuity that rings for as long as the filter decays.
 */
static void adopt_eq(void)
{
    s_eq_pending = false;

    for (unsigned ch = 0; ch < OAL_RTP_CHANNELS; ch++) {
        oal_eq_chain_build(&s_eq[ch], &s_eq_staged[ch], OAL_RTP_SAMPLE_RATE);
    }

    /*
     * The headroom is computed from the filters that were just built, not
     * taken from a setting. It is a function of them and nothing else, so
     * storing it separately only creates a way for the two to disagree --
     * which they would the moment somebody edited a vector by hand.
     */
    s_eq_preamp = oal_eq_headroom(s_eq_staged, OAL_RTP_CHANNELS, OAL_RTP_SAMPLE_RATE);

    /* Enabled only if there is something to run. A switch turned on over an
     * empty vector would cost a branch per chunk and change nothing. */
    s_eq_enabled = s_eq_staged_enabled
        && (oal_eq_chain_active(&s_eq[0]) || oal_eq_chain_active(&s_eq[1]));

    ESP_LOGI(TAG, "room correction %s: %u + %u bands, headroom %.1f dB",
             s_eq_enabled ? "on" : "off",
             (unsigned)s_eq[0].count, (unsigned)s_eq[1].count,
             (double)(20.0f * log10f(s_eq_preamp)));
}

/**
 * The headroom the running correction is taking, in dB. Zero when it is off
 * or when nothing boosts.
 */
float oal_playout_eq_headroom_db(void)
{
    if (!s_eq_enabled || s_eq_preamp >= 1.0f) {
        return 0.0f;
    }
    return 20.0f * log10f(s_eq_preamp);
}

void oal_playout_set_eq(const oal_eq_curve_t *left, const oal_eq_curve_t *right,
                        bool enabled)
{
    /*
     * Staged, not applied. The chains belong to the playout task and this
     * runs on whichever one handled the request; the flag is set last, so
     * the task never sees half a correction.
     */
    if (left != NULL) {
        s_eq_staged[0] = *left;
    }
    if (right != NULL) {
        s_eq_staged[1] = *right;
    }
    s_eq_staged_enabled = enabled;
    s_eq_pending = true;
}

uint8_t oal_playout_volume(void)
{
    return s_volume;
}

void oal_playout_submit(uint8_t *payload, size_t frames, uint32_t rtp_timestamp)
{
    if (!s_state.running || payload == NULL || frames == 0 || s_lock == NULL) {
        return;
    }
    if (frames > CHUNK_FRAMES) {
        frames = CHUNK_FRAMES;
    }

    /*
     * Arrival, read before the conversion and before the lock.
     *
     * Both of those would put work between the packet landing and the clock
     * being read, and the delay estimate is a minimum over a window — so a
     * reading inflated by a lock wait does not average out, it simply never
     * becomes the minimum, and the estimate quietly describes the moments
     * this task happened to be idle rather than the network.
     */
    uint32_t arrival = local_rtp_now();
    uint64_t arrival_us = (uint64_t)esp_timer_get_time();

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
    /*
     * Only once the ring is primed.
     *
     * Before that it is *deliberately* below target: the playout holds
     * silence until it has collected `s_target_samples`, because playing
     * the first frames as they arrive would empty it again immediately and
     * click through the whole first second. Every packet of that fill
     * arrives to a short ring, and the first arrives to an empty one, by
     * design.
     *
     * Counting those was measuring the buffer doing its job. It cost about
     * one late and twenty tight per prime -- and a prime happens again
     * after every underrun, so the refill following a dropout was scored
     * as twenty further near-misses on top of the dropout already counted.
     * The first hardware reading showed 10 late and 119 tight on a link
     * with zero loss, no gaps, 1.10 ms of jitter and a 100 ms low-water
     * margin: four measurements saying nothing was wrong, and one saying
     * something was, because it was counting the recoveries.
     */
    if (s_primed) {
        size_t margin = s_available;
        if (margin < s_margin_min) {
            s_margin_min = margin;
        }
        /*
         * And the worst since the stream began.
         *
         * `margin_min_frames` is the tightest moment of the last five
         * seconds, which is the right shape for watching a link live --
         * measured on hardware it jumps between 30 and 70 ms window to
         * window, because delivery pauses and catches up rather than
         * drifting. Every reading is a fresh sample of a fluctuating
         * quantity, so no single one says how close the design has ever
         * come to failing.
         *
         * This one does. It only falls, which is exactly the wrong
         * property for a live indicator and exactly the right one for
         * sizing a buffer: "in three hours the closest this got to silence
         * was 22 ms" is the number that decides whether 100 ms of target
         * is generous or barely enough.
         */
        if (margin < s_margin_worst) {
            s_margin_worst = margin;
            s_state.margin_worst_frames = (uint32_t)(margin / OAL_RTP_CHANNELS);
        }

        /* Against the target rather than capacity: the target is the
         * cushion the design intends this packet to find, and capacity is
         * a separate decision about how much burst to survive. */
        size_t tenths = s_target_samples ? (margin * 100u) / s_target_samples : 100u;
        size_t bucket = tenths < 10 ? 0u
                      : tenths < 25 ? 1u
                      : tenths < 50 ? 2u
                      : tenths < 75 ? 3u : 4u;
        s_state.margin_buckets[bucket]++;
        if (margin == 0) {
            s_state.late_packets++;
        } else if (margin < s_tight_below) {
            s_state.tight_packets++;
        }
    }

    /*
     * Where this packet sits on the sender's timeline, before anything
     * below moves the ring around.
     *
     * A break here — a restart, a seek, a hole left by loss — is counted
     * and nothing else. The ring's *content* is still contiguous; it is
     * only the sender's numbering of it that jumped, and the tracker
     * withholds the position until the pre-break audio has been played
     * rather than reporting one that is wrong by the size of the jump.
     */
    if (oal_phase_on_packet(&s_phase, rtp_timestamp, (uint32_t)frames, arrival,
                            arrival_us, (uint32_t)(s_available / OAL_RTP_CHANNELS))) {
        s_state.timeline_breaks = s_phase.breaks;
    }

    /*
     * A full ring drops its oldest frames, not its newest. Live audio
     * should stay current: dropping what is about to be played costs one
     * glitch, while dropping what just arrived costs the same glitch and
     * leaves the delay permanently longer.
     */
    if (s_available + samples > s_capacity) {
        size_t overflow = s_available + samples - s_capacity;
        s_read = (s_read + overflow) % s_capacity;
        s_available -= overflow;
        s_state.dropped_frames += (uint32_t)(overflow / OAL_RTP_CHANNELS);
        oal_phase_on_played(&s_phase, (uint32_t)(overflow / OAL_RTP_CHANNELS));
        /* The next chunk starts somewhere unrelated to the last sample the
         * speaker saw. Glide into it rather than stepping. */
        s_splice_pending = true;

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

    size_t first = s_capacity - s_write;
    if (first > samples) {
        first = samples;
    }
    memcpy(&s_ring[s_write], s_scratch, first * sizeof(int32_t));
    if (first < samples) {
        memcpy(&s_ring[0], &s_scratch[first], (samples - first) * sizeof(int32_t));
    }
    s_write = (s_write + samples) % s_capacity;
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
/** Remembers the last frame of `frames` frames, for the next ramp. */
static void hold_last_frame(const int32_t *chunk, size_t frames)
{
    if (frames == 0) {
        return;
    }
    for (unsigned c = 0; c < OAL_RTP_CHANNELS; c++) {
        s_last_frame[c] = chunk[(frames - 1) * OAL_RTP_CHANNELS + c];
    }
}

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
            /*
             * Start at exactly the target, never at whatever happened to
             * have arrived. This is the half of 0.33.0 that worked, and it
             * stays.
             *
             * At the target and not higher: 0.34.0 primed at the steering
             * line, 88 ms deeper, which left only 112 ms of headroom under
             * a 400 ms ring against stalls measured at 287 ms. A ring that
             * overflows discards its oldest audio, and that is a phase
             * jump -- the very thing two speakers cannot afford.
             *
             * This test runs once per 5 ms chunk and packets arrive in
             * bursts, so the ring can cross the target and keep going
             * before the next look -- most of all at startup, which is the
             * burstiest moment there is. Whatever depth that left became
             * the node's playback phase for the whole session, and two
             * nodes priming on different bursts were audibly apart with
             * nothing to bring them back.
             *
             * Dropping the overshoot is the same operation the trim does,
             * done once while the output is still silent, so it costs
             * nothing to hear. What it buys is that both nodes start from
             * the same number rather than from their own luck.
             *
             * The limit of this: equal depth is equal delay only as far as
             * the packets reach both nodes together. On one access point
             * that is a few milliseconds. It was never the term that
             * mattered -- the prime overshoot was tens.
             */
            size_t excess = s_available - s_target_samples;
            if (excess > 0) {
                s_read = (s_read + excess) % s_capacity;
                s_available -= excess;
                s_state.trimmed_frames += (uint32_t)(excess / OAL_RTP_CHANNELS);
                oal_phase_on_played(&s_phase, (uint32_t)(excess / OAL_RTP_CHANNELS));
            }
            s_primed = true;
            s_starved_chunks = 0;
            s_state.primed_frames = (uint32_t)(s_available / OAL_RTP_CHANNELS);
            s_state.prime_discarded_frames = (uint32_t)(excess / OAL_RTP_CHANNELS);
            /*
             * Warn rather than inform once this has happened before. A
             * first prime is the stream starting; a later one means the
             * ring ran dry, and the count is the number the listener
             * heard. Logging both the same way hid exactly that.
             */
            /* The first prime is the stream starting and moves no phase
             * that was already established; every later one is a jump. */
            if (s_state.underruns != 0) {
                s_state.reprimes++;
            }
            if (s_state.underruns == 0) {
                ESP_LOGI(TAG, "primed at %u frames; playing (discarded %u of overshoot)",
                         (unsigned)(s_available / OAL_RTP_CHANNELS),
                         (unsigned)(excess / OAL_RTP_CHANNELS));
            } else {
                ESP_LOGW(TAG, "re-primed at %u frames after underrun %u (discarded %u)",
                         (unsigned)(s_available / OAL_RTP_CHANNELS),
                         (unsigned)s_state.underruns,
                         (unsigned)(excess / OAL_RTP_CHANNELS));
            }
        } else {
            xSemaphoreGive(s_lock);
            memset(chunk, 0, CHUNK_SAMPLES * sizeof(int32_t));
            /* Filling while unprimed. The first such chunk still has to
             * get down from wherever the audio was cut off. */
            oal_fade_to_silence(chunk, CHUNK_FRAMES, s_last_frame, OAL_RTP_CHANNELS);
            hold_last_frame(chunk, CHUNK_FRAMES);
            s_splice_pending = true;
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
        /* The ring ran dry mid-note. Walk the last sample down instead of
         * cutting it, which is the difference between a dropout and a
         * click -- and the click is the louder of the two. */
        oal_fade_to_silence(chunk, CHUNK_FRAMES, s_last_frame, OAL_RTP_CHANNELS);
        hold_last_frame(chunk, CHUNK_FRAMES);
        s_splice_pending = true;
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
    /*
     * ...but gently, which it was not.
     *
     * This dropped a frame every chunk whenever the fill was above the
     * line, unconditionally. That is 200 frames a second out of 48 000 --
     * 4 167 ppm, about seven cents sharp -- and while a single dropped
     * frame really is inaudible, a run of them is not. A burst leaves the
     * ring high and the trim then runs flat out until it has walked all the
     * way back: run 34 recorded 128 651 trimmed frames, which is eleven
     * minutes of continuously playing slightly fast. Reported from the room
     * as the speaker sounding like it was speeding up to catch up, which is
     * exactly what it was doing.
     *
     * The pad path below already had the answer and this side never grew
     * it: one frame in four normally, every chunk only when the situation
     * is urgent. A quarter rate is 0.10 %, under two cents, which is the
     * threshold the pad comment already argued is inaudible.
     *
     * Urgent here means the fill has climbed halfway from the trim line to
     * the rim, where the next burst would overflow and cost a real splice.
     * Trading seven cents of pitch against a discontinuity is worth it;
     * trading it against nothing in particular is not.
     */
    /*
     * The slow average, updated once per chunk before anything acts on it.
     *
     * Seeded rather than started from zero: a buffer that begins at the
     * prime depth against an average that begins at nothing would steer
     * hard upward for the first minute, which is a fault dressed as a
     * feature.
     */
    if (!s_fill_avg_known) {
        s_fill_avg = (int64_t)s_available << AVG_SHIFT;
        s_fill_avg_known = true;
    } else {
        s_fill_avg += (int64_t)s_available - (s_fill_avg >> AVG_SHIFT);
    }

    bool acted = false;

    /*
     * Far enough out to step back rather than walk back.
     *
     * Checked before the emergency paths because it supersedes them: those
     * shave one frame at a time toward a threshold, and the point here is
     * that shaving is too slow to be worth doing at this distance.
     *
     * On the instantaneous fill, held for a second, rather than on the
     * average. The average is deliberately slow -- forty-one seconds -- so
     * that a burst cannot steer the loop, and waiting that long to notice
     * a speaker is a tenth of a second out defeats the purpose. A second
     * of confirmation is enough to know the fill is genuinely sitting
     * there, and RESYNC_MS is set above the sender's burst so an absorbed
     * lump is not mistaken for a disagreement.
     */
    if (s_resync_above > 0 && s_steer_to > 0) {
        size_t error = s_available > s_steer_to
            ? s_available - s_steer_to : s_steer_to - s_available;
        if (error > s_resync_above) {
            s_resync_held++;
        } else {
            s_resync_held = 0;
        }

        if (s_resync_held >= RESYNC_CONFIRM_CHUNKS) {
            if (s_available > s_steer_to) {
                /* Holding too much: drop the excess and play newer audio. */
                size_t excess = s_available - s_steer_to;
                s_read = (s_read + excess) % s_capacity;
                s_available -= excess;
                s_state.trimmed_frames += (uint32_t)(excess / OAL_RTP_CHANNELS);
                oal_phase_on_played(&s_phase, (uint32_t)(excess / OAL_RTP_CHANNELS));
                s_state.resyncs++;
                acted = true;
            }
            /*
             * Deliberately one-directional: it discards, it never winds
             * back.
             *
             * Winding back to *add* depth means replaying audio already
             * handed to the sink, and at this distance that is a fifth of
             * a second of it. The ring behind the read pointer is not
             * reserved -- the writer fills forward into it -- so a bulk
             * rewind is a race against how much new audio has arrived, and
             * losing that race hands the DAC whatever happened to be
             * there. One frame at a time, as the pad does, the exposure is
             * a frame; two hundred milliseconds at a time it is not worth
             * having.
             *
             * Nor is it needed. A node this far below the setpoint is
             * about to underrun, and the underrun path already re-primes
             * to the target in one step -- a jump by another name, and one
             * that fills from real audio rather than from history. The
             * fill that needs help is the one riding high, which nothing
             * else brings down quickly.
             */
            s_resync_held = 0;
            /* The average describes a fill that no longer exists. */
            s_fill_avg = (int64_t)s_available << AVG_SHIFT;
        }
    }

    if (!acted && s_available > s_trim_above) {
        bool urgent = s_available > s_trim_above + (s_capacity - s_trim_above) / 2;
        if (urgent || (s_trim_phase++ & 3) == 0) {
            s_read = (s_read + OAL_RTP_CHANNELS) % s_capacity;
            s_available -= OAL_RTP_CHANNELS;
            s_state.trimmed_frames++;
            acted = true;
        }
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
    /*
     * Only when the margin has really gone, and NOT as a servo toward a
     * setpoint. 0.34.0 padded anywhere below a line 13 ms under the trim,
     * on the reasoning that a shared setpoint is what puts two speakers
     * together. It made two speakers markedly worse and never settled.
     *
     * The dead band between here and the trim line is not a missing
     * correction, it is jitter tolerance. The comment above the trim line
     * says the fill swings about 35 ms either side of its mark on working
     * hardware and that trimming has to start above the ordinary swing or
     * the servo fights the weather -- and a pad line 13 ms below the trim
     * put exactly that fight on the other side. Every burst crossed the
     * line, every crossing spent a frame, and the counters showed it:
     * 100 792 trims against 34 287 pads on one node in three hours.
     *
     * Worse than wasteful. Each pad and trim is a *phase* shift, so a loop
     * that thrashes is a loop that walks the two speakers apart faster
     * than anything walks them back.
     */
    if (s_available < s_pad_below) {
        bool urgent = s_available < s_target_samples / 2;
        if (urgent || (s_pad_phase++ & 3) == 0) {
            s_read = (s_read + s_capacity - OAL_RTP_CHANNELS) % s_capacity;
            s_available += OAL_RTP_CHANNELS;
            s_state.padded_frames++;
            acted = true;
        }
    }

    /*
     * And the slow one, which is what actually holds two speakers together.
     *
     * Only when neither emergency fired this chunk, so the loop can never
     * fight itself, and only on the average, so a burst is invisible to it.
     * One frame per four chunks is 0.10 %, under two cents of pitch, and
     * about 1 ms of depth per second -- a 137 ms disagreement closes in a
     * little over two minutes.
     *
     * The slack either side is what stops it dithering. Without it the
     * setpoint is a line the fill crosses constantly, every crossing spends
     * a frame, and every spent frame is a phase shift -- which is precisely
     * how 0.34.0 made two speakers worse instead of better.
     */
    if (!acted && s_steer_to > 0) {
        size_t avg = (size_t)(s_fill_avg >> AVG_SHIFT);
        bool low = avg + s_steer_slack < s_steer_to;
        bool high = avg > s_steer_to + s_steer_slack;
        /*
         * How hard to pull, from how far out it is.
         *
         * A fixed rate is the same 1 ms a second whether the speaker is
         * 5 ms out or 90, and the second case is the one somebody can
         * hear: 90 ms took a minute and a half to close, which is longer
         * than the gap between disturbances on a busy evening, so it
         * never arrived.
         *
         * The rate near home is untouched -- one frame in four chunks,
         * a tenth of a percent, under two cents of pitch -- because that
         * is what stops the loop dithering and 0.34.0 is the record of
         * what happens when it is disturbed. Only the far half is
         * quicker, and only while it is far: a quarter of the target out
         * is 0.42 %, about seven cents, for the seconds it takes to get
         * back inside.
         */
        size_t error = high ? avg - s_steer_to : s_steer_to - avg;
        uint32_t mask = error > s_target_samples / 4 ? 0u
                      : error > s_target_samples / 8 ? 1u : 3u;
        if ((low || high) && (s_steer_phase++ & mask) == 0) {
            if (low && s_available + OAL_RTP_CHANNELS <= s_capacity) {
                s_read = (s_read + s_capacity - OAL_RTP_CHANNELS) % s_capacity;
                s_available += OAL_RTP_CHANNELS;
                s_state.padded_frames++;
            } else if (high && s_available >= OAL_RTP_CHANNELS) {
                s_read = (s_read + OAL_RTP_CHANNELS) % s_capacity;
                s_available -= OAL_RTP_CHANNELS;
                s_state.trimmed_frames++;
            }
        }
    }

    /*
     * Where this speaker is, published once per chunk while the ring is
     * still locked so the depth and the clock describe one instant.
     *
     * Taken *before* the copy below, because the question is which sample
     * is about to leave rather than which one just did — and read here
     * rather than assembled in the status handler, which is how
     * `playingTimestamp` came to be reported by subtracting one snapshot
     * from another taken at a different moment. That produced readings of
     * over a second, twenty-four times in one day, on nodes whose buffers
     * had not moved at all.
     */
    {
        uint32_t held_frames = (uint32_t)(s_available / OAL_RTP_CHANNELS);
        uint32_t now = local_rtp_now();
        /* Initialised because both are written only through a pointer the
         * callee may decline to use, and this file is built with -Werror. */
        uint32_t position = 0;
        int32_t error = 0;

        s_state.play_timestamp_known =
            s_primed && oal_phase_position(&s_phase, held_frames, &position);
        if (s_state.play_timestamp_known) {
            s_state.play_timestamp = position;
        }
        /*
         * Against the steering line, not the target.
         *
         * The first hardware reading caught this. The target is 200 ms but
         * the loop rests the fill at `s_steer_to` -- the centre of the
         * quiet band, 225 ms -- so measuring from the target reports a
         * node sitting exactly where the design wants it as 25 ms late,
         * for ever. Harmless when two nodes are compared, since the bias
         * is common and subtracts out, and not harmless at all the moment
         * anything steers on it: driving this to zero would haul the fill
         * to 200 ms against a loop holding it at 225, and the two would
         * push against each other indefinitely.
         *
         * `steerFrames` is what `/status` publishes as the line both
         * speakers aim at, so this is the same number the rest of the
         * system already calls the setpoint.
         */
        s_state.phase_known = s_primed
            && oal_phase_error(&s_phase, held_frames, now,
                               (uint32_t)(s_steer_to / OAL_RTP_CHANNELS), &error);
        if (s_state.phase_known) {
            s_state.phase_error_frames = error;
            if (!s_phase_span_seen || error < s_phase_min) {
                s_phase_min = error;
            }
            if (!s_phase_span_seen || error > s_phase_max) {
                s_phase_max = error;
            }
            s_phase_span_seen = true;
        }
    }

    copied = s_available < CHUNK_SAMPLES ? s_available : CHUNK_SAMPLES;

    size_t first = s_capacity - s_read;
    if (first > copied) {
        first = copied;
    }
    memcpy(chunk, &s_ring[s_read], first * sizeof(int32_t));
    if (first < copied) {
        memcpy(&chunk[first], &s_ring[0], (copied - first) * sizeof(int32_t));
    }
    s_read = (s_read + copied) % s_capacity;
    s_available -= copied;
    /* Lets a timeline break settle: the position comes back once the audio
     * from before the jump has all been played. */
    oal_phase_on_played(&s_phase, (uint32_t)(copied / OAL_RTP_CHANNELS));

    /*
     * Into the audio, if the last thing the speaker heard does not join on
     * to it: after an overflow spliced the ring, or after any stretch of
     * silence. Before the tail is shaped, so a chunk that both resumes and
     * runs out is faded at each end rather than only one.
     */
    if (s_splice_pending && copied > 0) {
        oal_fade_from(chunk, CHUNK_FRAMES, s_last_frame, OAL_RTP_CHANNELS);
        s_splice_pending = false;
    }

    if (copied < CHUNK_SAMPLES) {
        memset(&chunk[copied], 0, (CHUNK_SAMPLES - copied) * sizeof(int32_t));
        s_state.silence_frames += (uint32_t)((CHUNK_SAMPLES - copied) / OAL_RTP_CHANNELS);
        /* Ramp the tail down from the last frame that was real, not from
         * the previous chunk's — this one has already moved on. */
        hold_last_frame(chunk, copied / OAL_RTP_CHANNELS);
        oal_fade_to_silence(&chunk[copied], (CHUNK_SAMPLES - copied) / OAL_RTP_CHANNELS,
                            s_last_frame, OAL_RTP_CHANNELS);
        s_splice_pending = true;
    }

    /* Whatever the sink is about to be handed, fades included, is where
     * the next discontinuity has to start from. */
    hold_last_frame(chunk, CHUNK_FRAMES);

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
    int32_t phase_min, phase_max;
    uint64_t submitted;
    if (xSemaphoreTake(s_lock, pdMS_TO_TICKS(20)) != pdTRUE) {
        return;
    }
    fill_min = s_fill_min > s_capacity ? s_available : s_fill_min;
    fill_max = s_fill_max;
    /* Same fallback as the fill's: a window with no reading reports the
     * value now rather than the previous window's, which is the stale
     * lifetime counter this whole log format exists to avoid. */
    phase_min = s_phase_span_seen ? s_phase_min : s_state.phase_error_frames;
    phase_max = s_phase_span_seen ? s_phase_max : s_state.phase_error_frames;
    s_phase_span_seen = false;
    /* No packet arrived this window, so the honest answer is the fill
     * rather than the re-armed sentinel. */
    margin_min = s_margin_min > s_capacity ? s_available : s_margin_min;
    s_margin_min = s_capacity + 1;
    fill_now = s_available;
    submitted = s_submitted_frames;
    s_fill_min = s_capacity + 1; /* re-armed above any real fill */
    s_fill_max = 0;
    xSemaphoreGive(s_lock);

    /* Published as well as logged. The log line is unreadable on a USB
     * node -- its output stage owns the peripheral the console would use --
     * and the low-water mark is exactly what says whether a ring that
     * polls healthy is touching zero between polls. */
    s_state.fill_min_frames = (uint32_t)(fill_min / OAL_RTP_CHANNELS);
    s_state.fill_max_frames = (uint32_t)(fill_max / OAL_RTP_CHANNELS);
    s_state.margin_min_frames = (uint32_t)(margin_min / OAL_RTP_CHANNELS);
    s_state.phase_min_frames = phase_min;
    s_state.phase_max_frames = phase_max;

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
        /*
         * Room correction ahead of the volume, and both halves of that
         * matter.
         *
         * The headroom goes INSIDE the filter, not into the volume gain
         * after it. A boosting band pushes material already near full scale
         * past it, and the clip happens the moment the sample is written
         * back as an int32; attenuating afterwards scales a value whose
         * peaks are already gone. The first version of this folded the
         * preamp into the volume multiply, which was one multiply cheaper
         * and bought exactly nothing.
         *
         * And the headroom applies only when the correction does. It was
         * unconditional, so turning the correction off left the speaker
         * quieter by the preamp -- which would have made every comparison a
         * level difference rather than a tonal one, and the switch exists
         * precisely so that comparison is honest.
         */
        if (s_eq_pending) {
            adopt_eq();
        }
        if (s_eq_enabled) {
            for (unsigned ch = 0; ch < OAL_RTP_CHANNELS; ch++) {
                oal_eq_chain_run(&s_eq[ch], chunk + ch, CHUNK_FRAMES, OAL_RTP_CHANNELS,
                                 s_eq_preamp);
            }
        }

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
        }
        /*
         * Counted from the bytes this chunk actually moved, once, rather
         * than from each write's own quotient.
         *
         * A frame is eight bytes and the driver is under no obligation to
         * stop on one. The old line did `written / 8` per call and threw
         * the remainder away every time, so a chunk that took two writes
         * could lose most of a frame and one that took several could lose
         * several -- a write of four bytes counted as nothing at all.
         *
         * Silent, permanent, and one-directional: the counter can only run
         * slow. It reads exactly like a slow crystal, which is what it was
         * taken for. Two speakers reported about -4000 ppm against the Hub
         * while their buffers sat on the setpoint and their trim counts
         * said a few hundred -- and a real -4000 would have needed 691,200
         * trims an hour, against 144,548 in the node's whole life.
         *
         * The chunk is a whole number of frames, so on the ordinary path
         * this is exact however the driver split it. A short write can
         * still lose up to one frame, once, instead of once per call.
         */
        s_state.frames_played += offset / (OAL_RTP_CHANNELS * sizeof(int32_t));

        trace();
    }
}

/* Only ever called from a failed start. There is no oal_playout_stop: once
 * a node's output stage is up it stays up for the life of the boot, so in
 * the working case this ring is allocated once and outlives everything. */
static void release_ring(void)
{
    heap_caps_free(s_ring);
    s_ring = NULL;
    s_capacity = 0;
}

uint32_t oal_playout_max_target_ms(void)
{
    if (s_capacity == 0 || s_rate == 0) {
        return 0;
    }
    size_t ceiling = s_capacity / TARGET_FRACTION_DEN * TARGET_FRACTION_NUM;
    return (uint32_t)(ceiling / OAL_RTP_CHANNELS * 1000 / s_rate);
}

uint32_t oal_playout_ring_ms(void)
{
    if (s_capacity == 0 || s_rate == 0) {
        return 0;
    }
    return (uint32_t)(s_capacity / OAL_RTP_CHANNELS * 1000 / s_rate);
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

    /*
     * The ring, before anything else can want memory.
     *
     * Clamped here rather than trusted, because the value came from NVS and
     * NVS survives a downgrade: a node configured for 1000 ms by a later
     * firmware must not hand a rolled-back one a length it never expected.
     */
    uint32_t ring_ms = config->ring_ms ? config->ring_ms : RING_MS_DEFAULT;
    if (ring_ms < RING_MS_MIN) { ring_ms = RING_MS_MIN; }
    if (ring_ms > RING_MS_MAX) { ring_ms = RING_MS_MAX; }

    /* A whole number of packets, so the wrap arithmetic never splits one. */
    size_t packets = (size_t)rate * ring_ms / 1000 * OAL_RTP_CHANNELS / CHUNK_SAMPLES;
    if (packets < 2) { packets = 2; }
    s_capacity = packets * CHUNK_SAMPLES;

    /*
     * MALLOC_CAP_SPIRAM, never MALLOC_CAP_DEFAULT. Falling back to internal
     * DRAM is exactly the failure this project already had once: the ring
     * took the heap the USB host stack needed and the dongle node played
     * nothing while looking perfectly healthy. If PSRAM is missing, the
     * right outcome is a node that says so, not one that quietly starves
     * its own output stage.
     */
    s_ring = heap_caps_malloc(s_capacity * sizeof(int32_t), MALLOC_CAP_SPIRAM);
    if (s_ring == NULL) {
        ESP_LOGE(TAG, "no PSRAM for a %" PRIu32 " ms ring (%u bytes); "
                 "playout cannot start", ring_ms,
                 (unsigned)(s_capacity * sizeof(int32_t)));
        s_capacity = 0;
        return ESP_ERR_NO_MEM;
    }
    memset(s_ring, 0, s_capacity * sizeof(int32_t));
    s_read = 0;
    s_write = 0;
    s_available = 0;
    s_primed = false;
    /* Silence, and owing a fade-in: the first chunk then rises into the
     * music instead of stepping into it from nothing, which is the same
     * edge as every other one this guards. */
    memset(s_last_frame, 0, sizeof(s_last_frame));
    s_splice_pending = true;
    s_trim_phase = 0;
    s_pad_phase = 0;
    s_steer_phase = 0;
    s_fill_avg = 0;
    s_fill_avg_known = false;
    /* The ring is empty and the clocks mean nothing yet; the break count
     * survives, because it describes the link rather than this ring. */
    oal_phase_reset(&s_phase);
    s_state.play_timestamp_known = false;
    s_state.phase_known = false;
    s_phase_span_seen = false;
    s_phase_min = 0;
    s_phase_max = 0;

    /* Before the task exists, so the first chunk out of the DAC is already
     * at the stored level. Coming up at full scale and correcting a moment
     * later is a jump every time the node reboots, in a house where a node
     * reboots for an update. */
    oal_playout_set_volume(config->volume);

    apply_target(rate, target_ms);

    /* Every exit from here on has to give the ring back. It is up to a
     * megabyte of PSRAM, and a node that fails to start its output stage is
     * one a caller may well retry -- leaking a ring per attempt turns a
     * recoverable fault into a memory exhaustion a long way from its
     * cause. */
    s_lock = xSemaphoreCreateMutex();
    if (s_lock == NULL) {
        release_ring();
        return ESP_ERR_NO_MEM;
    }

    s_sink = oal_sink_for(config->output);
    if (s_sink == NULL) {
        ESP_LOGE(TAG, "no sink for output stage %s — this build cannot drive it",
                 oal_output_name(config->output));
        release_ring();
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
        release_ring();
        return err;
    }

    s_fill_min = s_capacity + 1;
    s_trace_at_us = (uint64_t)esp_timer_get_time();

    s_state.running = true;
    s_state.channel = config->channel;
    s_margin_min = s_capacity + 1;
    s_margin_worst = s_capacity + 1;
    s_state.margin_worst_frames = 0;

    s_state.target_frames = (uint32_t)(s_target_samples / OAL_RTP_CHANNELS);
    s_state.capacity_frames = s_capacity / OAL_RTP_CHANNELS;

    /*
     * Above the consumer. The DAC has a deadline every 5 ms and missing it
     * is audible, while a packet taken from the socket a moment late is
     * not — and the socket has a buffer for exactly that.
     */
    if (xTaskCreate(playout_task, "oal_playout", 4096, NULL, 7, &s_task) != pdPASS) {
        s_sink->close();
        s_sink = NULL;
        s_state.running = false;
        release_ring();
        return ESP_ERR_NO_MEM;
    }

    ESP_LOGI(TAG, "%s out, %" PRIu32 " Hz, %s, %" PRIu32 " ms target in a %"
             PRIu32 " ms ring (%u kB PSRAM), max target %" PRIu32 " ms",
             s_sink->name, rate, oal_channel_name(config->channel), target_ms,
             ring_ms, (unsigned)(s_capacity * sizeof(int32_t) / 1024),
             oal_playout_max_target_ms());
    return ESP_OK;
}
