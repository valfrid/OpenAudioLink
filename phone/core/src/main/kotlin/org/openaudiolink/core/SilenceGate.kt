package org.openaudiolink.core

/**
 * Whether a packet with nothing in it is worth putting on the wire.
 *
 * A cast point is not a stream. librespot publishes itself and then waits,
 * writing nothing to its pipe until somebody picks it in Spotify and
 * presses play — but the pacer above it does not know that, and the ring
 * pads an empty read with silence so the pacer always has something to
 * send. The result was 200 packets a second of digital silence from the
 * instant *Publish* was pressed, which is wrong twice over: it is airtime
 * and battery spent on nothing, and it destroys the one signal a person
 * has that anything is working, because the packet counter starts running
 * whether or not Spotify ever connects.
 *
 * So: publishing is quiet, playing sends. The gate decides which.
 *
 * **It is not simply "empty means quiet".** `PcmRing` argues, correctly,
 * that a decoder stumbling for 20 ms should cost 20 ms of silence rather
 * than a gap — a gap makes every consumer re-seat itself, and four
 * speakers re-seat at four slightly different moments. That argument holds
 * for a stumble in the middle of a track and does not hold for a source
 * that is simply not playing: there is no timeline left to keep continuous.
 * The hold window below is the line between those two cases.
 */
class SilenceGate(
    /**
     * How long an empty ring is treated as a stumble rather than a stop.
     *
     * 200 ms: comfortably longer than a decoder hiccup or a scheduler
     * pause, comfortably shorter than the gap between somebody pressing
     * pause and wondering why the speakers are still busy.
     */
    private val holdPackets: Int = DEFAULT_HOLD_PACKETS,
) {

    enum class Verdict {
        /** Send this packet — there is audio, or the stumble is brief. */
        SEND,

        /**
         * Send it, and mark it: audio has come back after a real stop.
         *
         * The marker bit is what tells a consumer to re-seat itself. It
         * belongs here and nowhere else — RTP's own rule is that a marked
         * packet is the first of a talkspurt, not an ordinary one.
         */
        RESUME,

        /** Send nothing. Nothing is playing. */
        HOLD,
    }

    private var empties = 0

    /**
     * Whether the gate is currently holding.
     *
     * Starts true, and that is the point: a stream that has just been
     * started has an empty ring and has never played anything, so the
     * first packet is held rather than sent. Pressing *Publish* puts a
     * cast point on the network and nothing on the wire.
     */
    var quiet = true
        private set

    /**
     * Decides for one packet, given what the ring holds **before** it is
     * read.
     *
     * Asked before the read rather than after, so a held packet never
     * touches the ring: reading an empty ring counts an underrun, and a
     * source that is merely idle has not underrun anything. Counting it
     * would turn "nobody is playing" into a fault report.
     */
    fun next(availableFrames: Int): Verdict {
        if (availableFrames > 0) {
            empties = 0
            if (quiet) {
                quiet = false
                return Verdict.RESUME
            }
            return Verdict.SEND
        }

        empties++
        if (!quiet && empties > holdPackets) quiet = true
        return if (quiet) Verdict.HOLD else Verdict.SEND
    }

    /** Back to the starting state, for a new source. */
    fun reset() {
        empties = 0
        quiet = true
    }

    companion object {
        /** 200 ms at the wire's packet rate. */
        const val DEFAULT_HOLD_PACKETS: Int =
            Rtp.SAMPLE_RATE / Rtp.FRAMES_PER_PACKET / 5
    }
}
