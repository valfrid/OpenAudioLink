#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Reception statistics for one RTP stream: what arrived, what did not, and
 * how unevenly it did.
 *
 * This is the measurement instrument for the link. A consumer with no DAC
 * attached can still answer every question that decides the architecture —
 * whether a producer sustains 200 packets per second with a busy radio,
 * what a given RSSI costs in loss, and how deep a jitter buffer has to be.
 * Nothing here touches audio or hardware.
 *
 * Loss and jitter follow RFC 3550 (appendices A.1 and A.8) so the numbers
 * mean the same as every other RTP tool's. Duplicates and reordering are
 * counted separately, which RFC 3550 does not do: on Wi-Fi they have
 * different causes and different fixes, and lumping them together hides
 * which one is happening.
 *
 * Loss is also measured in runs, not only in total, because the total
 * does not say what to do about it. Isolated single packets are 5 ms
 * gaps that concealment hides almost inaudibly; the same count arriving
 * as bursts of ten is a quarter-second dropout that only forward error
 * correction or a much deeper buffer can survive. One number, two
 * completely different engineering answers.
 *
 * Deliberately free of ESP-IDF headers so it compiles and runs on a host,
 * where its arithmetic is tested against synthetic sequences.
 */

/** Sequence numbers this far ahead start a new stream rather than count as loss. */
#define OAL_RTP_MAX_DROPOUT 3000

/** How far back a late packet is still recognised rather than treated as a restart. */
#define OAL_RTP_MAX_MISORDER 100

/** Packets in a row before a stream is believed. */
#define OAL_RTP_MIN_SEQUENTIAL 2

typedef struct {
    bool     valid;          /* false until probation passes */
    uint16_t probation;      /* packets still needed to believe the stream */

    uint32_t ssrc;
    bool     ssrc_known;

    uint16_t max_seq;        /* highest sequence number seen */
    uint32_t cycles;         /* wraps, in units of 65536 */
    uint32_t base_seq;       /* first sequence number of the stream */
    uint32_t bad_seq;        /* start of a suspected restart */

    uint64_t window;         /* which of the last 64 sequence numbers arrived */

    uint32_t received;       /* packets accepted */
    uint32_t loss_events;    /* gaps seen, however many packets each swallowed */
    uint32_t single_losses;  /* gaps of exactly one packet */
    uint32_t longest_gap;    /* worst run of consecutive missing packets */
    uint32_t duplicates;     /* the same sequence number more than once */
    uint32_t reordered;      /* arrived after a higher sequence number */
    uint32_t too_late;       /* older than the window can account for */
    uint32_t ssrc_changes;   /* a different source took over the stream */

    int32_t  transit;        /* last arrival-minus-timestamp, RFC 3550 A.8 */
    bool     transit_known;
    uint32_t jitter_x16;     /* smoothed jitter, scaled by 16 to keep precision */
} oal_rtp_stats_t;

/** Clears everything; call before the first packet. */
void oal_rtp_stats_reset(oal_rtp_stats_t *stats);

/**
 * Accounts for one received packet.
 *
 * @param seq        RTP sequence number, host order.
 * @param rtp_time   RTP timestamp of the packet, host order.
 * @param arrival    Arrival time in the *same units as rtp_time* — for the
 *                   48 kHz profile that is 48000 ticks per second. Jitter is
 *                   meaningless if the two use different clocks.
 * @param ssrc       Synchronisation source of the packet.
 * @return true if the packet counts towards playback, false if it was a
 *         duplicate or too late to use.
 */
bool oal_rtp_stats_on_packet(
    oal_rtp_stats_t *stats, uint16_t seq, uint32_t rtp_time, uint32_t arrival, uint32_t ssrc);

/** Packets the sequence numbers say should have arrived. */
uint32_t oal_rtp_stats_expected(const oal_rtp_stats_t *stats);

/** Expected minus received; never negative, since duplicates do not count. */
uint32_t oal_rtp_stats_lost(const oal_rtp_stats_t *stats);

/** Loss as parts per million, so a rate under 0.1% is still readable. */
uint32_t oal_rtp_stats_loss_ppm(const oal_rtp_stats_t *stats);

/** Mean packets per gap, scaled by 100. 100 means every gap was a single packet. */
uint32_t oal_rtp_stats_mean_gap_x100(const oal_rtp_stats_t *stats);

/** Smoothed interarrival jitter in RTP timestamp units (RFC 3550 A.8). */
uint32_t oal_rtp_stats_jitter(const oal_rtp_stats_t *stats);

/** Writes a JSON object of the counters. Returns length, or -1 if it will not fit. */
int oal_rtp_stats_to_json(const oal_rtp_stats_t *stats, char *out, size_t out_size);

/** Enough for oal_rtp_stats_to_json, including the terminator. */
#define OAL_RTP_STATS_JSON_MAX 384

#ifdef __cplusplus
}
#endif
