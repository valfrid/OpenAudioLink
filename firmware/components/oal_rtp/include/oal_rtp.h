#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * The OpenAudioLink RTP profile (protocol/AUDIO-RTP.md): an AES67 subset,
 * L24 stereo at 48 kHz in 5 ms packets.
 *
 * Free of ESP-IDF headers so the packet arithmetic can be tested on a
 * host. Byte order is the trap this file exists to contain: RTP L24 is
 * big-endian while the ESP32 is little-endian, and a missing swap sounds
 * exactly like loud static.
 */

#define OAL_RTP_VERSION        2
#define OAL_RTP_PAYLOAD_TYPE   96      /* dynamic, declared in SDP */
#define OAL_RTP_SAMPLE_RATE    48000
#define OAL_RTP_CHANNELS       2
#define OAL_RTP_BYTES_PER_SAMPLE 3     /* L24 */
#define OAL_RTP_PTIME_MS       5
#define OAL_RTP_FRAMES_PER_PACKET 240  /* 5 ms at 48 kHz */
#define OAL_RTP_HEADER_BYTES   12
#define OAL_RTP_DEFAULT_PORT   41100

#define OAL_RTP_PAYLOAD_BYTES \
    (OAL_RTP_FRAMES_PER_PACKET * OAL_RTP_CHANNELS * OAL_RTP_BYTES_PER_SAMPLE)   /* 1440 */
#define OAL_RTP_PACKET_BYTES (OAL_RTP_HEADER_BYTES + OAL_RTP_PAYLOAD_BYTES)     /* 1452 */
#define OAL_RTP_PACKETS_PER_SECOND (1000 / OAL_RTP_PTIME_MS)                    /* 200 */

/** Fields of an RTP fixed header, host order. */
typedef struct {
    uint8_t  version;
    bool     marker;
    uint8_t  payload_type;
    uint16_t sequence;
    uint32_t timestamp;
    uint32_t ssrc;
} oal_rtp_header_t;

/**
 * Writes the 12-byte fixed header. Returns the bytes written, or -1 if
 * the buffer is too small. Contributing sources and extensions are not
 * used, so the header is always exactly OAL_RTP_HEADER_BYTES.
 */
int oal_rtp_header_write(const oal_rtp_header_t *header, uint8_t *out, size_t out_size);

/**
 * Parses a received packet's fixed header. Returns false for anything
 * that is not a version-2 RTP packet of the expected payload type, or
 * that is too short to carry a whole number of frames.
 */
bool oal_rtp_header_read(const uint8_t *packet, size_t length, oal_rtp_header_t *out);

/** Frames carried by a packet of this length, or 0 if it is not a whole number. */
uint32_t oal_rtp_frames_in(size_t length);

/*
 * The synthetic source. Ahead of an ADC there is nothing to capture, but
 * the network path can still be measured honestly: this generates the
 * exact profile a real source would, at the same rate and packet size.
 *
 * Two shapes, because they answer different questions:
 *
 *  - PATTERN is derived from the absolute frame index, so a consumer can
 *    recompute every sample it should have received and compare. That
 *    catches corruption, truncation and mis-ordered payloads — things a
 *    sequence-number check cannot see. It is the default.
 *  - TONE is a sine, which is useless for verification and the only one
 *    worth listening to once a DAC exists.
 */
typedef enum {
    OAL_RTP_SOURCE_PATTERN = 0,
    OAL_RTP_SOURCE_TONE,
    /**
     * Real audio from an ADC. Nothing in this component can produce it —
     * the producer asks a registered callback instead — but the kind
     * belongs here beside the others because it is what a stream *is*,
     * and the API reports it by name.
     */
    OAL_RTP_SOURCE_CAPTURE,
} oal_rtp_source_t;

/**
 * Fills one packet's payload with L24 big-endian stereo.
 *
 * @param first_frame absolute frame index of the first frame, which is the
 *                    packet's RTP timestamp — so the content of a packet
 *                    depends only on where it sits in the stream, never on
 *                    when it was generated.
 * @param tone_hz     used by OAL_RTP_SOURCE_TONE only.
 */
void oal_rtp_fill_payload(
    uint8_t *payload, oal_rtp_source_t source, uint32_t first_frame, uint32_t tone_hz);

/**
 * Counts samples that differ from what OAL_RTP_SOURCE_PATTERN would have
 * produced. Zero means the payload arrived byte-exact. Meaningless against
 * a tone producer, which is why the pattern is the default.
 */
uint32_t oal_rtp_verify_payload(const uint8_t *payload, size_t length, uint32_t first_frame);

/** The pattern's sample value for one frame and channel, low 24 bits used. */
uint32_t oal_rtp_pattern_sample(uint32_t frame, unsigned channel);

#ifdef __cplusplus
}
#endif
