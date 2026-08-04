#pragma once

#include <stddef.h>

#include "esp_err.h"
#include "oal_destinations.h"
#include "oal_rtp.h"
#include "oal_rtp_stats.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Producer and Consumer audio paths (ARCHITECTURE.md section 2), driven by
 * a synthetic source so the network can be characterised before any ADC or
 * DAC exists. Which of these runs is decided by the node's roles in NVS,
 * not by the binary (decision 5).
 */

typedef struct {
    oal_destinations_t destinations;
    uint16_t port;
    oal_rtp_source_t source;
    uint32_t tone_hz;
} oal_stream_request_t;

typedef struct {
    bool running;
    size_t destination_count;
    uint16_t port;
    oal_rtp_source_t source;
    uint32_t packets_sent;     /* per destination replication counted once */
    uint32_t datagrams_sent;   /* one per destination per packet */
    uint32_t send_errors;      /* packets the radio never accepted */
    uint32_t send_retries;     /* transient refusals that a retry cleared */
    int      last_send_errno;  /* why the last refusal happened */
    uint32_t late_packets;     /* pacing slipped by more than one packet time */
    uint64_t started_at_us;
} oal_stream_producer_state_t;

/** Starts the producer task. Idempotent restart: a running stream is replaced. */
esp_err_t oal_stream_producer_start(const oal_stream_request_t *request);

/** Stops it. Safe when nothing is running. */
void oal_stream_producer_stop(void);

/*
 * The destination set can change while a stream runs. A Consumer joins by
 * asking, not by having been present when the music started (decision 9),
 * so a speaker switched on halfway through a record starts playing rather
 * than waiting for a restart.
 *
 * A late joiner simply begins wherever the stream has reached; RTP carries
 * the sequence and timestamp it needs, and the receiver's probation
 * handles arriving mid-stream. Nothing about the running stream changes —
 * no reset of sequence, timestamp or SSRC — because every other Consumer
 * is still playing it.
 *
 * These work whether or not a stream is running: the set is a property of
 * the Producer, not of the stream.
 */
oal_destinations_result_t oal_stream_producer_add_destination(const char *address);

/** Removes a destination. False when it was not in the set. */
bool oal_stream_producer_remove_destination(const char *address);

/** Copies the current destination set. */
void oal_stream_producer_destinations(oal_destinations_t *out);

void oal_stream_producer_get(oal_stream_producer_state_t *out);

typedef struct {
    bool listening;
    uint16_t port;
    oal_rtp_stats_t stats;
    uint32_t payload_errors;   /* samples differing from the pattern */
    uint32_t foreign_packets;  /* not our RTP profile */
    uint32_t last_ssrc;
    uint64_t last_packet_us;
} oal_stream_consumer_state_t;

/**
 * Starts listening for RTP. A consumer listens from boot rather than being
 * told to: it has nothing to configure, and a receiver that must be armed
 * before it can be sent to is a race every time a producer starts.
 */
esp_err_t oal_stream_consumer_start(uint16_t port);

/**
 * Where a received packet's audio goes after it has been counted.
 *
 * @param payload L24 big-endian stereo, which the sink may modify in place
 *                — the statistics and the pattern check are already done.
 * @param frames  frames, not bytes.
 *
 * Called from the receive task, so it must not block for long: this task
 * is the only thing emptying the socket, and a slow sink shows up later as
 * packet loss that the network did not cause.
 */
typedef void (*oal_stream_sink_t)(uint8_t *payload, size_t frames);

/**
 * Attaches an audio sink, or NULL to detach.
 *
 * A callback rather than a direct call into the playout code, so this
 * component keeps knowing nothing about DACs. It measures the network; a
 * node with no audio hardware still runs it, and the link measurements
 * were all made without one.
 */
void oal_stream_consumer_set_sink(oal_stream_sink_t sink);

void oal_stream_consumer_get(oal_stream_consumer_state_t *out);

/** Clears the counters without dropping the socket, to begin a measurement. */
void oal_stream_consumer_reset(void);

#ifdef __cplusplus
}
#endif
