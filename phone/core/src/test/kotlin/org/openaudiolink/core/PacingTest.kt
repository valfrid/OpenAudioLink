package org.openaudiolink.core

import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertTrue

/**
 * When packets leave, which on a phone is the interesting half.
 *
 * A phone is not a machine that gets to run a 5 ms loop undisturbed. It
 * gets frozen, throttled and descheduled, and the difference between an
 * app that survives that and one that does not is entirely in what it does
 * when it wakes up owing packets.
 */
class SendClockTest {

    private val ms = 1_000_000L
    private val packet = 5 * ms

    @Test
    fun `nothing is due before the first packet time`() {
        val clock = SendClock()
        clock.start(0)
        assertIs<SendClock.Tick.Wait>(clock.tick(4 * ms))
    }

    @Test
    fun `one packet every five milliseconds`() {
        val clock = SendClock()
        clock.start(0)
        for (i in 1..10) {
            val tick = clock.tick(i * packet)
            assertIs<SendClock.Tick.Send>(tick)
            assertEquals(1, tick.packets, "packet $i")
            clock.sent(tick.packets)
        }
        assertEquals(10, clock.packetsSent)
    }

    /*
     * The anchor is fixed, so lateness is corrected rather than
     * accumulated. Sleeping "5 ms" two hundred times a second drifts by
     * seconds an hour; counting from the start does not drift at all, and
     * a receiver measuring against the sender's timeline would see that
     * drift as the sender walking away from it.
     */
    @Test
    fun `a late wake-up catches up instead of accumulating`() {
        val clock = SendClock()
        clock.start(0)

        val first = clock.tick(packet)
        clock.sent((first as SendClock.Tick.Send).packets)

        // Descheduled: we wake at 17 ms owing packets 2, 3 and 4 minus one sent.
        val late = clock.tick(17 * ms)
        assertIs<SendClock.Tick.Send>(late)
        assertEquals(2, late.packets)
        clock.sent(late.packets)

        // And the schedule is unchanged, not shifted by the lateness.
        assertIs<SendClock.Tick.Wait>(clock.tick(18 * ms))
        val next = clock.tick(20 * ms)
        assertIs<SendClock.Tick.Send>(next)
        assertEquals(1, next.packets)
    }

    /*
     * Doze. A process frozen for a minute owes twelve thousand packets,
     * and sending them would be five seconds of airtime delivering audio
     * nobody can still use — the receivers would drop it as too late
     * anyway. Admit the break and start from now.
     */
    @Test
    fun `a long freeze resynchronises instead of flooding`() {
        val clock = SendClock()
        clock.start(0)
        val tick = clock.tick(60_000 * ms)
        assertIs<SendClock.Tick.Resync>(tick)

        // Re-anchored at the moment it woke: nothing owed yet.
        assertIs<SendClock.Tick.Wait>(clock.tick(60_000 * ms + ms))
        val after = clock.tick(60_005 * ms)
        assertIs<SendClock.Tick.Send>(after)
        assertEquals(1, after.packets)
    }

    /*
     * The limit is a quarter of a second: comfortably more than a
     * scheduler hiccup, comfortably less than a consumer's jitter buffer,
     * so a burst up to it is absorbed rather than heard.
     */
    @Test
    fun `the catch-up limit is a quarter of a second`() {
        val clock = SendClock()
        assertEquals(50, clock.catchUpLimit)

        clock.start(0)
        val atLimit = clock.tick(250 * ms)
        assertIs<SendClock.Tick.Send>(atLimit)
        assertEquals(50, atLimit.packets)

        val past = SendClock()
        past.start(0)
        assertIs<SendClock.Tick.Resync>(past.tick(256 * ms))
    }

    @Test
    fun `a clock that goes backwards resynchronises rather than stalling`() {
        val clock = SendClock()
        clock.start(1_000 * ms)
        assertIs<SendClock.Tick.Resync>(clock.tick(500 * ms))
    }

    @Test
    fun `nothing is due before the clock is started`() {
        assertIs<SendClock.Tick.Wait>(SendClock().tick(1_000 * ms))
    }

    @Test
    fun `the wait is the time to the next packet, never negative`() {
        val clock = SendClock()
        clock.start(0)
        assertEquals(packet, clock.nanosUntilNext(0))
        assertEquals(0, clock.nanosUntilNext(10 * ms))
    }
}

