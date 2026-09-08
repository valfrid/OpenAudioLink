package org.openaudiolink.core

import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.sin
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * 44.1 to 48 kHz, which every Spotify track needs and no other source does.
 *
 * The filter is a port of the Hub's, so the useful tests are the ones that
 * would catch a porting mistake: the wrong ratio, a lost channel, a phase
 * that does not carry between calls, or arithmetic that quietly diverges
 * from a sine. A resampler that is subtly wrong sounds fine and measures
 * badly, which is the worst way round.
 */
class ResamplerTest {

    private fun resampler(channels: Int = 2) = RationalResampler(44_100, 48_000, channels)

    @Test
    fun `the ratio reduces to one hundred and forty seven over one hundred and sixty`() {
        val r = resampler()
        // 160 out for every 147 in. Over a second: 44100 in, 48000 out.
        assertTrue(!r.isPassthrough)
        assertEquals(2, r.maxOutputFrames(1), "the very first frame can already produce two")
    }

    @Test
    fun `matching rates are a passthrough`() {
        assertTrue(RationalResampler(48_000, 48_000, 2).isPassthrough)
    }

    /*
     * A second in, a second out. The per-call count varies because the
     * ratio is not an integer, so this is the property that actually has to
     * hold — and drift here is a stream that runs slow all evening.
     */
    @Test
    fun `a second of input produces a second of output`() {
        val r = resampler()
        val chunk = 441 * 2                       // 441 frames, stereo
        val input = FloatArray(chunk)
        val output = FloatArray(r.maxOutputFrames(441) * 2)

        var frames = 0
        repeat(100) {                             // 44 100 frames, one second
            frames += r.process(input, chunk, output) / 2
        }
        // Within one frame of 48 000: the filter's own phase decides which
        // side of the boundary the last output lands on.
        assertTrue(abs(frames - 48_000) <= 1, "got $frames frames for one second")
    }

    /*
     * The test that would catch a real porting error: a sine in must be a
     * sine out, at the same frequency and roughly the same amplitude.
     */
    @Test
    fun `a sine survives the conversion`() {
        val r = resampler(channels = 1)
        val hz = 1000.0
        val inFrames = 44_100
        val input = FloatArray(inFrames) { sin(2.0 * PI * hz * it / 44_100.0).toFloat() * 0.5f }
        val output = FloatArray(r.maxOutputFrames(inFrames))
        val written = r.process(input, inFrames, output)

        /*
         * Skip the filter's group delay, where the output is still ramping
         * up through a history of zeros — measuring there would fail a
         * correct filter.
         */
        val settled = 200
        var peak = 0.0f
        var energy = 0.0
        var reference = 0.0
        for (i in settled until written) {
            val expected = sin(2.0 * PI * hz * (i - RationalResampler.TAPS_PER_PHASE / 2.0 *
                48_000.0 / 44_100.0) / 48_000.0) * 0.5
            peak = maxOf(peak, abs(output[i]))
            energy += (output[i] - expected) * (output[i] - expected)
            reference += expected * expected
        }

        assertTrue(peak in 0.45f..0.55f, "amplitude should survive; peak was $peak")
        // Error energy well under the signal: a wrong ratio or a broken
        // phase shows up here as noise comparable to the tone itself.
        val ratio = energy / reference
        assertTrue(ratio < 0.01, "error energy was ${ratio * 100}% of the signal")
    }

    /* Silence in, silence out — no DC offset, no ringing from nowhere. */
    @Test
    fun `silence stays silent`() {
        val r = resampler()
        val input = FloatArray(2048)
        val output = FloatArray(r.maxOutputFrames(1024) * 2)
        val written = r.process(input, input.size, output)
        for (i in 0 until written) assertEquals(0f, output[i], 1e-9f)
    }

    /*
     * The channels must not swap or bleed. A stereo mistake here is one
     * that survives every mono test and then puts the left speaker's audio
     * in the right speaker for the rest of the evening.
     */
    @Test
    fun `the channels stay apart`() {
        val r = resampler()
        val frames = 4096
        val input = FloatArray(frames * 2)
        for (i in 0 until frames) {
            input[i * 2] = sin(2.0 * PI * 1000.0 * i / 44_100.0).toFloat()
            input[i * 2 + 1] = 0f                 // right silent throughout
        }
        val output = FloatArray(r.maxOutputFrames(frames) * 2)
        val written = r.process(input, input.size, output)

        var left = 0.0f
        var right = 0.0f
        var i = 0
        while (i < written) {
            left = maxOf(left, abs(output[i]))
            right = maxOf(right, abs(output[i + 1]))
            i += 2
        }
        assertTrue(left > 0.9f, "the left channel should come through: $left")
        assertTrue(right < 1e-6f, "the right channel was silent and must stay so: $right")
    }

