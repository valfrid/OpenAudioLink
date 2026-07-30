/*
 * Host tests for the RTP packet format and the synthetic source.
 *
 * Byte order is the trap protocol/AUDIO-RTP.md warns about: L24 is
 * big-endian on the wire and the ESP32 is not, and a missing swap sounds
 * like loud static rather than failing. Checking it here means the first
 * real stream is not also the first test of the byte order.
 */

#include "oal_rtp.h"

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

static void test_the_profile_matches_the_spec(void)
{
    TEST("the profile matches protocol/AUDIO-RTP.md");
    CHECK_EQ(OAL_RTP_FRAMES_PER_PACKET, 240);
    CHECK_EQ(OAL_RTP_PAYLOAD_BYTES, 1440);
    CHECK_EQ(OAL_RTP_PACKET_BYTES, 1452);
    CHECK_EQ(OAL_RTP_PACKETS_PER_SECOND, 200);
    /* Plus 28 bytes of IP and UDP headers, still inside a 1500-byte MTU
     * with room to spare — the document says do not exceed it. */
    CHECK(OAL_RTP_PACKET_BYTES + 28 <= 1500);
}

static void test_header_round_trips(void)
{
    TEST("header round trips");
    oal_rtp_header_t out = {
        .version = OAL_RTP_VERSION,
        .marker = true,
        .payload_type = OAL_RTP_PAYLOAD_TYPE,
        .sequence = 0xBEEF,
        .timestamp = 0x12345678,
        .ssrc = 0xDEADBEEF,
    };

    uint8_t packet[OAL_RTP_PACKET_BYTES] = { 0 };
    CHECK_EQ(oal_rtp_header_write(&out, packet, sizeof(packet)), OAL_RTP_HEADER_BYTES);

    oal_rtp_header_t in;
    CHECK(oal_rtp_header_read(packet, sizeof(packet), &in));
    CHECK_EQ(in.sequence, 0xBEEF);
    CHECK_EQ(in.timestamp, 0x12345678);
    CHECK_EQ(in.ssrc, 0xDEADBEEF);
    CHECK(in.marker);
    CHECK_EQ(in.payload_type, OAL_RTP_PAYLOAD_TYPE);
}

/* RFC 3550 puts these fields in network order; a little-endian write would
 * be read as nonsense by GStreamer and every other conforming receiver. */
static void test_header_fields_are_big_endian(void)
{
    TEST("header fields are big-endian");
    oal_rtp_header_t header = {
        .version = OAL_RTP_VERSION,
        .payload_type = OAL_RTP_PAYLOAD_TYPE,
        .sequence = 0x0102,
        .timestamp = 0x03040506,
        .ssrc = 0x0708090A,
    };

    uint8_t packet[OAL_RTP_HEADER_BYTES];
    oal_rtp_header_write(&header, packet, sizeof(packet));

    CHECK_EQ(packet[0], 0x80);                 /* V=2, no padding/extension/CSRC */
    CHECK_EQ(packet[1], OAL_RTP_PAYLOAD_TYPE); /* marker clear */
    CHECK_EQ(packet[2], 0x01); CHECK_EQ(packet[3], 0x02);
    CHECK_EQ(packet[4], 0x03); CHECK_EQ(packet[7], 0x06);
    CHECK_EQ(packet[8], 0x07); CHECK_EQ(packet[11], 0x0A);
}

static void test_the_marker_bit_is_separate_from_the_payload_type(void)
{
    TEST("the marker bit is separate from the payload type");
    oal_rtp_header_t header = {
        .version = OAL_RTP_VERSION,
        .payload_type = OAL_RTP_PAYLOAD_TYPE,
        .marker = true,
    };

    uint8_t packet[OAL_RTP_HEADER_BYTES];
    oal_rtp_header_write(&header, packet, sizeof(packet));

    CHECK_EQ(packet[1], 0x80 | OAL_RTP_PAYLOAD_TYPE);

    oal_rtp_header_t in;
    CHECK(oal_rtp_header_read(packet, OAL_RTP_PACKET_BYTES, &in));
    CHECK_EQ(in.payload_type, OAL_RTP_PAYLOAD_TYPE);
    CHECK(in.marker);
}

static void test_foreign_packets_are_rejected(void)
{
    TEST("foreign packets are rejected");
    uint8_t packet[OAL_RTP_PACKET_BYTES] = { 0 };
    oal_rtp_header_t header = {
        .version = OAL_RTP_VERSION, .payload_type = OAL_RTP_PAYLOAD_TYPE,
    };
    oal_rtp_header_write(&header, packet, sizeof(packet));
    oal_rtp_header_t in;

    CHECK(oal_rtp_header_read(packet, sizeof(packet), &in));

    uint8_t wrong_version[OAL_RTP_PACKET_BYTES];
    memcpy(wrong_version, packet, sizeof(packet));
    wrong_version[0] = 0x40; /* version 1 */
    CHECK(!oal_rtp_header_read(wrong_version, sizeof(packet), &in));

    uint8_t wrong_payload[OAL_RTP_PACKET_BYTES];
    memcpy(wrong_payload, packet, sizeof(packet));
    wrong_payload[1] = 10; /* L16 stereo, someone else's stream */
    CHECK(!oal_rtp_header_read(wrong_payload, sizeof(packet), &in));

    /* Contributing sources would move the payload, and nothing here emits
     * them, so such a packet is not ours to decode. */
    uint8_t with_csrc[OAL_RTP_PACKET_BYTES];
    memcpy(with_csrc, packet, sizeof(packet));
    with_csrc[0] = 0x81;
    CHECK(!oal_rtp_header_read(with_csrc, sizeof(packet), &in));

    CHECK(!oal_rtp_header_read(packet, OAL_RTP_HEADER_BYTES - 1, &in));
}

