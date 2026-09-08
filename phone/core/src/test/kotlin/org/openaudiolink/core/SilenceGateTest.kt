package org.openaudiolink.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * Publishing is not playing, and the wire should show the difference.
 *
 * This came from a real report: pressing *Publish to Spotify* started the
 * packet counter immediately, before Spotify had been opened at all. The
 * pacer was doing exactly what it was built to do — the ring pads an empty
 * read with silence so a packet is always ready — and the result was a
 * phone streaming digital silence at 200 packets a second to speakers
 * nobody had asked to play anything.
 */
class SilenceGateTest {

    private val frame = Rtp.FRAMES_PER_PACKET

    @Test
    fun `it starts quiet, so publishing puts nothing on the wire`() {
        val gate = SilenceGate()
        assertTrue(gate.quiet)
        repeat(1000) {
            assertEquals(SilenceGate.Verdict.HOLD, gate.next(0))
        }
        assertTrue(gate.quiet, "a cast point nobody selected never starts sending")
    }

    @Test
    fun `the first audio resumes, and is marked`() {
        val gate = SilenceGate()
        gate.next(0)
        assertEquals(SilenceGate.Verdict.RESUME, gate.next(frame))
        assertFalse(gate.quiet)
        assertEquals(SilenceGate.Verdict.SEND, gate.next(frame), "only the first one is marked")
    }

    /*
     * PcmRing's argument, preserved: a decoder that stumbles for 20 ms
     * should cost 20 ms of silence rather than a gap, because a gap makes
     * every consumer re-seat itself and four speakers re-seat at four
     * slightly different moments.
     */
    @Test
    fun `a brief stumble mid-track is still sent as silence`() {
        val gate = SilenceGate(holdPackets = 40)
        gate.next(frame)

        repeat(40) {
            assertEquals(SilenceGate.Verdict.SEND, gate.next(0), "within the hold window")
        }
        assertFalse(gate.quiet)
        assertEquals(SilenceGate.Verdict.SEND, gate.next(frame), "no re-seat for a stumble")
    }

    /*
     * And the other side of the same line: a source that has actually
     * stopped has no timeline left to keep continuous.
     */
    @Test
    fun `a real stop goes quiet, and coming back is marked`() {
        val gate = SilenceGate(holdPackets = 40)
        gate.next(frame)

        repeat(41) { gate.next(0) }
        assertTrue(gate.quiet, "past the hold window this is a stop, not a stumble")
        assertEquals(SilenceGate.Verdict.HOLD, gate.next(0))

        assertEquals(SilenceGate.Verdict.RESUME, gate.next(frame))
    }

    @Test
    fun `the hold window restarts after every packet of audio`() {
        val gate = SilenceGate(holdPackets = 3)
        gate.next(frame)
        repeat(3) { gate.next(0) }
        gate.next(frame)                       // one real packet resets it
        repeat(3) {
            assertEquals(SilenceGate.Verdict.SEND, gate.next(0))
        }
        assertFalse(gate.quiet)
    }

    /* A partial packet is still audio: the ring tops it up with silence. */
    @Test
    fun `even one frame counts as playing`() {
        val gate = SilenceGate()
        assertEquals(SilenceGate.Verdict.RESUME, gate.next(1))
    }

    @Test
    fun `reset returns it to the published-but-silent state`() {
        val gate = SilenceGate()
        gate.next(frame)
        assertFalse(gate.quiet)
        gate.reset()
        assertTrue(gate.quiet)
        assertEquals(SilenceGate.Verdict.HOLD, gate.next(0))
    }

    @Test
    fun `the default hold is 200 milliseconds of packets`() {
        assertEquals(40, SilenceGate.DEFAULT_HOLD_PACKETS)
    }
}

/**
 * The media clock keeps running while the sender is quiet.
 *
 * This is the half of silence suppression that is easy to get wrong and
 * impossible to hear until two speakers disagree. A consumer places itself
 * on the *sender's* timeline, so the timestamp has to account for the
 * pause; a producer that resumed with the timestamp it left off with would
 * be claiming the silence never happened, and the new audio would be
 * seated exactly as far in the past as the pause was long.
 */
class SkippedTimeTest {

    @Test
    fun `skipping advances the timestamp by the frames it covers`() {
        val stream = RtpStream(ssrc = 1, startSequence = 0, startTimestamp = 0)
        stream.skip(Rtp.FRAMES_PER_PACKET)
        assertEquals(Rtp.FRAMES_PER_PACKET, stream.timestamp)
    }

    /*
     * And not the sequence number. Sequence counts packets on the wire and
     * a receiver reads a gap in it as loss, so burning numbers on packets
     * deliberately not sent would report the producer's own silence as a
     * broken network.
     */
    @Test
    fun `skipping does not consume sequence numbers`() {
        val stream = RtpStream(ssrc = 1, startSequence = 700, startTimestamp = 0)
        repeat(500) { stream.skip(Rtp.FRAMES_PER_PACKET) }
        assertEquals(700, stream.sequence)
    }

    @Test
    fun `a quiet stretch then audio lands where the clock says it should`() {
        val stream = RtpStream(ssrc = 1, startSequence = 0, startTimestamp = 1_000)
        val payload = ByteArray(Rtp.PAYLOAD_BYTES)
        val packet = ByteArray(Rtp.PACKET_BYTES)

        // One second held, then a packet of audio.
        repeat(200) { stream.skip(Rtp.FRAMES_PER_PACKET) }
        stream.markDiscontinuity()
        stream.write(packet, payload, Rtp.FRAMES_PER_PACKET)

        val sent = ((packet[4].toInt() and 0xFF) shl 24) or
            ((packet[5].toInt() and 0xFF) shl 16) or
            ((packet[6].toInt() and 0xFF) shl 8) or
            (packet[7].toInt() and 0xFF)
        assertEquals(1_000 + Rtp.SAMPLE_RATE, sent, "a second of quiet is a second of timestamp")
        assertTrue(packet[1].toInt() and 0x80 != 0, "and it is marked as a new talkspurt")
    }

    @Test
    fun `the timestamp wraps rather than overflowing`() {
        val stream = RtpStream(ssrc = 1, startSequence = 0, startTimestamp = Int.MAX_VALUE)
        stream.skip(2)
        assertEquals(Int.MIN_VALUE + 1, stream.timestamp)
    }
}