/**
 * The buffer between a decoder that runs in bursts and a wire that does
 * not.
 */
class PcmRingTest {

    private fun frames(n: Int, value: Float = 0.25f) =
        FloatArray(n * Rtp.CHANNELS) { value }

    @Test
    fun `what goes in comes out in order`() {
        val ring = PcmRing(1000)
        val input = FloatArray(20) { it.toFloat() / 100f }
        assertEquals(10, ring.write(input, input.size))

        val out = FloatArray(20)
        assertEquals(10, ring.readOrSilence(out, 10))
        for (i in 0 until 20) assertEquals(input[i], out[i], 1e-6f)
    }

    /*
     * Silence, not nothing. A gap is a break in the sender's timeline and
     * a consumer answers a break by re-seating itself — every speaker at a
     * slightly different moment. Timed silence keeps the timeline whole,
     * so a decoder stumbling for 20 ms costs 20 ms of quiet rather than a
     * second of re-priming on four speakers.
     */
    @Test
    fun `an empty ring yields silence rather than failing`() {
        val ring = PcmRing(1000)
        val out = FloatArray(Rtp.FRAMES_PER_PACKET * Rtp.CHANNELS) { 9f }

        assertEquals(0, ring.readOrSilence(out, Rtp.FRAMES_PER_PACKET))
        assertTrue(out.all { it == 0f })
        assertEquals(Rtp.FRAMES_PER_PACKET.toLong(), ring.underruns)
    }

    @Test
    fun `a partial read is topped up with silence and counted`() {
        val ring = PcmRing(1000)
        ring.write(frames(100), 100 * Rtp.CHANNELS)

        val out = FloatArray(Rtp.FRAMES_PER_PACKET * Rtp.CHANNELS)
        assertEquals(100, ring.readOrSilence(out, Rtp.FRAMES_PER_PACKET))
        assertEquals(0.25f, out[0])
        assertEquals(0f, out[100 * Rtp.CHANNELS], "silence begins exactly where audio ran out")
        assertEquals(140L, ring.underruns)
    }

    @Test
    fun `a full ring reports what it could not take`() {
        val ring = PcmRing(10)
        val input = frames(25)
        assertEquals(10, ring.write(input, input.size))
        assertEquals(15L, ring.overruns)
    }

    /* Half a frame would swap the channels for the rest of the stream. */
    @Test
    fun `only whole frames are accepted`() {
        val ring = PcmRing(4)
        assertEquals(2, ring.write(FloatArray(5), 5), "two frames and a stray sample")
    }

    /*
     * A partial take is the normal case, not an error: a source offering
     * more than the ring can hold has to come back with the rest. The
     * obvious loop without an offset re-offers the whole buffer and
     * duplicates everything already accepted, which is a stutter rather
     * than a silence and therefore much harder to spot.
     */
    @Test
    fun `an offset lets a source resume where it was cut off`() {
        val ring = PcmRing(2)
        val input = FloatArray(8) { it.toFloat() }

        assertEquals(2, ring.write(input, input.size), "only two frames fit")

        val out = FloatArray(4)
        ring.readOrSilence(out, 2)
        assertContentEquals(floatArrayOf(0f, 1f, 2f, 3f), out)

        assertEquals(2, ring.write(input, 4, offset = 4), "the rest, not the start again")
        ring.readOrSilence(out, 2)
        assertContentEquals(floatArrayOf(4f, 5f, 6f, 7f), out)
    }

    @Test
    fun `it wraps without losing anything`() {
        val ring = PcmRing(8)
        val out = FloatArray(16)
        repeat(20) { round ->
            val input = FloatArray(8) { round.toFloat() }
            assertEquals(4, ring.write(input, input.size))
            assertEquals(4, ring.readOrSilence(out, 4))
            assertTrue(out.take(8).all { it == round.toFloat() }, "round $round")
        }
        assertEquals(0L, ring.underruns)
        assertEquals(0L, ring.overruns)
    }

    @Test
    fun `clearing forgets everything held`() {
        val ring = PcmRing(100)
        ring.write(frames(50), 100)
        ring.clear()
        assertEquals(0, ring.availableFrames)
    }
}