/* A payload that does not divide into whole frames is truncated or from
 * another format; decoding it would emit a half sample as audio. */
static void test_partial_frames_are_rejected(void)
{
    TEST("partial frames are rejected");
    CHECK_EQ(oal_rtp_frames_in(OAL_RTP_PACKET_BYTES), OAL_RTP_FRAMES_PER_PACKET);
    CHECK_EQ(oal_rtp_frames_in(OAL_RTP_HEADER_BYTES), 0);
    CHECK_EQ(oal_rtp_frames_in(OAL_RTP_PACKET_BYTES - 1), 0);
    CHECK_EQ(oal_rtp_frames_in(OAL_RTP_HEADER_BYTES + 6), 1);
}

static void test_pattern_payload_verifies(void)
{
    TEST("pattern payload verifies");
    uint8_t payload[OAL_RTP_PAYLOAD_BYTES];
    oal_rtp_fill_payload(payload, OAL_RTP_SOURCE_PATTERN, 480000, 0);

    CHECK_EQ(oal_rtp_verify_payload(payload, sizeof(payload), 480000), 0);
}

/* The content of a packet depends only on where it sits in the stream, so
 * a payload checked against the wrong timestamp must not pass. */
static void test_pattern_is_tied_to_the_frame_index(void)
{
    TEST("pattern is tied to the frame index");
    uint8_t payload[OAL_RTP_PAYLOAD_BYTES];
    oal_rtp_fill_payload(payload, OAL_RTP_SOURCE_PATTERN, 480000, 0);

    CHECK_EQ(oal_rtp_verify_payload(payload, sizeof(payload), 480240),
             OAL_RTP_FRAMES_PER_PACKET * OAL_RTP_CHANNELS);
}

static void test_corruption_is_detected(void)
{
    TEST("corruption is detected");
    uint8_t payload[OAL_RTP_PAYLOAD_BYTES];
    oal_rtp_fill_payload(payload, OAL_RTP_SOURCE_PATTERN, 0, 0);

    payload[7] ^= 0xFF; /* one byte inside the second sample */

    CHECK_EQ(oal_rtp_verify_payload(payload, sizeof(payload), 0), 1);
}

/* Channels must differ, or a swapped or mono-duplicated stream would pass
 * verification while being wrong. */
static void test_the_channels_carry_different_samples(void)
{
    TEST("the channels carry different samples");
    CHECK(oal_rtp_pattern_sample(1000, 0) != oal_rtp_pattern_sample(1000, 1));
    CHECK(oal_rtp_pattern_sample(1000, 0) != oal_rtp_pattern_sample(1001, 0));
}

/* Consecutive frames must differ in the high bits too: a payload arriving
 * shifted by a few bytes should fail immediately, not match for a while. */
static void test_consecutive_samples_differ_widely(void)
{
    TEST("consecutive samples differ widely");
    uint32_t a = oal_rtp_pattern_sample(5000, 0);
    uint32_t b = oal_rtp_pattern_sample(5001, 0);

    CHECK((a & 0xFF0000u) != (b & 0xFF0000u));
    CHECK_EQ(a >> 24, 0); /* 24-bit samples only */
    CHECK_EQ(b >> 24, 0);
}

/* A sine is continuous across packet boundaries because its phase comes
 * from the absolute frame index, not from a per-packet counter. */
static void test_tone_is_continuous_across_packets(void)
{
    TEST("tone is continuous across packets");
    uint8_t first[OAL_RTP_PAYLOAD_BYTES];
    uint8_t second[OAL_RTP_PAYLOAD_BYTES];
    oal_rtp_fill_payload(first, OAL_RTP_SOURCE_TONE, 0, 1000);
    oal_rtp_fill_payload(second, OAL_RTP_SOURCE_TONE, OAL_RTP_FRAMES_PER_PACKET, 1000);

    /* The tone repeats every 48 samples at 1 kHz, and 240 is a multiple of
     * 48, so the second packet must begin exactly where the first did. */
    CHECK_EQ(memcmp(first, second, OAL_RTP_PAYLOAD_BYTES), 0);
}

int main(void)
{
    test_the_profile_matches_the_spec();
    test_header_round_trips();
    test_header_fields_are_big_endian();
    test_the_marker_bit_is_separate_from_the_payload_type();
    test_foreign_packets_are_rejected();
    test_partial_frames_are_rejected();
    test_pattern_payload_verifies();
    test_pattern_is_tied_to_the_frame_index();
    test_corruption_is_detected();
    test_the_channels_carry_different_samples();
    test_consecutive_samples_differ_widely();
    test_tone_is_continuous_across_packets();

    if (failures > 0) {
        printf("\n%d check(s) failed\n", failures);
        return 1;
    }
    printf("\nall checks passed\n");
    return 0;
}
