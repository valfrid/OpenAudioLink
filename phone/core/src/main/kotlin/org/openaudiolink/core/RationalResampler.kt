package org.openaudiolink.core

import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * Streaming sample-rate conversion between two fixed rates.
 *
 * Spotify is 44.1 kHz and the wire is 48 kHz (decision 13), so something
 * has to resample. On the Hub that is `RationalResampler.cs`; this is the
 * same filter, ported, because a phone producing a different 48 kHz from
 * the Hub's would be a difference nobody could hear but everybody would
 * have to reason about.
 *
 * The ratio is exact — 44100:48000 reduces to 147:160 — so this is a
 * polyphase FIR rather than an interpolator chasing a drifting phase.
 * Conceptually the input is upsampled by `L`, low-pass filtered and
 * decimated by `M`; in practice only the taps landing on a nonzero sample
 * are multiplied, which is [TAPS_PER_PHASE] of them per output sample.
 *
 * Not thread-safe: it carries the filter history, so one instance belongs
 * to one stream.
 */
class RationalResampler(
    val inputRate: Int,
    val outputRate: Int,
    val channels: Int,
) {
    companion object {
        /**
         * Taps per phase. Sets the transition band: 64 puts the passband
         * edge above 20 kHz for 44.1 kHz input, where 32 would pull it down
         * to about 18 kHz. The cost is 64 multiplies per output sample,
         * which at 48 kHz stereo is a few million a second — nothing, even
         * on a phone.
         */
        const val TAPS_PER_PHASE = 64

        /** Kaiser window parameter, chosen for roughly -90 dB stopband. */
        private const val WINDOW_BETA = 8.6

        private fun gcd(a: Int, b: Int): Int {
            var x = a
            var y = b
            while (y != 0) {
                val t = x % y
                x = y
                y = t
            }
            return x
        }

        /** Modified Bessel function of the first kind, order zero. */
        private fun besselI0(x: Double): Double {
            var sum = 1.0
            var term = 1.0
            for (k in 1 until 64) {
                val factor = x / (2.0 * k)
                term *= factor * factor
                sum += term
                if (term < 1e-14 * sum) break
            }
            return sum
        }

        /**
         * Windowed-sinc low-pass, cut at the lower of the two Nyquist
         * limits and scaled by the interpolation factor to make up for the
         * zeros inserted between input samples.
         */
        private fun buildTaps(interpolation: Int, decimation: Int): FloatArray {
            val length = TAPS_PER_PHASE * interpolation
            val cutoff = 0.5 / max(interpolation, decimation)
            val centre = length / 2.0
            val normaliser = besselI0(WINDOW_BETA)

            val prototype = DoubleArray(length)
            for (i in 0 until length) {
                val x = i - centre
                val sinc = if (abs(x) < 1e-9) {
                    2.0 * cutoff
                } else {
                    sin(2.0 * PI * cutoff * x) / (PI * x)
                }
                val position = x / centre
                val window = besselI0(
                    WINDOW_BETA * sqrt(max(0.0, 1.0 - position * position))
                ) / normaliser
                prototype[i] = sinc * window * interpolation
            }

            // Stored phase-major so one output sample walks a contiguous run.
            val taps = FloatArray(length)
            for (phase in 0 until interpolation) {
                for (k in 0 until TAPS_PER_PHASE) {
                    taps[phase * TAPS_PER_PHASE + k] =
                        prototype[phase + k * interpolation].toFloat()
                }
            }
            return taps
        }
    }

    private val interpolation: Int
    private val decimation: Int
    private val taps: FloatArray
    private val history: FloatArray
    private var newest = 0

    /**
     * Position of the next output within the current input frame,
     * numerator-only: an output is due while this is below [interpolation].
     */
    private var phase = 0

    init {
        require(inputRate > 0 && outputRate > 0) { "sample rates must be positive" }
        require(channels > 0) { "channel count must be positive" }

        val divisor = gcd(inputRate, outputRate)
        interpolation = outputRate / divisor
        decimation = inputRate / divisor
        taps = buildTaps(interpolation, decimation)
        history = FloatArray(TAPS_PER_PHASE * channels)
    }

    /** True when the rates match, so a caller can skip the whole thing. */
    val isPassthrough: Boolean get() = interpolation == 1 && decimation == 1

    /**
     * Largest number of output frames [inputFrames] can produce.
     *
     * The count varies from call to call because the ratio is not an
     * integer, so a caller sizing a buffer must use this rather than the
     * average — the very first frame of a 44.1 to 48 kHz conversion already
     * produces two.
     */
    fun maxOutputFrames(inputFrames: Int): Int =
        if (inputFrames <= 0) 0
        else (((inputFrames.toLong() + 1) * interpolation - 1) / decimation).toInt()

    /**
     * Resamples interleaved frames.
     *
     * @return samples written, which is frames times channels.
     */
    fun process(input: FloatArray, inputLength: Int, output: FloatArray): Int {
        val frames = inputLength / channels
        if (frames == 0) return 0
        require(output.size >= maxOutputFrames(frames) * channels) {
            "output needs room for ${maxOutputFrames(frames)} frames"
        }

        var written = 0
        for (frame in 0 until frames) {
            newest = if (newest + 1 == TAPS_PER_PHASE) 0 else newest + 1
            val slot = newest * channels
            for (channel in 0 until channels) {
                history[slot + channel] = input[frame * channels + channel]
            }

            while (phase < interpolation) {
                val phaseAt = phase * TAPS_PER_PHASE
                for (channel in 0 until channels) {
                    var sum = 0.0
                    var at = newest
                    for (k in 0 until TAPS_PER_PHASE) {
                        sum += taps[phaseAt + k] * history[at * channels + channel]
                        at = if (at == 0) TAPS_PER_PHASE - 1 else at - 1
                    }
                    output[written + channel] = sum.toFloat()
                }
                written += channels
                phase += decimation
            }
            phase -= interpolation
        }

        return written
    }

    /**
     * Forgets the filter history.
     *
     * Called when a stream restarts, so the tail of the last track cannot
     * bleed into the first samples of the next one.
     */
    fun reset() {
        history.fill(0f)
        newest = 0
        phase = 0
    }
}
