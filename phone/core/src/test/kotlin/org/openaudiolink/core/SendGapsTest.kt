package org.openaudiolink.core

import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals

/**
 * Measuring this end, so the two ends can be compared.
 *
 * A log from two speakers showed both reporting arrival gaps over 200 ms
 * at the same rate — 1 262 and 1 264 over the same quarter of an hour.
 * Two receivers on different radios do not agree that closely by accident,
 * so the cause was upstream of both; and nothing either of them recorded
 * could say whether upstream meant the producer or the air between.
 */
class SendGapsTest {

    /** A clock the test drives, in nanoseconds. */
    private class Hand {
        var now = 0L
        fun read(): Long = now
        fun advanceMs(ms: Long) { now += ms * 1_000_000L }
    }

    @Test
    fun `even sending records no gaps`() {
        val hand = Hand()
        val gaps = SendGaps(hand::read)
        repeat(1000) {
            gaps.sent()
            hand.advanceMs(5)
        }
        assertEquals(0, gaps.gaps)
        assertEquals(0, gaps.worstMs)
    }

    /*
     * Three packet intervals, matching the firmware's threshold exactly so
     * the two columns can be read against each other rather than
     * translated. Any thread is a millisecond late routinely; a counter
     * that fired on that would read high forever.
     */
    @Test
    fun `being a little late is not a gap`() {
        val hand = Hand()
        val gaps = SendGaps(hand::read)
        gaps.sent()
        hand.advanceMs(14)
        gaps.sent()
        assertEquals(0, gaps.gaps, "14 ms is under three packet intervals")

        hand.advanceMs(16)
        gaps.sent()
        assertEquals(1, gaps.gaps, "16 ms is over")
    }

    @Test
    fun `the first send starts the clock rather than counting`() {
        val hand = Hand()
        val gaps = SendGaps(hand::read)
        hand.advanceMs(10_000)
        gaps.sent()
        assertEquals(0, gaps.gaps, "nothing preceded it")
    }

    @Test
    fun `the worst is kept, and the shape beside it`() {
        val hand = Hand()
        val gaps = SendGaps(hand::read)
        gaps.sent()
        for (ms in listOf(18L, 40L, 80L, 150L, 400L)) {
            hand.advanceMs(ms)
            gaps.sent()
        }
        assertEquals(5, gaps.gaps)
        assertEquals(400, gaps.worstMs)
        assertContentEquals(longArrayOf(1, 1, 1, 1, 1), gaps.buckets)
    }

    /*
     * A deliberate silence must not be counted here, or the phone
     * reproduces at its own end exactly the misreading that made a paused
     * track look like a sixteen-second stall at the other.
     */
    @Test
    fun `a resumed silence is not a late send`() {
        val hand = Hand()
        val gaps = SendGaps(hand::read)
        gaps.sent()
        hand.advanceMs(16_000)
        gaps.resume()
        gaps.sent()
        assertEquals(0, gaps.gaps)
        assertEquals(0, gaps.worstMs)

        // And the interval after it is measured normally again.
        hand.advanceMs(300)
        gaps.sent()
        assertEquals(1, gaps.gaps)
    }

    @Test
    fun `buckets match the firmware's edges`() {
        val hand = Hand()
        val gaps = SendGaps(hand::read)
        gaps.sent()
        for (ms in listOf(20L, 21L, 50L, 51L, 100L, 101L, 200L, 201L)) {
            hand.advanceMs(ms)
            gaps.sent()
        }
        // 20 -> b0, 21 -> b1, 50 -> b1, 51 -> b2, 100 -> b2, 101 -> b3,
        // 200 -> b3, 201 -> b4
        assertContentEquals(longArrayOf(1, 2, 2, 2, 1), gaps.buckets)
    }
}
