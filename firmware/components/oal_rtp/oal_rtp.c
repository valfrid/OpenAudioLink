#include "oal_rtp.h"

#include <math.h>
#include <string.h>

/* Not M_PI: that is a POSIX extension, absent under a strict C11 dialect,
 * and this file is compiled both by the host test build and ESP-IDF. */
#define OAL_PI 3.14159265358979323846

int oal_rtp_header_write(const oal_rtp_header_t *header, uint8_t *out, size_t out_size)
{
    if (header == NULL || out == NULL || out_size < OAL_RTP_HEADER_BYTES) {
        return -1;
    }

    /* V=2, no padding, no extension, no contributing sources. */
    out[0] = (uint8_t)((header->version & 0x03) << 6);
    out[1] = (uint8_t)((header->marker ? 0x80 : 0x00) | (header->payload_type & 0x7F));
    out[2] = (uint8_t)(header->sequence >> 8);
    out[3] = (uint8_t)(header->sequence);
    out[4] = (uint8_t)(header->timestamp >> 24);
    out[5] = (uint8_t)(header->timestamp >> 16);
    out[6] = (uint8_t)(header->timestamp >> 8);
    out[7] = (uint8_t)(header->timestamp);
    out[8] = (uint8_t)(header->ssrc >> 24);
    out[9] = (uint8_t)(header->ssrc >> 16);
    out[10] = (uint8_t)(header->ssrc >> 8);
    out[11] = (uint8_t)(header->ssrc);
    return OAL_RTP_HEADER_BYTES;
}

uint32_t oal_rtp_frames_in(size_t length)
{
    if (length <= OAL_RTP_HEADER_BYTES) {
        return 0;
    }
    size_t payload = length - OAL_RTP_HEADER_BYTES;
    size_t frame_bytes = OAL_RTP_CHANNELS * OAL_RTP_BYTES_PER_SAMPLE;
    if (payload % frame_bytes != 0) {
        return 0; /* a partial frame means a truncated or foreign packet */
    }
    return (uint32_t)(payload / frame_bytes);
}

bool oal_rtp_header_read(const uint8_t *packet, size_t length, oal_rtp_header_t *out)
{
    if (packet == NULL || out == NULL || length < OAL_RTP_HEADER_BYTES) {
        return false;
    }

    uint8_t version = (uint8_t)(packet[0] >> 6);
    uint8_t csrc_count = (uint8_t)(packet[0] & 0x0F);
    bool has_extension = (packet[0] & 0x10) != 0;
    uint8_t payload_type = (uint8_t)(packet[1] & 0x7F);

    /* Only the shape this profile emits. Contributing sources and header
     * extensions would move the payload, and nothing here produces them,
     * so a packet carrying either is not ours. */
    if (version != OAL_RTP_VERSION || csrc_count != 0 || has_extension
        || payload_type != OAL_RTP_PAYLOAD_TYPE || oal_rtp_frames_in(length) == 0) {
        return false;
    }

    out->version = version;
    out->marker = (packet[1] & 0x80) != 0;
    out->payload_type = payload_type;
    out->sequence = (uint16_t)((packet[2] << 8) | packet[3]);
    out->timestamp = ((uint32_t)packet[4] << 24) | ((uint32_t)packet[5] << 16)
                   | ((uint32_t)packet[6] << 8) | packet[7];
    out->ssrc = ((uint32_t)packet[8] << 24) | ((uint32_t)packet[9] << 16)
              | ((uint32_t)packet[10] << 8) | packet[11];
    return true;
}

uint32_t oal_rtp_pattern_sample(uint32_t frame, unsigned channel)
{
    /*
     * A multiply by a large odd constant, so consecutive frames differ in
     * every bit rather than only the low ones — a payload that arrived
     * shifted, truncated or from the wrong offset then fails immediately
     * instead of matching for the first few bytes. Knuth's 2654435761.
     */
    uint32_t mixed = (frame * 2654435761u) ^ (channel * 0x9E3779B9u);
    return mixed & 0xFFFFFFu;
}

/* L24 is big-endian on the wire; the ESP32 is not. */
static void write_sample_be(uint8_t *out, uint32_t sample)
{
    out[0] = (uint8_t)(sample >> 16);
    out[1] = (uint8_t)(sample >> 8);
    out[2] = (uint8_t)(sample);
}

static uint32_t read_sample_be(const uint8_t *in)
{
    return ((uint32_t)in[0] << 16) | ((uint32_t)in[1] << 8) | in[2];
}

void oal_rtp_fill_payload(
    uint8_t *payload, oal_rtp_source_t source, uint32_t first_frame, uint32_t tone_hz)
{
    if (payload == NULL) {
        return;
    }

    for (uint32_t i = 0; i < OAL_RTP_FRAMES_PER_PACKET; i++) {
        uint32_t frame = first_frame + i;
        for (unsigned ch = 0; ch < OAL_RTP_CHANNELS; ch++) {
            uint32_t sample;
            if (source == OAL_RTP_SOURCE_TONE) {
                /* Half scale, so a downstream mixer has headroom. Phase is
                 * a function of the absolute frame index, so the tone is
                 * continuous across packets and across a restart. */
                double phase = 2.0 * OAL_PI * (double)tone_hz * (double)frame / OAL_RTP_SAMPLE_RATE;
                int32_t value = (int32_t)(sin(phase) * 4194303.0); /* 2^22 - 1 */
                sample = (uint32_t)value & 0xFFFFFFu;
            } else {
                sample = oal_rtp_pattern_sample(frame, ch);
            }
            write_sample_be(payload + (i * OAL_RTP_CHANNELS + ch) * OAL_RTP_BYTES_PER_SAMPLE,
                            sample);
        }
    }
}

uint32_t oal_rtp_verify_payload(const uint8_t *payload, size_t length, uint32_t first_frame)
{
    if (payload == NULL) {
        return 0;
    }

    size_t frame_bytes = OAL_RTP_CHANNELS * OAL_RTP_BYTES_PER_SAMPLE;
    uint32_t frames = (uint32_t)(length / frame_bytes);
    uint32_t errors = 0;

    for (uint32_t i = 0; i < frames; i++) {
        for (unsigned ch = 0; ch < OAL_RTP_CHANNELS; ch++) {
            const uint8_t *at = payload + (i * OAL_RTP_CHANNELS + ch) * OAL_RTP_BYTES_PER_SAMPLE;
            if (read_sample_be(at) != oal_rtp_pattern_sample(first_frame + i, ch)) {
                errors++;
            }
        }
    }
    return errors;
}
