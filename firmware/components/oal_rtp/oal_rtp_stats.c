#include "oal_rtp_stats.h"

#include <stdio.h>
#include <string.h>

#define SEQ_MOD 0x10000u
#define WINDOW_BITS 64

/* RFC 3550 A.1 init_seq: begins accounting at a sequence number. */
static void init_seq(oal_rtp_stats_t *stats, uint16_t seq)
{
    stats->base_seq = seq;
    stats->max_seq = seq;
    stats->bad_seq = SEQ_MOD + 1; /* impossible, so nothing matches it yet */
    stats->cycles = 0;
    stats->received = 0;
    stats->window = 0;
    stats->transit_known = false;
}

/* A believed stream starting at seq, with that packet counted. */
static void begin_stream(oal_rtp_stats_t *stats, uint16_t seq)
{
    init_seq(stats, seq);
    stats->valid = true;
    stats->probation = 0;
    stats->window = 1; /* bit 0 is max_seq, which just arrived */
    stats->received = 1;
}

/* The first packet from a source: wait for consecutive ones before
 * believing it, so a stray datagram cannot fix the base sequence number
 * far from the real one and make everything after it look lost. */
static void expect_stream(oal_rtp_stats_t *stats, uint16_t seq)
{
    init_seq(stats, seq);
    stats->valid = false;
    stats->max_seq = (uint16_t)(seq - 1);
    stats->probation = OAL_RTP_MIN_SEQUENTIAL;
}

void oal_rtp_stats_reset(oal_rtp_stats_t *stats)
{
    if (stats == NULL) {
        return;
    }
    memset(stats, 0, sizeof(*stats));
    stats->probation = OAL_RTP_MIN_SEQUENTIAL;
    stats->bad_seq = SEQ_MOD + 1;
}

/*
 * Records that a sequence number arrived, and reports whether it already
 * had. The window covers the 64 sequence numbers up to and including
 * max_seq, bit 0 being max_seq itself — the same shape as a replay window,
 * because it answers the same question.
 */
static bool seen_before(oal_rtp_stats_t *stats, int32_t delta)
{
    if (delta > 0) {
        /* Newer than anything seen. A jump wider than the window leaves the
         * skipped numbers unknown, which is exactly what loss looks like. */
        stats->window = (delta >= WINDOW_BITS) ? 1u : ((stats->window << delta) | 1u);
        return false;
    }

    uint32_t back = (uint32_t)(-delta);
    if (back >= WINDOW_BITS) {
        return false; /* beyond memory; the caller treats it as too late */
    }

    uint64_t bit = 1ull << back;
    if ((stats->window & bit) != 0) {
        return true;
    }
    stats->window |= bit;
    return false;
}

/* RFC 3550 A.8, in fixed point: jitter += (|d| - jitter) / 16. */
static void update_jitter(oal_rtp_stats_t *stats, uint32_t rtp_time, uint32_t arrival)
{
    int32_t transit = (int32_t)(arrival - rtp_time);

    if (!stats->transit_known) {
        stats->transit = transit;
        stats->transit_known = true;
        return;
    }

    int32_t d = transit - stats->transit;
    stats->transit = transit;
    if (d < 0) {
        d = -d;
    }

    stats->jitter_x16 += (uint32_t)d - ((stats->jitter_x16 + 8) >> 4);
}

