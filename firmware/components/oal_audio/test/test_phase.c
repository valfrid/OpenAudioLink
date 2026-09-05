/*
 * Where a speaker is on the sender's timeline, checked on the host.
 *
 * Every test here is a property the correction loop will eventually rest
 * on, and the reason the arithmetic lives in its own ESP-free file is so
 * they can be run against the real code rather than against a copy of it.
 * The project's other answer to "this file needs ESP-IDF" is to mirror the
 * sums into the test with a comment saying to keep them in step, and that
 * has already shipped a threshold nobody had checked.
 *
 * The one that matters most is Bursts_do_not_move_the_phase. It is the
 * whole reason for measuring position instead of depth, and it is the
 * property that lets the tolerance be a few milliseconds instead of the
 * 120 ms that let a speaker sit a slap echo behind its partner for twelve
 * minutes on 2026-09-04.
 */

#include "oal_phase.h"

#include <assert.h>
#include <stdio.h>

#define RATE 48000u
#define PACKET 240u          /* 5 ms, as OAL_RTP_FRAMES_PER_PACKET is */
#define TARGET 9600u         /* 200 ms */

static int failures;

#define CHECK(cond, ...) do { \
    if (!(cond)) { \
        printf("FAIL %s:%d: ", __func__, __LINE__); \
        printf(__VA_ARGS__); \
        printf("\n"); \
        failures++; \
    } \
} while (0)

/*
 * A sender and a node, both driven by hand.
 *
 * `epoch` is deliberately an ugly number rather than zero: the difference
 * between the node's clock and the sender's is arbitrary in real life, and
 * an epoch of zero would let sign errors and wrap bugs pass unnoticed.
 */
typedef struct {
    oal_phase_t phase;
    uint32_t sender_rtp;     /* the sender's next timestamp */
    uint32_t node_rtp;       /* the node's clock, in RTP units */
    uint64_t node_us;
    uint32_t held;           /* frames in the ring */
    uint32_t epoch;          /* node clock minus sender clock, at zero delay */
} rig_t;

static void rig_init(rig_t *r, uint32_t sender_start, uint32_t epoch)
{
    oal_phase_reset(&r->phase);
    r->sender_rtp = sender_start;
    r->epoch = epoch;
    r->node_rtp = sender_start + epoch;
    r->node_us = 0;
    r->held = 0;
}

/** Advances the node's clock by `frames` of real time. */
static void tick(rig_t *r, uint32_t frames)
{
    r->node_rtp += frames;
    r->node_us += (uint64_t)frames * 1000000ull / RATE;
}

/** One packet arriving `delay` frames after the sender stamped it. */
static bool arrive(rig_t *r, uint32_t delay)
{
    uint32_t ts = r->sender_rtp;
    r->sender_rtp += PACKET;
    bool broke = oal_phase_on_packet(&r->phase, ts, PACKET,
                                     ts + r->epoch + delay, r->node_us, r->held);
    r->held += PACKET;
    return broke;
}

/** One chunk played out of the ring. */
static void play(rig_t *r, uint32_t frames)
{
    r->held -= frames;
    oal_phase_on_played(&r->phase, frames);
}

/** Runs the link in step for `seconds`, holding the ring at the target. */
static void settle(rig_t *r, uint32_t delay, unsigned seconds)
{
    for (unsigned i = 0; i < seconds * RATE / PACKET; i++) {
        arrive(r, delay);
        play(r, PACKET);
        tick(r, PACKET);
    }
}

/* ------------------------------------------------------------------ */

/**
 * A link running exactly as designed reports no error, whatever the
 * arbitrary offset between the two clocks happens to be.
 */
static void A_link_at_the_target_reads_zero(void)
{
    const uint32_t epochs[] = { 0u, 1234567u, 0xFFFFFF00u, 0x80000000u };
    for (unsigned e = 0; e < sizeof(epochs) / sizeof(epochs[0]); e++) {
        rig_t r;
        rig_init(&r, 500000u, epochs[e]);

        /* Fill to the target, then hold it there. */
        for (unsigned i = 0; i < TARGET / PACKET; i++) {
            arrive(&r, 0);
            tick(&r, PACKET);
        }
        settle(&r, 0, 30);

        int32_t error;
        CHECK(oal_phase_error(&r.phase, r.held, r.node_rtp, TARGET, &error),
              "epoch %u: no reading", (unsigned)epochs[e]);
        CHECK(error > -3 && error < 3,
              "epoch %08x: expected about zero, got %d frames", (unsigned)epochs[e], error);
    }
}

