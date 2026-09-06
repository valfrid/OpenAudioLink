package org.openaudiolink.core

/**
 * When the next packet is due.
 *
 * A producer's whole obligation, now that consumers place themselves on
 * the sender's RTP timeline, is to stamp consistently and to keep sending.
 * This is the second half of that: 200 packets a second, measured against
 * a fixed anchor rather than against the last packet, so a late wake-up is
 * corrected instead of accumulated. Sleeping "5 ms" 200 times a second on
 * a phone drifts by seconds an hour; counting from the start does not
 * drift at all.
 *
 * Pure and clock-injected so the awkward cases can be tested rather than
 * waited for — the interesting ones take a phone going to sleep.
 */
class SendClock(
    private val packetsPerSecond: Int = Rtp.SAMPLE_RATE / Rtp.FRAMES_PER_PACKET,
) {
    sealed interface Tick {
        /** Nothing due; sleep until something is. */
        data object Wait : Tick

        /** Send this many packets now, back to back. */
        data class Send(val packets: Int) : Tick

        /**
         * Too far behind to catch up. Re-anchor the clock and mark the
         * next packet as a discontinuity.
         *
         * This is Android's doing, not the network's: a process that has
         * been frozen for a minute owes 12 000 packets, and sending them
         * would be a flood the receivers would drop anyway — five seconds
         * of airtime to deliver audio nobody can still use. Better to
         * admit the break, tell the consumers about it with the marker
         * bit, and start again from now.
         */
        data object Resync : Tick
    }

    /**
     * How far behind is worth catching up on.
     *
     * A packet is 5 ms, so this is a quarter of a second — comfortably
     * more than a scheduler hiccup and comfortably less than a consumer's
     * jitter buffer, which means a burst this size is absorbed rather
     * than heard.
     */
    val catchUpLimit: Int = packetsPerSecond / 4

    private var anchorNanos = 0L
    private var sent = 0L
    private var started = false

    /** Anchors the timeline at @p nowNanos. */
    fun start(nowNanos: Long) {
        anchorNanos = nowNanos
        sent = 0
        started = true
    }

    val packetsSent: Long get() = sent

    fun tick(nowNanos: Long): Tick {
        if (!started) return Tick.Wait

        val elapsed = nowNanos - anchorNanos
        if (elapsed < 0) {
            // A clock that went backwards is not a clock we can pace on.
            start(nowNanos)
            return Tick.Resync
        }

        val due = elapsed * packetsPerSecond / 1_000_000_000L
        val behind = due - sent
        return when {
            behind <= 0 -> Tick.Wait
            behind > catchUpLimit -> {
                start(nowNanos)
                Tick.Resync
            }
            else -> Tick.Send(behind.toInt())
        }
    }

    /** Records packets actually put on the wire. */
    fun sent(packets: Int) {
        sent += packets
    }

    /** Nanoseconds until the next packet is due, never negative. */
    fun nanosUntilNext(nowNanos: Long): Long {
        if (!started) return 0
        val nextAt = anchorNanos + (sent + 1) * 1_000_000_000L / packetsPerSecond
        return maxOf(0L, nextAt - nowNanos)
    }
}