bool oal_rtp_stats_on_packet(
    oal_rtp_stats_t *stats, uint16_t seq, uint32_t rtp_time, uint32_t arrival, uint32_t ssrc)
{
    if (stats == NULL) {
        return false;
    }

    /* A new source is a new stream. Measuring its sequence numbers against
     * the old one's would invent enormous loss out of nothing. */
    if (!stats->ssrc_known) {
        stats->ssrc = ssrc;
        stats->ssrc_known = true;
        expect_stream(stats, seq);
    } else if (ssrc != stats->ssrc) {
        uint32_t changes = stats->ssrc_changes;
        uint32_t duplicates = stats->duplicates;
        uint32_t reordered = stats->reordered;
        uint32_t too_late = stats->too_late;
        oal_rtp_stats_reset(stats);
        /* Counters that describe the link survive: a source restarting
         * does not undo the loss already measured. */
        stats->ssrc_changes = changes + 1;
        stats->duplicates = duplicates;
        stats->reordered = reordered;
        stats->too_late = too_late;
        stats->ssrc = ssrc;
        stats->ssrc_known = true;
        expect_stream(stats, seq);
    }

    if (stats->probation > 0) {
        if (seq == (uint16_t)(stats->max_seq + 1)) {
            stats->probation--;
            stats->max_seq = seq;
            if (stats->probation == 0) {
                begin_stream(stats, seq);
                update_jitter(stats, rtp_time, arrival);
                return true;
            }
        } else {
            stats->probation = OAL_RTP_MIN_SEQUENTIAL - 1;
            stats->max_seq = seq;
        }
        return false;
    }

    uint16_t udelta = (uint16_t)(seq - stats->max_seq);

    if (udelta == 0) {
        stats->duplicates++;
        return false;
    }

    if (udelta < OAL_RTP_MAX_DROPOUT) {
        /* In order, or a gap small enough to be ordinary loss. */
        if (udelta > 1) {
            /* Counted where the gap appears rather than at the end of the
             * run, so a burst is one event of many packets and not many
             * events of one. A reordered packet arriving later would make
             * this an overcount, which is why reordering is tracked
             * separately: if it is zero, these runs are real loss. */
            uint32_t gap = udelta - 1u;
            stats->loss_events++;
            if (gap == 1) {
                stats->single_losses++;
            }
            if (gap > stats->longest_gap) {
                stats->longest_gap = gap;
            }
        }
        if (seq < stats->max_seq) {
            stats->cycles += SEQ_MOD; /* sequence numbers wrapped */
        }
        seen_before(stats, (int32_t)udelta);
        stats->max_seq = seq;
        stats->received++;
        update_jitter(stats, rtp_time, arrival);
        return true;
    }

    if (udelta <= SEQ_MOD - OAL_RTP_MAX_MISORDER) {
        /* Too far ahead to be a gap. Either the source restarted or this is
         * a stray; one more packet in step with it decides which. */
        if (seq == (uint16_t)stats->bad_seq) {
            begin_stream(stats, seq);
            update_jitter(stats, rtp_time, arrival);
            return true;
        }
        stats->bad_seq = (seq + 1u) & (SEQ_MOD - 1u);
        stats->too_late++;
        return false;
    }

    /* Behind max_seq by no more than MAX_MISORDER: late, not lost. */
    int32_t back = (int32_t)(SEQ_MOD - udelta);
    if (seen_before(stats, -back)) {
        stats->duplicates++;
        return false;
    }
    if ((uint32_t)back >= WINDOW_BITS) {
        stats->too_late++;
        return false;
    }
    stats->reordered++;
    stats->received++;
    /* Deliberately excluded from the jitter estimate: RFC 3550 measures the
     * spacing of the stream as sent, and a packet that overtook another
     * would report a delay it never experienced. */
    return true;
}

uint32_t oal_rtp_stats_expected(const oal_rtp_stats_t *stats)
{
    if (stats == NULL || !stats->valid) {
        return 0;
    }
    return stats->cycles + stats->max_seq - stats->base_seq + 1;
}

uint32_t oal_rtp_stats_lost(const oal_rtp_stats_t *stats)
{
    uint32_t expected = oal_rtp_stats_expected(stats);
    if (stats == NULL || expected <= stats->received) {
        return 0;
    }
    return expected - stats->received;
}

uint32_t oal_rtp_stats_loss_ppm(const oal_rtp_stats_t *stats)
{
    uint32_t expected = oal_rtp_stats_expected(stats);
    if (expected == 0) {
        return 0;
    }
    return (uint32_t)(((uint64_t)oal_rtp_stats_lost(stats) * 1000000u) / expected);
}

uint32_t oal_rtp_stats_mean_gap_x100(const oal_rtp_stats_t *stats)
{
    if (stats == NULL || stats->loss_events == 0) {
        return 0;
    }
    return (uint32_t)(((uint64_t)oal_rtp_stats_lost(stats) * 100u) / stats->loss_events);
}

uint32_t oal_rtp_stats_jitter(const oal_rtp_stats_t *stats)
{
    return (stats == NULL) ? 0 : (stats->jitter_x16 >> 4);
}

int oal_rtp_stats_to_json(const oal_rtp_stats_t *stats, char *out, size_t out_size)
{
    if (stats == NULL || out == NULL) {
        return -1;
    }

    int len = snprintf(out, out_size,
                       "{\"received\":%u,\"expected\":%u,\"lost\":%u,\"lossPpm\":%u,"
                       "\"duplicates\":%u,\"reordered\":%u,\"tooLate\":%u,"
                       "\"jitter\":%u,\"ssrcChanges\":%u,"
                       "\"lossEvents\":%u,\"singleLosses\":%u,\"longestGap\":%u,"
                       "\"meanGapX100\":%u}",
                       (unsigned)stats->received, (unsigned)oal_rtp_stats_expected(stats),
                       (unsigned)oal_rtp_stats_lost(stats), (unsigned)oal_rtp_stats_loss_ppm(stats),
                       (unsigned)stats->duplicates, (unsigned)stats->reordered,
                       (unsigned)stats->too_late, (unsigned)oal_rtp_stats_jitter(stats),
                       (unsigned)stats->ssrc_changes,
                       (unsigned)stats->loss_events, (unsigned)stats->single_losses,
                       (unsigned)stats->longest_gap,
                       (unsigned)oal_rtp_stats_mean_gap_x100(stats));

    return (len > 0 && (size_t)len < out_size) ? len : -1;
}