/**
 * The property the whole design turns on.
 *
 * A burst of packets arriving at once raises the ring's depth without
 * moving the sound at all. Depth calls that an error of the burst's whole
 * size; the phase must call it nothing, because nothing happened to what
 * the listener hears.
 */
static void Bursts_do_not_move_the_phase(void)
{
    rig_t r;
    rig_init(&r, 900000u, 77777u);
    for (unsigned i = 0; i < TARGET / PACKET; i++) { arrive(&r, 0); tick(&r, PACKET); }
    settle(&r, 0, 20);

    int32_t before = 0;
    CHECK(oal_phase_error(&r.phase, r.held, r.node_rtp, TARGET, &before), "no reading");
    uint32_t depth_before = r.held;

    /* Twelve packets in one instant: 60 ms of sender catch-up, which is
     * ordinary for a Windows sender waking on a 15.6 ms timer. */
    for (unsigned i = 0; i < 12; i++) {
        arrive(&r, (uint32_t)(11 - i) * PACKET);
    }

    int32_t after = 0;
    CHECK(oal_phase_error(&r.phase, r.held, r.node_rtp, TARGET, &after), "no reading after");

    CHECK(r.held - depth_before == 12 * PACKET,
          "the burst should have raised the depth by 2880 frames, not %u",
          (unsigned)(r.held - depth_before));
    CHECK(after == before,
          "a burst moved the phase by %d frames; depth moved by %u. "
          "This is the property the tolerance depends on",
          after - before, (unsigned)(r.held - depth_before));
}

/**
 * A speaker holding more than the target is playing older audio, and that
 * is what a listener hears as an echo against a partner that is not.
 * Positive must mean late.
 */
static void Holding_extra_audio_reads_as_late(void)
{
    rig_t r;
    rig_init(&r, 12345u, 4242u);
    for (unsigned i = 0; i < TARGET / PACKET; i++) { arrive(&r, 0); tick(&r, PACKET); }
    settle(&r, 0, 20);

    /* Take in 100 ms without playing it: the speaker is now that far behind
     * where the design meant it to be. */
    for (unsigned i = 0; i < 20; i++) {
        arrive(&r, 0);
        tick(&r, PACKET);
    }

    int32_t error = 0;
    CHECK(oal_phase_error(&r.phase, r.held, r.node_rtp, TARGET, &error), "no reading");
    CHECK(error > 4700 && error < 4900,
          "100 ms of extra depth should read as about +4800 frames late, got %d", error);
}

/**
 * And the other way, so the sign is not merely conventional.
 *
 * Note what does *not* make a speaker early: playing 100 ms of audio in
 * 100 ms of time. That drains the ring without moving the sound, and the
 * phase must read zero for it — the node is playing the right sample at the
 * right moment and is merely short of margin. Depth cannot tell those
 * apart, and its answer, "you are 100 ms low, pad", would make the node
 * genuinely late to fix a problem it did not have.
 *
 * What does make it early is discarding frames: a trim advances the
 * position without any time passing.
 */
static void Holding_less_audio_reads_as_early(void)
{
    rig_t r;
    rig_init(&r, 999u, 31337u);
    for (unsigned i = 0; i < TARGET / PACKET; i++) { arrive(&r, 0); tick(&r, PACKET); }
    settle(&r, 0, 20);

    int32_t drained = 0;
    for (unsigned i = 0; i < 20; i++) {
        play(&r, PACKET);
        tick(&r, PACKET);
    }
    CHECK(oal_phase_error(&r.phase, r.held, r.node_rtp, TARGET, &drained), "no reading");
    CHECK(drained > -3 && drained < 3,
          "playing 100 ms of audio in 100 ms of time is not a phase error, "
          "but it read %d frames", drained);

    /* Now a trim: 100 ms discarded, no time passing. */
    play(&r, 4800);

    int32_t error = 0;
    CHECK(oal_phase_error(&r.phase, r.held, r.node_rtp, TARGET, &error), "no reading");
    CHECK(error < -4700 && error > -4900,
          "a 100 ms trim should read as about -4800 frames early, got %d", error);
}

/**
 * Two nodes on one stream, with different clock epochs, different network
 * delays and different depths.
 *
 * The difference in their phase errors must equal the offset a listener
 * hears, to within one term: each node subtracts its *own* least delay, so
 * a node on a slower path aims to play that much later. The residual is
 * therefore exactly the difference in their network delays — 2 ms here,
 * chosen far worse than one access point produces, and the number is
 * asserted rather than waved at.
 *
 * Nothing passes between the nodes to make any of this true.
 */
