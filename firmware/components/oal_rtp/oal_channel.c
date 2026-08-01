#include "oal_channel.h"

#include <string.h>

#define BYTES_PER_SAMPLE 3
#define CHANNELS 2
#define SIGN_BIT 0x800000
#define WRAP     0x1000000

const char *oal_channel_name(oal_channel_t channel)
{
    switch (channel) {
    case OAL_CHANNEL_STEREO: return "stereo";
    case OAL_CHANNEL_MONO:   return "mono";
    case OAL_CHANNEL_LEFT:   return "left";
    case OAL_CHANNEL_RIGHT:  return "right";
    default:                 return "stereo";
    }
}

bool oal_channel_parse(const char *name, oal_channel_t *out)
{
    if (name == NULL || out == NULL) {
        return false;
    }
    static const oal_channel_t all[] = {
        OAL_CHANNEL_STEREO, OAL_CHANNEL_MONO, OAL_CHANNEL_LEFT, OAL_CHANNEL_RIGHT,
    };
    for (size_t i = 0; i < sizeof(all) / sizeof(all[0]); i++) {
        if (strcmp(name, oal_channel_name(all[i])) == 0) {
            *out = all[i];
            return true;
        }
    }
    return false;
}

/*
 * L24 is big-endian two's complement (RFC 3190). Sign-extending by
 * subtraction rather than by shifting: a left-then-right shift of a signed
 * value is implementation-defined in C11, and this costs nothing.
 */
static int32_t read_signed(const uint8_t *in)
{
    int32_t value = ((int32_t)in[0] << 16) | ((int32_t)in[1] << 8) | in[2];
    return (value & SIGN_BIT) != 0 ? value - WRAP : value;
}

static void write_signed(uint8_t *out, int32_t value)
{
    uint32_t raw = (uint32_t)value & 0xFFFFFFu;
    out[0] = (uint8_t)(raw >> 16);
    out[1] = (uint8_t)(raw >> 8);
    out[2] = (uint8_t)raw;
}

void oal_channel_apply(uint8_t *payload, size_t frames, oal_channel_t channel)
{
    if (payload == NULL || channel == OAL_CHANNEL_STEREO) {
        return;
    }

    for (size_t i = 0; i < frames; i++) {
        uint8_t *left = payload + (i * CHANNELS) * BYTES_PER_SAMPLE;
        uint8_t *right = left + BYTES_PER_SAMPLE;

        int32_t chosen;
        switch (channel) {
        case OAL_CHANNEL_LEFT:
            chosen = read_signed(left);
            break;
        case OAL_CHANNEL_RIGHT:
            chosen = read_signed(right);
            break;
        case OAL_CHANNEL_MONO:
        default:
            /* Halved, not summed. Two channels near full scale sum past
             * full scale and clip; the 3 dB this costs comes back at the
             * amplifier, where clipping never does. Both operands are
             * within 24 bits, so the sum cannot overflow int32_t. */
            chosen = (read_signed(left) + read_signed(right)) / 2;
            break;
        }

        write_signed(left, chosen);
        write_signed(right, chosen);
    }
}
