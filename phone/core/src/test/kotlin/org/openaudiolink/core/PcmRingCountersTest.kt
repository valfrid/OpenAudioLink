package org.openaudiolink.core

import kotlin.test.Test
import kotlin.test.assertEquals

/**
 * Counters that belong to one stream, not to the whole app.
 *
 * A screen reading "10451 packets · 17856 underruns" is what prompted
 * this. The two are not comparable: the packet count restarts with each
 * new sender, while the ring lives for the life of the process, so the
 * underruns were everything since launch. It reads as more silence than
 * audio, and it is simply two different measurements printed side by side.
 */
class PcmRingCountersTest {

    private fun starve(ring: PcmRing, packets: Int) {
        val out = FloatArray(Rtp.FRAMES_PER_PACKET * Rtp.CHANNELS)
        repeat(packets) { ring.readOrSilence(out, Rtp.FRAMES_PER_PACKET) }
    }

    @Test
    fun `resetting the counters leaves the audio alone`() {
        val ring = PcmRing(1000)
        starve(ring, 1)                                  // underruns something
        ring.write(FloatArray(200) { 0.5f }, 200)        // then 100 frames arrive

        ring.resetCounters()

        assertEquals(0, ring.underruns)
        assertEquals(0, ring.overruns)
        assertEquals(100, ring.availableFrames, "the audio held is untouched")
    }

    @Test
    fun `clearing the audio leaves the counters alone`() {
        val ring = PcmRing(1000)
        starve(ring, 2)
        val before = ring.underruns

        ring.clear()

        assertEquals(before, ring.underruns, "a resync must not erase the stumble that caused it")
        assertEquals(0, ring.availableFrames)
    }

    @Test
    fun `a new stream starts both counts at zero`() {
        val ring = PcmRing(1000)
        starve(ring, 5)
        val tiny = FloatArray(4000) { 0.1f }
        ring.write(tiny, tiny.size)          // overruns: the ring holds 1000 frames
        assertEquals(true, ring.underruns > 0 && ring.overruns > 0)

        // What Producer.startStream does.
        ring.clear()
        ring.resetCounters()

        assertEquals(0, ring.underruns)
        assertEquals(0, ring.overruns)
    }
}
