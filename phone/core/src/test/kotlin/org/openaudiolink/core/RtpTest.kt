package org.openaudiolink.core

import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

/**
 * The packets, checked byte by byte against `protocol/AUDIO-RTP.md`.
 *
 * A producer is correct when software that has never heard of
 * OpenAudioLink plays its stream, and the way to get there is to build the
 * header from the specification and then read it back one field at a time.
 * The alternative — pointing it at a speaker and listening — cannot tell a
 * wrong payload type from a wrong byte order from a wrong port.
 */
class RtpTest {

    private fun stream(marker: Boolean = true) =
        RtpStream(ssrc = 0x11223344, startSequence = 0xFFFE, startTimestamp = 0x7FFFFFF0)
            .also { if (!marker) it.write(ByteArray(Rtp.PACKET_BYTES), ByteArray(Rtp.PAYLOAD_BYTES), Rtp.FRAMES_PER_PACKET) }

    @Test
    fun `the profile matches the protocol document`() {
        assertEquals(48_000, Rtp.SAMPLE_RATE)
        assertEquals(2, Rtp.CHANNELS)
        assertEquals(240, Rtp.FRAMES_PER_PACKET, "5 ms at 48 kHz")
        assertEquals(1440, Rtp.PAYLOAD_BYTES, "240 frames, stereo, 24-bit")
        assertEquals(1452, Rtp.PACKET_BYTES, "and it must fit a 1500-byte MTU")
        assertEquals(96, Rtp.PAYLOAD_TYPE)
        assertEquals(41100, Rtp.DEFAULT_PORT)
    }

    @Test
    fun `the header is the twelve bytes RFC 3550 specifies`() {
        val out = ByteArray(Rtp.PACKET_BYTES)
        val length = stream().write(out, ByteArray(Rtp.PAYLOAD_BYTES), Rtp.FRAMES_PER_PACKET)

        assertEquals(Rtp.PACKET_BYTES, length)
        assertEquals(0x80.toByte(), out[0], "version 2, no padding, no extension, no CSRCs")
        assertEquals(0xE0.toByte(), out[1], "marker set on the first packet, payload type 96")
        assertEquals(0xFF.toByte(), out[2]); assertEquals(0xFE.toByte(), out[3])
        assertEquals(0x7F.toByte(), out[4]); assertEquals(0xFF.toByte(), out[5])
        assertEquals(0xFF.toByte(), out[6]); assertEquals(0xF0.toByte(), out[7])
        assertEquals(0x11.toByte(), out[8]); assertEquals(0x22.toByte(), out[9])
        assertEquals(0x33.toByte(), out[10]); assertEquals(0x44.toByte(), out[11])
    }

    @Test
    fun `the marker is set once, not on every packet`() {
        val s = stream()
        val out = ByteArray(Rtp.PACKET_BYTES)
        s.write(out, ByteArray(Rtp.PAYLOAD_BYTES), Rtp.FRAMES_PER_PACKET)
        assertEquals(0xE0.toByte(), out[1])
        s.write(out, ByteArray(Rtp.PAYLOAD_BYTES), Rtp.FRAMES_PER_PACKET)
        assertEquals(0x60.toByte(), out[1], "payload type 96 alone")
    }

    @Test
    fun `a deliberate discontinuity marks the next packet and only that one`() {
        val s = stream()
        val out = ByteArray(Rtp.PACKET_BYTES)
        s.write(out, ByteArray(Rtp.PAYLOAD_BYTES), Rtp.FRAMES_PER_PACKET)
        s.write(out, ByteArray(Rtp.PAYLOAD_BYTES), Rtp.FRAMES_PER_PACKET)
        s.markDiscontinuity()
        s.write(out, ByteArray(Rtp.PAYLOAD_BYTES), Rtp.FRAMES_PER_PACKET)
        assertEquals(0xE0.toByte(), out[1])
        s.write(out, ByteArray(Rtp.PAYLOAD_BYTES), Rtp.FRAMES_PER_PACKET)
        assertEquals(0x60.toByte(), out[1])
    }

    @Test
    fun `the sequence wraps at sixteen bits`() {
        val s = stream()
        val out = ByteArray(Rtp.PACKET_BYTES)
        val payload = ByteArray(Rtp.PAYLOAD_BYTES)
        s.write(out, payload, Rtp.FRAMES_PER_PACKET)     // 0xFFFE
        s.write(out, payload, Rtp.FRAMES_PER_PACKET)     // 0xFFFF
        assertEquals(0xFFFF, s.sequence.let { (out[2].toInt() and 0xFF shl 8) or (out[3].toInt() and 0xFF) })
        s.write(out, payload, Rtp.FRAMES_PER_PACKET)     // 0x0000
        assertEquals(0, (out[2].toInt() and 0xFF shl 8) or (out[3].toInt() and 0xFF))
        assertEquals(1, s.sequence)
    }

