package org.openaudiolink.core

/**
 * The buffer between a decoder and the wire.
 *
 * A decoder does not run in real time — it produces a second of audio in a
 * few milliseconds and then nothing at all while it waits for a read — and
 * the wire wants exactly 240 frames every 5 ms. This holds the difference.
 *
 * **Underrun sends silence rather than nothing.** A gap in the stream is a
 * break in the sender's timeline, and a consumer answers a break by
 * re-seating itself: it stops, waits to refill, and every speaker in the
 * house does it at a slightly different moment. Timed silence keeps the
 * timeline continuous and the receivers primed, so a decoder stumbling for
 * 20 ms costs 20 ms of quiet instead of a second of re-priming on four
 * speakers. The firmware takes the same position for a producer with
 * nothing wired to its input.
 *
 * Frames, never bytes or samples. Two of the three counting mistakes
 * available here are stereo ones.
 */
class PcmRing(capacityFrames: Int) {
    private val capacity = capacityFrames * Rtp.CHANNELS
    private val buffer = FloatArray(capacity)
    private var readAt = 0
    private var writeAt = 0
    private var filled = 0

    /** Frames dropped because the ring was full — the decoder ran ahead. */
    var overruns: Long = 0
        private set

    /** Frames sent as silence because the ring was empty. */
    var underruns: Long = 0
        private set

    val capacityFrames: Int get() = capacity / Rtp.CHANNELS

    @get:Synchronized
    val availableFrames: Int get() = filled / Rtp.CHANNELS

    /**
     * Adds interleaved float samples, starting at @p offset.
     *
     * The offset exists because a partial take is the normal case, not an
     * error: a decoder offering a second of audio to a ring holding half
     * of one has to be able to come back with the rest. Without it the
     * obvious loop re-offers the whole buffer and duplicates everything
     * that was already accepted.
     *
     * @return how many *frames* were taken. Fewer than offered means the
     * ring is full, and the caller should wait rather than spin: dropping
     * audio to keep a decoder happy is the wrong trade in a system where
     * the decoder can simply be asked again in a moment.
     */
    @Synchronized
    fun write(samples: FloatArray, count: Int, offset: Int = 0): Int {
        var taken = 0
        while (taken < count && filled < capacity) {
            buffer[writeAt] = samples[offset + taken]
            writeAt = (writeAt + 1) % capacity
            filled++
            taken++
        }
        if (taken < count) {
            overruns += (count - taken) / Rtp.CHANNELS
        }
        // Only whole frames, or the channels swap for the rest of the stream.
        return taken / Rtp.CHANNELS
    }

    /**
     * Takes exactly @p frames, filling any shortfall with silence.
     *
     * Always succeeds, because the pacer above it has to send something on
     * time — see the note on underruns at the top of this file.
     *
     * @return how many frames were real audio.
     */
    @Synchronized
    fun readOrSilence(out: FloatArray, frames: Int): Int {
        val wanted = frames * Rtp.CHANNELS
        var got = 0
        while (got < wanted && filled > 0) {
            out[got] = buffer[readAt]
            readAt = (readAt + 1) % capacity
            filled--
            got++
        }
        if (got < wanted) {
            java.util.Arrays.fill(out, got, wanted, 0f)
            underruns += (wanted - got) / Rtp.CHANNELS
        }
        return got / Rtp.CHANNELS
    }

    /**
     * Forgets what has been counted, for a new stream.
     *
     * Separate from [clear], which throws away *audio* and is called
     * mid-stream — on a resync, and on every seek or track change the
     * decoder reports. Resetting the counters there would erase the
     * evidence of the very stumble that caused it.
     *
     * This is for the other case: a new source, a new sender, a fresh
     * `packetsSent`. Without it the two are on different clocks, and a
     * screen showing "10451 packets · 17856 underruns" is comparing this
     * stream against everything since the app launched — which reads as
     * more silence than audio and is simply two different measurements
     * side by side.
     */
    @Synchronized
    fun resetCounters() {
        overruns = 0
        underruns = 0
    }

    /** Forgets everything held, for a source change. */
    @Synchronized
    fun clear() {
        readAt = 0
        writeAt = 0
        filled = 0
    }
}
