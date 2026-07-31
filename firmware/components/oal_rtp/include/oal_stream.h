#pragma once

#include <stddef.h>

#include "esp_err.h"
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

/** Most consumers one producer will feed; decision 2 expects strain near 4. */
#define OAL_STREAM_MAX_DESTINATIONS 8

typedef struct {
    char destinations[OAL_STREAM_MAX_DESTINATIONS][16]; /* dotted-quad */
    size_t destination_count;
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

void oal_stream_consumer_get(oal_stream_consumer_state_t *out);

/** Clears the counters without dropping the socket, to begin a measurement. */
void oal_stream_consumer_reset(void);

#ifdef __cplusplus
}
#endif
