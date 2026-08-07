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

/*
 * Volume.
 *
 * There is nowhere else for it to live. The PCM5102A has no volume
 * register — no I²C, no control interface at all, just an I²S input and
 * two analog outputs — so the only thing between the wire and the speaker
 * that can change the level is this CPU. Attenuating in the sender was the
 * alternative and is wrong for a house: one stream feeds every room, and
 * turning down the kitchen would turn down the living room with it.
 *
 * So: a multiply per sample on the consumer, per node, which is what makes
 * "quieter in the kitchen" mean what a person means by it.
 */

/**
 * Interleaving this file assumes. Stated here rather than taken from the
 * RTP profile, because this header is deliberately free of every other
 * header in the project so the arithmetic can be tested on a host.
 */
#define OAL_PCM_CHANNELS 2

/** Full scale. The gain a fresh node has until told otherwise. */
#define OAL_VOLUME_DEFAULT 100

/** The Q16 multiplier that leaves a sample untouched. */
#define OAL_GAIN_UNITY 65536

/**
 * Turns a 0-100 volume into a Q16 multiplier.
 *
 * Cubed, not linear. A linear slider spends three quarters of its travel
 * in a range that all sounds equally loud, because hearing is closer to
 * logarithmic than to linear — the classic "does nothing until the last
 * inch, then deafening" control. Cubing approximates the audio taper a
 * real volume pot has: half travel lands at about -18 dB, which is where
 * a physical knob's midpoint sits, and a tenth of travel at -60 dB.
 *
 * Integer throughout, so the same input gives the same gain on the node
 * and in a host test, and so nothing here needs an FPU.
 *
 * Values above 100 are clamped: this attenuates and never amplifies.
 * Amplifying digitally would clip the loud passages of an already
 * full-scale stream, which is the one failure mode a volume control must
 * not have.
 */
int32_t oal_pcm_gain_q16(uint8_t percent);

/**
 * Scales left-justified I²S words in place.
 *
 * @param samples one word per sample, as oal_pcm_l24_to_i2s produced them
 * @param count   samples, not frames and not bytes
 * @param gain_q16 from oal_pcm_gain_q16
 *
 * Unity is skipped rather than multiplied by one, so a node at full volume
 * — which is most of them, most of the time — pays nothing for this.
 */
void oal_pcm_apply_gain(int32_t *samples, size_t count, int32_t gain_q16);

/** What <see cref="oal_pcm_dbfs"/> returns for silence. */
#define OAL_DBFS_SILENT (-120)

/**
 * Turns a peak sample magnitude into whole decibels below full scale.
 *
 * The one measurement that answers "is anything actually plugged in".
 * Everything else the capture path reports — the rate, the buffer fill,
 * the read errors — is identical whether a record is playing or the cable
 * is lying on the floor, because they all count frames and frames arrive
 * either way. Only the level distinguishes an ADC that is working from an
 * input that is connected.
 *
 * @param peak the largest absolute sample seen, as a left-justified I²S
 *             word — so full scale is 2^31, not 2^23
 * @return 0 at full scale, negative below it, OAL_DBFS_SILENT for digital
 *         silence. Whole dB: a tenth of a decibel is below the resolution
 *         of the question being asked.
 */
int oal_pcm_dbfs(int32_t peak);

/**
 * The largest absolute value in one channel of an interleaved buffer.
 *
 * @param samples interleaved, one word per sample
 * @param count   samples, not frames
 * @param channel 0 for left, 1 for right
 *
 * Per channel because a turntable is the one source where half of it
 * failing is ordinary: a lifted ground, a bad RCA, a worn cartridge coil.
 * One number for both would report that as "quieter than expected".
 */
int32_t oal_pcm_peak(const int32_t *samples, size_t count, unsigned channel);

#ifdef __cplusplus
}
#endif