static void Two_nodes_agree_without_talking_to_each_other(void)
{
    rig_t a, b;
    rig_init(&a, 700000u, 0u);
    rig_init(&b, 700000u, 0xFFF00000u);      /* wildly different clock epoch */

    for (unsigned i = 0; i < TARGET / PACKET; i++) {
        arrive(&a, 48);                       /* 1 ms from the sender */
        arrive(&b, 144);                      /* 3 ms — a worse path */
        tick(&a, PACKET); tick(&b, PACKET);
    }
    for (unsigned i = 0; i < 20 * RATE / PACKET; i++) {
        arrive(&a, 48); arrive(&b, 144);
        play(&a, PACKET); play(&b, PACKET);
        tick(&a, PACKET); tick(&b, PACKET);
    }

    /* B falls 50 ms behind: it takes in audio it does not play. */
    for (unsigned i = 0; i < 10; i++) {
        arrive(&a, 48); arrive(&b, 144);
        play(&a, PACKET);
        tick(&a, PACKET); tick(&b, PACKET);
    }

    int32_t ea = 0, eb = 0;
    CHECK(oal_phase_error(&a.phase, a.held, a.node_rtp, TARGET, &ea), "a: no reading");
    CHECK(oal_phase_error(&b.phase, b.held, b.node_rtp, TARGET, &eb), "b: no reading");

    uint32_t pa = 0, pb = 0;
    CHECK(oal_phase_position(&a.phase, a.held, &pa), "a: no position");
    CHECK(oal_phase_position(&b.phase, b.held, &pb), "b: no position");

    int32_t heard = (int32_t)(pa - pb);       /* what the listener hears */
    int32_t predicted = eb - ea;              /* what the two nodes would work out */

    CHECK(heard == 2400, "B should be playing 2400 frames behind A, not %d", heard);
    CHECK(heard - predicted == 144 - 48,
          "the audible offset (%d frames) and the difference the nodes compute "
          "(%d) should differ by exactly the 96 frames between their network "
          "delays, not by %d", heard, predicted, heard - predicted);
}

/**
 * A stream restart hands out a new random timestamp base. The tracker must
 * notice, and must not report a position while the ring still holds audio
 * from the old timeline.
 */
static void A_restart_is_noticed_and_waited_out(void)
{
    rig_t r;
    rig_init(&r, 100000u, 555u);
    for (unsigned i = 0; i < TARGET / PACKET; i++) { arrive(&r, 0); tick(&r, PACKET); }
    settle(&r, 0, 20);

    uint32_t breaks_before = r.phase.breaks;
    uint32_t held_at_break = r.held;

    /* The sender restarts: new base, nowhere near the old one. */
    r.sender_rtp = 0x7A5B0000u;
    bool broke = arrive(&r, 0);
    CHECK(broke, "a restarted stream should be reported as a break");
    CHECK(r.phase.breaks == breaks_before + 1, "the break should have been counted");

    uint32_t position = 0;
    CHECK(!oal_phase_position(&r.phase, r.held, &position),
          "the position must be withheld while pre-break audio is still queued");

    /* Play out everything that was in the ring when it broke. */
    for (uint32_t played = 0; played < held_at_break; played += PACKET) {
        arrive(&r, 0);
        play(&r, PACKET);
        tick(&r, PACKET);
    }
    CHECK(oal_phase_position(&r.phase, r.held, &position),
          "the position must come back once the old audio has been played");
}

/** Loss leaves a hole, which is a break for the same reason a restart is. */
static void A_hole_left_by_loss_is_a_break(void)
{
    rig_t r;
    rig_init(&r, 4000u, 9u);
    for (unsigned i = 0; i < TARGET / PACKET; i++) { arrive(&r, 0); tick(&r, PACKET); }
    settle(&r, 0, 15);

    uint32_t before = r.phase.breaks;
    r.sender_rtp += PACKET * 5;               /* five packets never arrived */
    CHECK(arrive(&r, 0), "a hole should be reported as a break");
    CHECK(r.phase.breaks == before + 1, "the break should have been counted");
}

/**
 * Clock drift is reported once, not twice.
 *
 * The node's crystal runs 100 ppm fast against the sender's, so its clock
 * ticks faster *and* its DAC consumes faster. The ring drains and the sound
 * genuinely runs ahead, which is a real phase error and must be reported as
 * one — it is exactly what the trim and the pad exist to correct.
 *
 * What must not happen is counting it twice. The delay estimate is taken on
 * the same drifting clock, so a fixed one would add the drift to itself and
 * report double. The windowed minimum follows it instead, leaving the error
 * equal to the ring's own departure from target and no larger.
 */
