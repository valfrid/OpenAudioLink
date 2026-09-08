package org.openaudiolink.core

/**
 * How evenly this phone actually put packets on the wire.
 *
 * The consumers already measure this from the other end — `arrivalGaps`,
 * in the firmware — and a log from two speakers raised a question that end
 * cannot answer. Both nodes reported **the same number** of arrival gaps
 * over 200 ms, 1 262 and 1 264 over the same fifteen minutes, about 1.4
 * every second. Two receivers on different radios at different signal
 * strengths do not agree to within a fifth of a percent by coincidence:
 * whatever caused those gaps happened once, upstream of both.
 *
 * Upstream of both is this phone, or the air between it and them, and
 * nothing at either end could tell those apart. The node knows when a
 * packet arrived; only the producer knows when it was sent. So the
 * producer measures it too, in the same buckets, and the two columns
 * side by side answer the question in one reading:
 *
 *  - **gaps here, gaps there** — the phone stalled. A scheduler that
 *    descheduled the sending thread, a socket that blocked, a garbage
 *    collection.
 *  - **no gaps here, gaps there** — the phone paced correctly and the
 *    network clumped the packets on the way. Nothing in this app will fix
 *    that; the consumer's cushion is what absorbs it.
 *
 * Thresholds identical to the firmware's on purpose, so the two are read
 * against each other rather than translated.
 */
class SendGaps(private val clock: () -> Long = System::nanoTime) {

    /**
     * A gap worth counting: three packet intervals, as the firmware uses.
     *
     * Not one interval. Any thread on any operating system is late by a
     * millisecond routinely, and a counter that fired on that would read
     * high forever and mean nothing.
     */
    private val thresholdNanos = 3L * 1_000_000_000L / (Rtp.SAMPLE_RATE / Rtp.FRAMES_PER_PACKET)

    private var lastNanos = 0L
    private var started = false

    /** Gaps longer than three packet intervals between one send and the next. */
    @Volatile var gaps: Long = 0; private set

    /** The worst, in milliseconds. A lifetime maximum, like the node's. */
    @Volatile var worstMs: Long = 0; private set

    /**
     * The same five buckets the firmware keeps, and for the same reason: a
     * lifetime maximum stops being news, and the shape of the distribution
     * is what says whether a cushion can absorb it.
     */
    val buckets = LongArray(BUCKETS)

    /** Records that a packet has just gone out. */
    fun sent() {
        val now = clock()
        if (!started) {
            started = true
            lastNanos = now
            return
        }
        val gap = now - lastNanos
        lastNanos = now
        if (gap <= thresholdNanos) return

        gaps++
        val ms = gap / 1_000_000L
        if (ms > worstMs) worstMs = ms
        buckets[bucketFor(ms)]++
    }

    /**
     * Starts a fresh interval without counting one.
     *
     * For a deliberate silence: the gate closing and opening again is not
     * a stall, and counting it here would reproduce, on the phone, exactly
     * the misreading this class exists to resolve at the other end.
     */
    fun resume() {
        started = false
    }

    private fun bucketFor(ms: Long): Int = when {
        ms <= 20 -> 0
        ms <= 50 -> 1
        ms <= 100 -> 2
        ms <= 200 -> 3
        else -> 4
    }

    companion object {
        const val BUCKETS = 5
    }
}
