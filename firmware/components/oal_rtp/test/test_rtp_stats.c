/*
 * Host tests for the RTP reception statistics.
 *
 * The component has no ESP-IDF dependencies precisely so this can run
 * anywhere: the arithmetic is what decides whether a measured link is
 * healthy, and getting it wrong would send us chasing radio problems that
 * are really counting problems. Built and run by CI with plain cc.
 */

#include "oal_rtp_stats.h"

#include <stdio.h>
#include <string.h>

static int failures;
static const char *current_test;

#define CHECK(expr)                                                        \
    do {                                                                   \
        if (!(expr)) {                                                     \
            printf("  FAIL %s:%d: %s\n", current_test, __LINE__, #expr);   \
            failures++;                                                    \
        }                                                                  \
    } while (0)

#define CHECK_EQ(actual, expected)                                              \
    do {                                                                        \
        unsigned long a_ = (unsigned long)(actual);                             \
        unsigned long e_ = (unsigned long)(expected);                           \
        if (a_ != e_) {                                                         \
            printf("  FAIL %s:%d: %s was %lu, expected %lu\n",                  \
                   current_test, __LINE__, #actual, a_, e_);                    \
            failures++;                                                         \
        }                                                                       \
    } while (0)

#define TEST(name) current_test = name; printf("%s\n", name);

#define SSRC 0xDEADBEEFu

/* The 48 kHz, 5 ms profile: 240 frames per packet, 200 packets a second. */
#define FRAMES_PER_PACKET 240

/** Sends a run of perfectly spaced packets, as a healthy producer would. */
static void send_clean(oal_rtp_stats_t *stats, uint16_t first_seq, int count)
{
    for (int i = 0; i < count; i++) {
        uint32_t t = (uint32_t)(first_seq + i) * FRAMES_PER_PACKET;
        oal_rtp_stats_on_packet(stats, (uint16_t)(first_seq + i), t, t, SSRC);
    }
}

static void test_clean_stream_loses_nothing(void)
{
    TEST("clean stream loses nothing");
    oal_rtp_stats_t s;
    oal_rtp_stats_reset(&s);

    send_clean(&s, 1000, 200);

    /* Probation withholds the first packet, so accounting starts at the
     * second one and 199 are expected. */
    CHECK_EQ(oal_rtp_stats_expected(&s), 199);
    CHECK_EQ(s.received, 199);
    CHECK_EQ(oal_rtp_stats_lost(&s), 0);
    CHECK_EQ(oal_rtp_stats_loss_ppm(&s), 0);
    CHECK_EQ(s.duplicates, 0);
    CHECK_EQ(s.reordered, 0);
    CHECK_EQ(oal_rtp_stats_jitter(&s), 0);
}

static void test_a_gap_is_counted_as_loss(void)
{
    TEST("a gap is counted as loss");
    oal_rtp_stats_t s;
    oal_rtp_stats_reset(&s);

    send_clean(&s, 0, 10);      /* 0..9, accounting from 1 */
    send_clean(&s, 15, 10);     /* 10..14 never arrive */

    CHECK_EQ(oal_rtp_stats_lost(&s), 5);
    CHECK_EQ(oal_rtp_stats_expected(&s), 24);
    CHECK_EQ(s.received, 19);
    /* 5 of 24 is 208333 ppm; integer division truncates. */
    CHECK_EQ(oal_rtp_stats_loss_ppm(&s), 208333);
}

static void test_duplicates_are_not_counted_as_received(void)
{
    TEST("duplicates are not counted as received");
    oal_rtp_stats_t s;
    oal_rtp_stats_reset(&s);

    send_clean(&s, 100, 5);
    uint32_t before = s.received;

    /* The newest packet again, and an older one again. */
    CHECK(!oal_rtp_stats_on_packet(&s, 104, 104 * FRAMES_PER_PACKET, 104 * FRAMES_PER_PACKET, SSRC));
    CHECK(!oal_rtp_stats_on_packet(&s, 102, 102 * FRAMES_PER_PACKET, 102 * FRAMES_PER_PACKET, SSRC));

    CHECK_EQ(s.duplicates, 2);
    CHECK_EQ(s.received, before);
    CHECK_EQ(oal_rtp_stats_lost(&s), 0);
}

static void test_reordering_is_not_loss(void)
{
    TEST("reordering is not loss");
    oal_rtp_stats_t s;
    oal_rtp_stats_reset(&s);

    send_clean(&s, 500, 5);   /* 500..504 */
    /* 506 overtakes 505, which then arrives. */
    oal_rtp_stats_on_packet(&s, 506, 506 * FRAMES_PER_PACKET, 506 * FRAMES_PER_PACKET, SSRC);
    CHECK(oal_rtp_stats_on_packet(&s, 505, 505 * FRAMES_PER_PACKET, 505 * FRAMES_PER_PACKET, SSRC));

    CHECK_EQ(s.reordered, 1);
    CHECK_EQ(oal_rtp_stats_lost(&s), 0);
    CHECK_EQ(s.duplicates, 0);
}

static void test_sequence_wrap_is_not_a_restart(void)
{
    TEST("sequence wrap is not a restart");
    oal_rtp_stats_t s;
    oal_rtp_stats_reset(&s);

    /* Straight through 65535 -> 0, which is where naive counting explodes. */
    for (int i = 0; i < 40; i++) {
        uint16_t seq = (uint16_t)(65520 + i);
        uint32_t t = (uint32_t)i * FRAMES_PER_PACKET;
        oal_rtp_stats_on_packet(&s, seq, t, t, SSRC);
    }

    CHECK_EQ(oal_rtp_stats_lost(&s), 0);
    CHECK_EQ(oal_rtp_stats_expected(&s), 39);
    CHECK_EQ(s.cycles, 0x10000u);
}

static void test_a_new_source_restarts_accounting(void)
{
    TEST("a new source restarts accounting");
    oal_rtp_stats_t s;
    oal_rtp_stats_reset(&s);

    send_clean(&s, 40000, 20);

    /* A different producer, sequence numbers nowhere near the old ones.
     * Counting them against the old base would report absurd loss. */
    for (int i = 0; i < 20; i++) {
        uint32_t t = (uint32_t)i * FRAMES_PER_PACKET;
        oal_rtp_stats_on_packet(&s, (uint16_t)(7 + i), t, t, 0x12345678u);
    }

    CHECK_EQ(s.ssrc_changes, 1);
    CHECK_EQ(oal_rtp_stats_lost(&s), 0);
    CHECK_EQ(oal_rtp_stats_expected(&s), 19);
}

static void test_jitter_is_zero_for_even_spacing(void)
{
    TEST("jitter is zero for even spacing");
    oal_rtp_stats_t s;
    oal_rtp_stats_reset(&s);

    /* Arrival advances exactly one packet per packet: no jitter at all. */
    send_clean(&s, 0, 100);

    CHECK_EQ(oal_rtp_stats_jitter(&s), 0);
}

static void test_jitter_grows_with_uneven_arrival(void)
{
    TEST("jitter grows with uneven arrival");
    oal_rtp_stats_t s;
    oal_rtp_stats_reset(&s);

    /* Every other packet arrives 120 ticks (2.5 ms) late. */
    for (int i = 0; i < 200; i++) {
        uint32_t t = (uint32_t)i * FRAMES_PER_PACKET;
        uint32_t arrival = t + ((i % 2) ? 120u : 0u);
        oal_rtp_stats_on_packet(&s, (uint16_t)i, t, arrival, SSRC);
    }

    /* Alternating |d| of 120 settles at 120 under the RFC 3550 filter. */
    uint32_t jitter = oal_rtp_stats_jitter(&s);
    CHECK(jitter > 100);
    CHECK(jitter < 140);
}

static void test_a_steady_delay_is_not_jitter(void)
{
    TEST("a steady delay is not jitter");
    oal_rtp_stats_t s;
    oal_rtp_stats_reset(&s);

    /* Every packet 5000 ticks late, but evenly so. Jitter measures
     * variation, not latency; a constant offset must not register. */
    for (int i = 0; i < 100; i++) {
        uint32_t t = (uint32_t)i * FRAMES_PER_PACKET;
        oal_rtp_stats_on_packet(&s, (uint16_t)i, t, t + 5000u, SSRC);
    }

    CHECK_EQ(oal_rtp_stats_jitter(&s), 0);
}

static void test_probation_rejects_a_stray_packet(void)
{
    TEST("probation rejects a stray packet");
    oal_rtp_stats_t s;
    oal_rtp_stats_reset(&s);

    /* One packet from nowhere, then the real stream. If the stray fixed the
     * base sequence number, every later packet would look lost. */
    oal_rtp_stats_on_packet(&s, 9000, 0, 0, SSRC);
    send_clean(&s, 100, 20);

    CHECK_EQ(oal_rtp_stats_lost(&s), 0);
    CHECK(oal_rtp_stats_expected(&s) <= 20);
}

static void test_json_reports_the_counters(void)
{
    TEST("json reports the counters");
    oal_rtp_stats_t s;
    oal_rtp_stats_reset(&s);
    send_clean(&s, 0, 10);

    char json[OAL_RTP_STATS_JSON_MAX];
    int len = oal_rtp_stats_to_json(&s, json, sizeof(json));

    CHECK(len > 0);
    CHECK(strstr(json, "\"received\":9") != NULL);
    CHECK(strstr(json, "\"lost\":0") != NULL);
    CHECK(strstr(json, "\"jitter\":0") != NULL);

    /* A buffer too small must fail rather than emit truncated JSON. */
    char tiny[8];
    CHECK_EQ(oal_rtp_stats_to_json(&s, tiny, sizeof(tiny)), (unsigned long)-1);
}

static void test_nothing_received_reports_nothing(void)
{
    TEST("nothing received reports nothing");
    oal_rtp_stats_t s;
    oal_rtp_stats_reset(&s);

    CHECK_EQ(oal_rtp_stats_expected(&s), 0);
    CHECK_EQ(oal_rtp_stats_lost(&s), 0);
    CHECK_EQ(oal_rtp_stats_loss_ppm(&s), 0);
    CHECK_EQ(oal_rtp_stats_jitter(&s), 0);
}

/*
 * The total loss figure does not say what to do about it. Isolated single
 * packets are 5 ms gaps concealment hides; the same total arriving in
 * bursts is a dropout needing forward error correction. These separate
 * the two.
 */
static void test_isolated_losses_are_counted_as_single(void)
{
    TEST("isolated losses are counted as single");
    oal_rtp_stats_t s;
    oal_rtp_stats_reset(&s);

    /* Every fourth packet missing, never two in a row. */
    for (int i = 0; i < 100; i++) {
        if (i > 2 && i % 4 == 0) {
            continue;
        }
        uint32_t t = (uint32_t)i * FRAMES_PER_PACKET;
        oal_rtp_stats_on_packet(&s, (uint16_t)i, t, t, SSRC);
    }

    CHECK_EQ(s.longest_gap, 1);
    CHECK_EQ(s.loss_events, s.single_losses);
    CHECK_EQ(oal_rtp_stats_mean_gap_x100(&s), 100); /* exactly one per gap */
}

static void test_a_burst_is_one_event_of_many(void)
{
    TEST("a burst is one event of many");
    oal_rtp_stats_t s;
    oal_rtp_stats_reset(&s);

    send_clean(&s, 0, 10);      /* 0..9 */
    send_clean(&s, 20, 10);     /* 10..19 missing: one burst of ten */

    CHECK_EQ(s.loss_events, 1);
    CHECK_EQ(s.single_losses, 0);
    CHECK_EQ(s.longest_gap, 10);
    CHECK_EQ(oal_rtp_stats_lost(&s), 10);
    CHECK_EQ(oal_rtp_stats_mean_gap_x100(&s), 1000); /* ten packets per gap */
}

/* The same total loss, told apart by shape — the distinction the whole
 * measurement exists for. */
static void test_same_total_different_shape(void)
{
    TEST("same total, different shape");
    oal_rtp_stats_t scattered;
    oal_rtp_stats_reset(&scattered);
    for (int i = 0; i < 100; i++) {
        if (i > 2 && i % 20 == 0) continue;  /* 4 isolated losses */
        uint32_t t = (uint32_t)i * FRAMES_PER_PACKET;
        oal_rtp_stats_on_packet(&scattered, (uint16_t)i, t, t, SSRC);
    }

    oal_rtp_stats_t bursty;
    oal_rtp_stats_reset(&bursty);
    send_clean(&bursty, 0, 50);
    send_clean(&bursty, 54, 46);   /* 4 in a row */

    CHECK_EQ(oal_rtp_stats_lost(&scattered), oal_rtp_stats_lost(&bursty));
    CHECK(scattered.loss_events > bursty.loss_events);
    CHECK(scattered.longest_gap < bursty.longest_gap);
}

static void test_a_clean_stream_reports_no_gaps(void)
{
    TEST("a clean stream reports no gaps");
    oal_rtp_stats_t s;
    oal_rtp_stats_reset(&s);
    send_clean(&s, 0, 200);

    CHECK_EQ(s.loss_events, 0);
    CHECK_EQ(s.longest_gap, 0);
    CHECK_EQ(oal_rtp_stats_mean_gap_x100(&s), 0);
}

int main(void)
{
    test_clean_stream_loses_nothing();
    test_a_gap_is_counted_as_loss();
    test_duplicates_are_not_counted_as_received();
    test_reordering_is_not_loss();
    test_sequence_wrap_is_not_a_restart();
    test_a_new_source_restarts_accounting();
    test_jitter_is_zero_for_even_spacing();
    test_jitter_grows_with_uneven_arrival();
    test_a_steady_delay_is_not_jitter();
    test_probation_rejects_a_stray_packet();
    test_isolated_losses_are_counted_as_single();
    test_a_burst_is_one_event_of_many();
    test_same_total_different_shape();
    test_a_clean_stream_reports_no_gaps();
    test_json_reports_the_counters();
    test_nothing_received_reports_nothing();

    if (failures > 0) {
        printf("\n%d check(s) failed\n", failures);
        return 1;
    }
    printf("\nall checks passed\n");
    return 0;
}
