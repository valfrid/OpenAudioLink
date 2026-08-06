#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Sample conversion between the wire and the I²S peripheral.
 *
 * These two formats disagree about almost everything, and every
 * disagreement is audible:
 *
 *  - RTP L24 (RFC 3190) is big-endian, three bytes per sample, most
 *    significant byte first.
 *  - The ESP32's I²S peripheral shifts out 32-bit words held in memory
 *    little-endian, most significant bit first on the wire.
 *
 * So a sample has to be read byte by byte and rebuilt, and the 24 bits
 * have to be left-justified into 32 so the DAC's first bit is the sample's
 * first bit. Getting the byte order wrong sounds like loud static; getting
 * the justification wrong sounds like a very quiet, very distorted version
 * of the right audio. Neither is subtle, and neither is something you want
 * to diagnose through a speaker.
 *
 * Free of ESP-IDF headers so it is tested on a host, like the rest of the
 * arithmetic in this project.
 */

/**
 * Reads one L24 big-endian sample as a signed value in
 * [-8388608, 8388607].
 */
int32_t oal_pcm_read_l24(const uint8_t *bytes);

/**
 * Converts interleaved L24 big-endian samples into the 32-bit words the
 * I²S peripheral sends, left-justified so the 24 bits occupy the top of
 * each word and the low byte is zero.
 *
 * @param payload big-endian L24, three bytes per sample
 * @param out     one word per sample
 * @param samples samples, not frames and not bytes
 */
void oal_pcm_l24_to_i2s(const uint8_t *payload, int32_t *out, size_t samples);

/**
 * Writes one signed sample as three big-endian L24 bytes.
 *
 * Values outside [-8388608, 8388607] are clamped rather than wrapped. A
 * clamp is a flat top on a loud passage; a wrap turns the loudest sample
 * into the quietest one of the opposite sign, which is a click on every
 * peak and sounds like a broken cable rather than like clipping.
 */
void oal_pcm_write_l24(int32_t value, uint8_t *bytes);

/**
 * The inverse of oal_pcm_l24_to_i2s: 32-bit words as the I²S peripheral
 * delivers them, left-justified with the sample in the top 24 bits, into
 * interleaved big-endian L24 for the wire.
 *
 * @param in      one word per sample, as read from the I²S peripheral
 * @param payload big-endian L24, three bytes per sample
 * @param samples samples, not frames and not bytes
 */
void oal_pcm_i2s_to_l24(const int32_t *in, uint8_t *payload, size_t samples);

#ifdef __cplusplus
}
#endif
