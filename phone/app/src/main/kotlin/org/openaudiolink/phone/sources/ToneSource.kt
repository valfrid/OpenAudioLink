package org.openaudiolink.phone.sources

import org.openaudiolink.core.PcmRing
import org.openaudiolink.core.Rtp
import kotlin.math.PI
import kotlin.math.sin

/**
 * A sine tone, and the reason it ships in the first version.
 *
 * It needs no permission, no file, no decoder and no account, so it
 * separates two questions that otherwise arrive together: "does the
 * network path work" and "does the decoder work". When a speaker is silent
 * this is what says which half to look at — the firmware carries a tone
 * source for exactly the same reason.
 *
 * Deliberately quiet. Full scale into an unknown amplifier at a party is
 * how equipment gets damaged, and −20 dB is plainly audible.
 */
class ToneSource(
    private val hz: Double = 1000.0,
    private val amplitude: Float = 0.1f,
) : AudioSource {

    override val label: String get() = "Test tone, ${hz.toInt()} Hz"

    @Volatile private var running = false
    private var thread: Thread? = null

    override val isPlaying: Boolean get() = running

    override fun start(ring: PcmRing) {
        if (running) return
        running = true
        thread = Thread({ generate(ring) }, "oal-tone").apply { start() }
    }

    override fun stop() {
        running = false
        thread?.join(500)
        thread = null
    }

    private fun generate(ring: PcmRing) {
        val chunk = FloatArray(Rtp.FRAMES_PER_PACKET * Rtp.CHANNELS)
        var phase = 0.0
        val step = 2.0 * PI * hz / Rtp.SAMPLE_RATE

        while (running) {
            for (frame in 0 until Rtp.FRAMES_PER_PACKET) {
                val sample = (sin(phase) * amplitude).toFloat()
                chunk[frame * 2] = sample
                chunk[frame * 2 + 1] = sample
                phase += step
                // Wrapped rather than left to grow: a double accumulating
                // 48 000 additions a second loses precision by the evening,
                // and the symptom is a tone that slowly goes out of tune.
                if (phase > 2.0 * PI) phase -= 2.0 * PI
            }

            var written = 0
            while (running && written < chunk.size) {
                val frames = ring.write(chunk, chunk.size - written, written)
                written += frames * Rtp.CHANNELS
                if (frames == 0) Thread.sleep(2)   // full; the pacer will drain it
            }
        }
    }
}
