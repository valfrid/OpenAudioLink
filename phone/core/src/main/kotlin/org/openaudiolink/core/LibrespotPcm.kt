package org.openaudiolink.core

/**
 * What comes out of librespot's pipe.
 *
 * `--backend pipe` writes raw interleaved PCM to stdout in whatever
 * `--format` names, with no header and no framing — so the reader has to
 * know the width and the byte order, and getting either wrong produces
 * something that is still recognisably music (quieter, noisier, or an
 * octave out) rather than an error anybody notices.
 *
 * S16 is what this app asks for. It is what Spotify delivers anyway —
 * Ogg Vorbis decoded to 16-bit — so a wider pipe would carry padding, and
 * at 44.1 kHz stereo it is 1.4 Mbit/s through a pipe on a phone, which is
 * worth not doubling for nothing.
 */
object LibrespotPcm {

    /** Bytes per sample of the `--format S16` this app asks librespot for. */
    const val S16_WIDTH = 2

    /**
     * Decodes signed 16-bit little-endian into floats in [-1, 1].
     *
     * @return samples written. A trailing odd byte is left undecoded: a
     * half sample taken now would put the channels out of step for the rest
     * of the stream, which is a swap that never recovers rather than one
     * bad sample. The caller carries the remainder into the next read.
     */
    fun decodeS16(bytes: ByteArray, length: Int, out: FloatArray): Int {
        val samples = minOf(length / S16_WIDTH, out.size)
        for (i in 0 until samples) {
            val low = bytes[i * 2].toInt() and 0xFF
            val high = bytes[i * 2 + 1].toInt()          // signed: carries the sign
            val value = (high shl 8) or low
            /*
             * Divided by 32768, not 32767.
             *
             * It makes -32768 exactly -1.0 and full-scale positive a hair
             * under, which is the same asymmetry two's complement has and
             * the same convention `L24.fromFloat` uses on the way out. The
             * other choice would let a full-scale negative sample come out
             * marginally past -1.0 and clip on conversion.
             */
            out[i] = value / 32768f
        }
        return samples
    }
}