static void Clock_drift_is_reported_once(void)
{
    oal_phase_t phase;
    oal_phase_reset(&phase);

    const uint32_t epoch = 0x0BADF00Du;
    const double rate = 1.0 + 100e-6;         /* the node's clock, against the sender's */

    uint32_t sender = 3000000u;
    uint32_t held = 0;
    uint64_t real = 0;                        /* sender frames elapsed */
    double consumed = 0;                      /* fractional frames the DAC owes */

    /* Prime to the target. */
    for (unsigned i = 0; i < TARGET / PACKET; i++) {
        uint32_t node_at_arrival = epoch + (uint32_t)((double)real * rate);
        oal_phase_on_packet(&phase, sender, PACKET, node_at_arrival,
                            (uint64_t)real * 1000000ull / RATE, held);
        sender += PACKET;
        held += PACKET;
        real += PACKET;
    }

    for (unsigned i = 0; i < 300 * RATE / PACKET; i++) {
        uint32_t node_at_arrival = epoch + (uint32_t)((double)real * rate);
        oal_phase_on_packet(&phase, sender, PACKET, node_at_arrival,
                            (uint64_t)real * 1000000ull / RATE, held);
        sender += PACKET;
        held += PACKET;

        /* The DAC eats slightly more than a packet per packet-time. */
        consumed += (double)PACKET * rate;
        uint32_t whole = (uint32_t)consumed;
        consumed -= whole;
        if (whole > held) { whole = held; }
        held -= whole;
        oal_phase_on_played(&phase, whole);

        real += PACKET;
    }

    /* 300 s at 100 ppm is 30 ms — the ring should have drained by that. */
    int32_t depth_error = (int32_t)held - (int32_t)TARGET;
    CHECK(depth_error < -1300 && depth_error > -1600,
          "the ring should have drained about 1440 frames, not %d", depth_error);

    uint32_t node_now = epoch + (uint32_t)((double)real * rate);
    int32_t error = 0;
    CHECK(oal_phase_error(&phase, held, node_now, TARGET, &error), "no reading");
    CHECK(error - depth_error > -200 && error - depth_error < 200,
          "drift should be reported once: phase says %d frames, the ring says %d. "
          "Twice that would mean the delay estimate is not following the clock",
          error, depth_error);
}

/** Nothing is claimed before there is anything to claim it from. */
static void Nothing_is_reported_before_the_first_packet(void)
{
    oal_phase_t phase;
    oal_phase_reset(&phase);

    uint32_t position = 0;
    int32_t error = 0;
    CHECK(!oal_phase_position(&phase, 0, &position), "position from nothing");
    CHECK(!oal_phase_error(&phase, 0, 12345u, TARGET, &error), "error from nothing");
}

/**
 * The sender's timestamp wraps every 24.9 hours, and a house system is
 * expected to be playing when it does.
 */
static void The_timeline_survives_its_own_wrap(void)
{
    rig_t r;
    /* Start so the wrap lands in the middle of the run. */
    rig_init(&r, 0xFFFFFFFFu - (TARGET + 40u * PACKET), 606060u);
    for (unsigned i = 0; i < TARGET / PACKET; i++) { arrive(&r, 0); tick(&r, PACKET); }

    for (unsigned i = 0; i < 200; i++) {
        arrive(&r, 0);
        play(&r, PACKET);
        tick(&r, PACKET);
        int32_t error = 0;
        CHECK(oal_phase_error(&r.phase, r.held, r.node_rtp, TARGET, &error),
              "lost the reading at step %u", i);
        CHECK(error > -3 && error < 3,
              "step %u across the wrap: expected zero, got %d", i, error);
        CHECK(r.phase.breaks == 0, "the wrap must not be mistaken for a break");
    }
}

int main(void)
{
    A_link_at_the_target_reads_zero();
    Bursts_do_not_move_the_phase();
    Holding_extra_audio_reads_as_late();
    Holding_less_audio_reads_as_early();
    Two_nodes_agree_without_talking_to_each_other();
    A_restart_is_noticed_and_waited_out();
    A_hole_left_by_loss_is_a_break();
    Clock_drift_is_reported_once();
    Nothing_is_reported_before_the_first_packet();
    The_timeline_survives_its_own_wrap();

    if (failures != 0) {
        printf("%d check(s) failed\n", failures);
        return 1;
    }
    printf("phase tracker: all checks passed\n");
    return 0;
}
