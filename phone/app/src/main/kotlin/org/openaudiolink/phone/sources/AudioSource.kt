package org.openaudiolink.phone.sources

import org.openaudiolink.core.PcmRing

/**
 * One interface, and every source behind it.
 *
 * This is the containment decision 19 records, expressed as code. A source
 * produces 48 kHz interleaved stereo float PCM into the ring and knows
 * nothing about RTP; the sender knows nothing about decoders. The
 * consequences are worth stating because they are the reason for the shape:
 *
 *  - **A librespot-backed source can be left out of a build** without
 *    taking the app with it. Whether running a reimplementation of
 *    somebody's streaming protocol is licensed for use with that service
 *    is the operator's decision, and an app that is still a radio, a
 *    library player and a microphone without it leaves that decision open.
 *  - **Internet radio is the same interface.** Media3 decodes MP3, AAC and
 *    FLAC already, so a station is a different URL rather than a different
 *    pipeline.
 *  - **Nothing downstream cares what is playing.** The pacing, the
 *    packetisation and the speakers are identical for a record, a stream
 *    and a test tone.
 */
interface AudioSource {

    /** What to show while this is playing. */
    val label: String

    /**
     * Begins producing into @p ring.
     *
     * A source writes as fast as it can and blocks when the ring is full;
     * the ring is what makes a decoder that runs in bursts and a wire that
     * does not into the same system.
     */
    fun start(ring: PcmRing)

    fun stop()

    val isPlaying: Boolean

    /**
     * What the source would say for itself, if asked.
     *
     * Packet counters prove this app is sending; they say nothing about
     * whether the *source* is happy. For Spotify that is the whole
     * question — librespot can be running, publishing nothing, and
     * refusing to authenticate, and from outside that is identical to a
     * cast point Spotify simply has not shown yet.
     */
    val status: String? get() = null
}