    /*
     * Phase carries across calls. librespot arrives in whatever chunks the
     * pipe hands over, so a resampler that restarted its phase every call
     * would click on every buffer boundary — hundreds of times a second,
     * and never in a test that resamples one big array.
     */
    @Test
    fun `many small calls match one big one`() {
        val frames = 4410
        val input = FloatArray(frames * 2) {
            sin(2.0 * PI * 440.0 * (it / 2) / 44_100.0).toFloat() * 0.5f
        }

        val whole = RationalResampler(44_100, 48_000, 2)
        val wholeOut = FloatArray(whole.maxOutputFrames(frames) * 2)
        val wholeWritten = whole.process(input, input.size, wholeOut)

        val piecemeal = RationalResampler(44_100, 48_000, 2)
        val pieceOut = FloatArray(wholeOut.size)
        var written = 0
        val step = 147 * 2                        // an awkward chunk on purpose
        var at = 0
        val scratch = FloatArray(step)
        val scratchOut = FloatArray(piecemeal.maxOutputFrames(step / 2) * 2)
        while (at + step <= input.size) {
            input.copyInto(scratch, 0, at, at + step)
            val n = piecemeal.process(scratch, step, scratchOut)
            scratchOut.copyInto(pieceOut, written, 0, n)
            written += n
            at += step
        }

        assertEquals(wholeWritten, written, "the same input must give the same count")
        for (i in 0 until written) {
            assertEquals(wholeOut[i], pieceOut[i], 1e-6f, "sample $i differs")
        }
    }

    /* A restart must not bleed the last track's tail into the next one. */
    @Test
    fun `reset forgets the history`() {
        val r = resampler(channels = 1)
        val loud = FloatArray(256) { 1f }
        val out = FloatArray(r.maxOutputFrames(256))
        r.process(loud, loud.size, out)

        r.reset()

        val silence = FloatArray(256)
        val after = FloatArray(r.maxOutputFrames(256))
        val written = r.process(silence, silence.size, after)
        for (i in 0 until written) {
            assertEquals(0f, after[i], 1e-9f, "sample $i carried over from before the reset")
        }
    }
}

/**
 * librespot's pipe, byte for byte.
 *
 * It writes raw PCM to stdout in the format `--format` names, and getting
 * the width or the byte order wrong produces something that is still
 * audibly music — quieter, noisier, or an octave out — rather than an
 * error. That is why this is checked here rather than by listening.
 */
class LibrespotPcmTest {

    @Test
    fun `signed sixteen-bit little-endian decodes to plus and minus one`() {
        val bytes = byteArrayOf(
            0x00, 0x00,                     // 0
            0xFF.toByte(), 0x7F,            // +32767, full scale
            0x00, 0x80.toByte(),            // -32768
            0x00, 0x40,                     // +16384, half
        )
        val out = FloatArray(4)
        assertEquals(4, LibrespotPcm.decodeS16(bytes, bytes.size, out))

        assertEquals(0f, out[0])
        assertEquals(1f, out[1], 1e-4f)
        assertEquals(-1f, out[2], 1e-4f)
        assertEquals(0.5f, out[3], 1e-4f)
    }

    /* A partial frame decoded now puts the channels out of step for the
     * rest of the stream — a swap that never recovers, not one bad sample. */
    @Test
    fun `a trailing odd byte is not decoded`() {
        val bytes = byteArrayOf(0x00, 0x40, 0x11)
        val out = FloatArray(4)
        assertEquals(1, LibrespotPcm.decodeS16(bytes, bytes.size, out))
    }

    @Test
    fun `whole stereo frames survive interleaved`() {
        // left = +full, right = -full
        val bytes = byteArrayOf(0xFF.toByte(), 0x7F, 0x00, 0x80.toByte())
        val out = FloatArray(2)
        assertEquals(2, LibrespotPcm.decodeS16(bytes, bytes.size, out))
        assertTrue(out[0] > 0.99f)
        assertTrue(out[1] < -0.99f)
    }
}
