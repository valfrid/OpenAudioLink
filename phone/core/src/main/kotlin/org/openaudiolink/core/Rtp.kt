package org.openaudiolink.core

/**
 * The wire format, exactly as `protocol/AUDIO-RTP.md` specifies it.
 *
 * This file has no Android in it on purpose. The packets a producer emits
 * are the one part of a phone app that cannot be debugged by looking at
 * the screen — a byte order mistake is "loud static" on a speaker in
 * another room — so the bytes are built here and checked on the host,
 * the same discipline the firmware uses for its own arithmetic.
 */
object Rtp {
    /** L24, 48 kHz, stereo. Decision 13: one wire rate. */
    const val SAMPLE_RATE = 48_000
    const val CHANNELS = 2
    const val BYTES_PER_SAMPLE = 3

    /** 5 ms. The OpenAudioLink default, not the AES67 1 ms baseline. */
    const val FRAMES_PER_PACKET = 240

    /** Dynamic payload type, declared in SDP. */
    const val PAYLOAD_TYPE = 96

    const val HEADER_BYTES = 12
    const val PAYLOAD_BYTES = FRAMES_PER_PACKET * CHANNELS * BYTES_PER_SAMPLE  // 1440
    const val PACKET_BYTES = HEADER_BYTES + PAYLOAD_BYTES                      // 1452

    /** Where a consumer listens from boot; no handshake precedes a stream. */
    const val DEFAULT_PORT = 41100
}

/**
 * One stream's sequence, timestamp and SSRC.
 *
 * The timestamp is a *media* clock: it advances by exactly the frames sent
 * and says nothing about when the packet left. That is what lets a
 * consumer place itself on the sender's timeline rather than on its own
 * guess about the network, which is the property the whole synchronisation
 * design now rests on — so this counter must advance for silence and for
 * music alike, and must never be re-seated to "catch up" with a clock.
 *
 * Not thread-safe: one stream belongs to one sending thread.
 */
class RtpStream(
    ssrc: Int,
    startSequence: Int,
    startTimestamp: Int,
    private val payloadType: Int = Rtp.PAYLOAD_TYPE,
) {
    /** Stable for the stream's lifetime, random per stream. */
    val ssrc: Int = ssrc

    var sequence: Int = startSequence and 0xFFFF
        private set

    /** Unsigned on the wire; kept as Int and allowed to wrap. */
    var timestamp: Int = startTimestamp
        private set

    /**
     * Set on the first packet, and after a deliberate discontinuity.
     *
     * A consumer treats a marked packet as "this is a new thing", so it
     * must not be set for ordinary silence — only where the timeline
     * genuinely restarts.
     */
    private var markNext = true

    /** Marks the next packet, for a restart or a deliberate gap. */
    fun markDiscontinuity() {
        markNext = true
    }

    /**
     * Accounts for time that passed without a packet.
     *
     * The timestamp is a media clock and the class comment above is
     * emphatic about it: it advances by the frames *sampled*, not by the
     * packets sent. So a sender that deliberately stays quiet — nothing is
     * playing, see [SilenceGate] — still owes those frames to the clock,
     * and must hand them over here. Skipping this instead would make a
     * resumed stream claim the silence never happened, and a consumer that
     * places itself on the sender's timeline would seat the new audio
     * exactly as far in the past as the pause was long.
     *
     * The **sequence number does not move**, and that is not an oversight.
     * Sequence counts packets on the wire, and a receiver reads a gap in
     * it as loss; a producer that burned sequence numbers on packets it
     * chose not to send would be reporting its own silence as a broken
     * network.
     */
    fun skip(frames: Int) {
        require(frames > 0) { "a skip covers at least one frame" }
        timestamp += frames
    }

    /**
     * Writes a packet into @p out and advances the counters.
     *
     * @param payload L24 big-endian, interleaved left then right.
     * @param frames how many frames @p payload holds.
     * @return how many bytes of @p out were written.
     */
    fun write(out: ByteArray, payload: ByteArray, frames: Int): Int {
        val payloadBytes = frames * Rtp.CHANNELS * Rtp.BYTES_PER_SAMPLE
        require(frames > 0) { "a packet carries at least one frame" }
        require(payload.size >= payloadBytes) { "payload holds fewer than $frames frames" }
        require(out.size >= Rtp.HEADER_BYTES + payloadBytes) { "packet buffer too small" }

        // Version 2, no padding, no extension, no CSRCs.
        out[0] = 0x80.toByte()
        out[1] = ((if (markNext) 0x80 else 0) or (payloadType and 0x7F)).toByte()
        out[2] = (sequence ushr 8).toByte()
        out[3] = sequence.toByte()
        out[4] = (timestamp ushr 24).toByte()
        out[5] = (timestamp ushr 16).toByte()
        out[6] = (timestamp ushr 8).toByte()
        out[7] = timestamp.toByte()
        out[8] = (ssrc ushr 24).toByte()
        out[9] = (ssrc ushr 16).toByte()
        out[10] = (ssrc ushr 8).toByte()
        out[11] = ssrc.toByte()

        System.arraycopy(payload, 0, out, Rtp.HEADER_BYTES, payloadBytes)

        markNext = false
        sequence = (sequence + 1) and 0xFFFF
        timestamp += frames   // wraps at 2^32, which is ~24.9 hours

        return Rtp.HEADER_BYTES + payloadBytes
    }
}