    /*
     * The media clock advances by frames sent, not by time passed. It is
     * what a consumer measures its own position against, so a sender that
     * re-seated it to "catch up" with a wall clock would move every
     * speaker in the house at once — which is the fault the whole
     * timestamp-anchored design exists to remove.
     */
    @Test
    fun `the timestamp advances by frames and wraps at thirty-two bits`() {
        val s = RtpStream(ssrc = 1, startSequence = 0, startTimestamp = -0x10)   // 0xFFFFFFF0
        val out = ByteArray(Rtp.PACKET_BYTES)
        val payload = ByteArray(Rtp.PAYLOAD_BYTES)

        s.write(out, payload, Rtp.FRAMES_PER_PACKET)
        assertEquals(-0x10 + 240, s.timestamp, "240 frames later, having wrapped")

        val before = s.timestamp
        s.write(out, payload, Rtp.FRAMES_PER_PACKET)
        assertEquals(before + 240, s.timestamp)
    }

    @Test
    fun `the ssrc is stable for the life of the stream`() {
        val s = stream()
        val out = ByteArray(Rtp.PACKET_BYTES)
        val first = ByteArray(4)
        s.write(out, ByteArray(Rtp.PAYLOAD_BYTES), Rtp.FRAMES_PER_PACKET)
        out.copyInto(first, 0, 8, 12)
        repeat(10) { s.write(out, ByteArray(Rtp.PAYLOAD_BYTES), Rtp.FRAMES_PER_PACKET) }
        assertContentEquals(first, out.copyOfRange(8, 12))
    }

    @Test
    fun `a packet must carry frames and fit its buffer`() {
        val s = stream()
        assertFailsWith<IllegalArgumentException> {
            s.write(ByteArray(Rtp.PACKET_BYTES), ByteArray(Rtp.PAYLOAD_BYTES), 0)
        }
        assertFailsWith<IllegalArgumentException> {
            s.write(ByteArray(100), ByteArray(Rtp.PAYLOAD_BYTES), Rtp.FRAMES_PER_PACKET)
        }
    }
}

/**
 * Byte order, which the protocol document calls "the classic
 * implementation trap" and which is worth a test of its own.
 *
 * Everything a producer runs on is little-endian; the wire is not. A
 * stream that plays as loud static is almost always this.
 */
class L24Test {

    @Test
    fun `samples are big-endian, most significant byte first`() {
        val out = ByteArray(3)
        L24.fromFloat(floatArrayOf(0.5f), 1, out)
        // 0.5 * 8388607 = 4194303.5, rounds to 4194304 = 0x400000
        assertEquals(0x40.toByte(), out[0])
        assertEquals(0x00.toByte(), out[1])
        assertEquals(0x00.toByte(), out[2])
    }

    @Test
    fun `full scale is the largest value twenty-four bits hold`() {
        val out = ByteArray(3)
        L24.fromFloat(floatArrayOf(1.0f), 1, out)
        assertEquals(0x7F.toByte(), out[0])
        assertEquals(0xFF.toByte(), out[1])
        assertEquals(0xFF.toByte(), out[2])
    }

    @Test
    fun `negative full scale is the two's complement floor`() {
        val out = ByteArray(3)
        L24.fromFloat(floatArrayOf(-1.0f), 1, out)
        assertEquals(0x80.toByte(), out[0], "the top bit means negative")
        assertEquals(0x00.toByte(), out[1])
        assertEquals(0x00.toByte(), out[2])
    }

    /*
     * A wrap turns one over-range sample into full-scale noise of the
     * opposite sign — a click on every speaker at once. A clamp is merely
     * the loudest thing the format can say.
     */
    @Test
    fun `over-range samples clamp rather than wrap`() {
        val out = ByteArray(6)
        L24.fromFloat(floatArrayOf(2.0f, -2.0f), 2, out)
        assertEquals(0x7F.toByte(), out[0]); assertEquals(0xFF.toByte(), out[1])
        assertEquals(0x80.toByte(), out[3]); assertEquals(0x00.toByte(), out[4])
    }

    @Test
    fun `silence is silence`() {
        val out = ByteArray(6) { 0x5A }
        L24.fromFloat(floatArrayOf(0f, -0f), 2, out)
        assertTrue(out.all { it == 0.toByte() })
    }

    @Test
    fun `interleaving is preserved`() {
        val out = ByteArray(6)
        L24.fromFloat(floatArrayOf(1.0f, -1.0f), 2, out)
        assertEquals(0x7F.toByte(), out[0], "left first")
        assertEquals(0x80.toByte(), out[3], "then right")
    }

    /*
     * A sixteen-bit source shifts left by eight; the low byte is zero and
     * saying so is more honest than a scale that pretends otherwise. It is
     * also why a 24-bit wire format does not by itself make a stream
     * high-resolution — the file decides that.
     */
    @Test
    fun `sixteen-bit sources are shifted, not scaled`() {
        val out = ByteArray(3)
        L24.fromShort(shortArrayOf(0x1234), 1, out)
        assertEquals(0x12.toByte(), out[0])
        assertEquals(0x34.toByte(), out[1])
        assertEquals(0x00.toByte(), out[2])
    }

    @Test
    fun `a whole packet converts to exactly the payload size`() {
        val samples = FloatArray(Rtp.FRAMES_PER_PACKET * Rtp.CHANNELS)
        val out = ByteArray(Rtp.PAYLOAD_BYTES)
        assertEquals(Rtp.PAYLOAD_BYTES, L24.fromFloat(samples, samples.size, out))
    }
}