/**
 * Float PCM to L24 big-endian.
 *
 * Big-endian is the trap the protocol document calls out by name: every
 * machine a producer is likely to run on is little-endian, and a stream
 * that "plays as loud static" is almost always this conversion missing.
 * There is a test that reads the bytes in order for exactly that reason.
 */
object L24 {
    /** 2^23 - 1: the largest positive value 24 bits hold. */
    const val FULL_SCALE = 8_388_607

    /** -2^23: and the smallest. Two's complement is not symmetric. */
    const val FLOOR = -8_388_608

    /**
     * The scale, which is the floor's magnitude rather than the ceiling's.
     *
     * So -1.0 lands exactly on the floor and +1.0 clamps one step below
     * the arithmetic result. Scaling by the ceiling instead would leave
     * the most negative code unreachable — a converter that can never
     * output one of its own values — and would put a rounding step
     * between -1.0 and the floor for no gain.
     */
    private const val SCALE = 8_388_608f

    /**
     * Converts interleaved float samples in [-1, 1] to L24.
     *
     * Values outside the range are clamped rather than allowed to wrap.
     * Wrapping turns one over-range sample into full-scale noise of the
     * opposite sign, which is a click on every speaker in the house; a
     * clamp is merely the loudest thing the format can say.
     *
     * @return bytes written.
     */
    fun fromFloat(samples: FloatArray, count: Int, out: ByteArray, offset: Int = 0): Int {
        var w = offset
        for (i in 0 until count) {
            val scaled = samples[i] * SCALE
            val v = when {
                scaled >= FULL_SCALE.toFloat() -> FULL_SCALE
                scaled <= FLOOR.toFloat() -> FLOOR
                else -> Math.round(scaled)
            }
            out[w++] = (v shr 16).toByte()
            out[w++] = (v shr 8).toByte()
            out[w++] = v.toByte()
        }
        return w - offset
    }

    /**
     * Converts interleaved 16-bit samples to L24.
     *
     * A left shift by eight, not a scale by 256: the low byte is zero and
     * saying so plainly is more honest than pretending to a precision the
     * source never had. This is the path a 16-bit file takes, and it is
     * why the format being 24-bit does not by itself make a stream
     * high-resolution.
     */
    fun fromShort(samples: ShortArray, count: Int, out: ByteArray, offset: Int = 0): Int {
        var w = offset
        for (i in 0 until count) {
            val v = samples[i].toInt() shl 8
            out[w++] = (v shr 16).toByte()
            out[w++] = (v shr 8).toByte()
            out[w++] = v.toByte()
        }
        return w - offset
    }
}
