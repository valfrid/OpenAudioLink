package org.openaudiolink.phone.sources

import android.content.Context
import android.net.Uri
import androidx.annotation.OptIn
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.util.UnstableApi
import androidx.media3.common.audio.SonicAudioProcessor
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.audio.AudioSink
import androidx.media3.exoplayer.audio.DefaultAudioSink
import androidx.media3.exoplayer.audio.TeeAudioProcessor
import org.openaudiolink.core.PcmRing
import org.openaudiolink.core.Rtp
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * The phone's own music, decoded and resampled to the wire rate.
 *
 * Media3 does the two things that would otherwise be a project each: it
 * decodes MP3, AAC (in M4A, MP4 and ADTS), FLAC, WAV, Ogg Vorbis, Opus,
 * AMR and Matroska — its own extractors plus the phone's own decoders, no
 * decoder extensions bundled — and `SonicAudioProcessor` resamples
 * whatever came out to 48 kHz. That matters more than it sounds — decision
 * 13 fixes one wire rate at 48 kHz and most music is 44.1, so *every*
 * ordinary track needs resampling before it can be sent.
 *
 * A `TeeAudioProcessor` placed after the resampler is the tap. The player
 * still runs a real audio sink because that is what paces the decoder, so
 * the phone's own volume is set to zero: the speakers play, the phone does
 * not, and the pipeline is driven by the same clock either way.
 *
 * **This is where internet radio arrives**, whenever it does. A station is
 * a different `MediaItem` and nothing else — same decoder, same resampler,
 * same tap.
 *
 * **Worth being plain about resolution.** The wire is 24-bit, and a 16-bit
 * 44.1 kHz file resampled to 48 kHz is still that file. The format does
 * not add information; what it avoids is throwing any away between here
 * and the speaker.
 *
 * And in the other direction: a 24-bit file is folded to 16 before it
 * reaches the wire — see the note on float output in `start()`, which is
 * where that is forced and why. Everything is resampled to 48 kHz too, so
 * a 96 kHz file is a 48 kHz stream. Decision 13 fixes one wire rate and
 * this is what that costs.
 */
@OptIn(UnstableApi::class)
class LibrarySource(
    private val context: Context,
    private val uri: Uri,
    private val title: String,
) : AudioSource {

    override val label: String get() = title

    private var player: ExoPlayer? = null

    @Volatile private var ring: PcmRing? = null
    @Volatile private var playing = false

    /** Set from the sink's flush, read by the buffer handler. */
    @Volatile private var channels = Rtp.CHANNELS
    @Volatile private var encoding = C.ENCODING_PCM_16BIT

    override val isPlaying: Boolean get() = playing

    private val tap = object : TeeAudioProcessor.AudioBufferSink {
        override fun flush(sampleRateHz: Int, channelCount: Int, pcmEncoding: Int) {
            channels = channelCount
            encoding = pcmEncoding
            // A seek or a track change: what is held is from before it.
            ring?.clear()
        }

        override fun handleBuffer(buffer: ByteBuffer) {
            val target = ring ?: return
            val order = buffer.order()
            buffer.order(ByteOrder.nativeOrder())
            try {
                when (encoding) {
                    C.ENCODING_PCM_16BIT -> pushShort(buffer, target)
                    /*
                     * Not reachable as the sink is configured — float
                     * output is off, so the chain only ever sees 16-bit —
                     * and kept anyway. It costs one branch, and the day
                     * somebody turns float output back on is the day this
                     * is the difference between working and silent.
                     */
                    C.ENCODING_PCM_FLOAT -> pushFloat(buffer, target)
                    else -> Unit   // an encoding we cannot read is silence, not a crash
                }
            } finally {
                buffer.order(order)
            }
        }
    }

    private fun pushFloat(buffer: ByteBuffer, target: PcmRing) {
        val floats = buffer.asFloatBuffer()
        val scratch = FloatArray(floats.remaining())
        floats.get(scratch)
        offer(toStereo(scratch), target)
    }

    private fun pushShort(buffer: ByteBuffer, target: PcmRing) {
        val shorts = buffer.asShortBuffer()
        val scratch = FloatArray(shorts.remaining())
        for (i in scratch.indices) scratch[i] = shorts.get(i) / 32768f
        offer(toStereo(scratch), target)
    }

    /** Mono is duplicated; the wire is stereo and always has been. */
    private fun toStereo(samples: FloatArray): FloatArray {
        if (channels == Rtp.CHANNELS) return samples
        if (channels != 1) return samples   // more than stereo: take it as it comes
        val out = FloatArray(samples.size * 2)
        for (i in samples.indices) {
            out[i * 2] = samples[i]
            out[i * 2 + 1] = samples[i]
        }
        return out
    }

    /**
     * Blocks until the ring takes everything.
     *
     * This is the back pressure that keeps a decoder — which can produce a
     * minute of audio in a second — from running away from a wire that
     * cannot. It runs on Media3's audio thread, which is the right place
     * to be slow: being slow here pauses decoding, which is exactly what
     * is wanted.
     */
    private fun offer(samples: FloatArray, target: PcmRing) {
        var written = 0
        while (playing && written < samples.size) {
            val frames = target.write(samples, samples.size - written, written)
            written += frames * Rtp.CHANNELS
            if (frames == 0) Thread.sleep(2)
        }
    }

    override fun start(ring: PcmRing) {
        if (playing) return
        this.ring = ring
        playing = true

        val renderers = object : DefaultRenderersFactory(context) {
            override fun buildAudioSink(
                context: Context,
                enableFloatOutput: Boolean,
                enableAudioTrackPlaybackParams: Boolean,
            ): AudioSink {
                val resampler = SonicAudioProcessor().apply {
                    setOutputSampleRateHz(Rtp.SAMPLE_RATE)
                }
                return DefaultAudioSink.Builder(context)
                    /*
                     * Float output **off**, and this is not a preference.
                     *
                     * `DefaultAudioSink.configure()` builds two different
                     * pipelines, and only one of them contains the chain
                     * supplied above:
                     *
                     *     if (shouldUseFloatOutput(pcmEncoding)) {
                     *         pipeline.addAll(toFloatPcmAvailableAudioProcessors);
                     *     } else {
                     *         pipeline.addAll(toIntPcmAvailableAudioProcessors);
                     *         pipeline.add(audioProcessorChain.getAudioProcessors());
                     *     }
                     *
                     * and `shouldUseFloatOutput` is `enableFloatOutput &&
                     * isEncodingHighResolutionPcm(encoding)` — 24-bit,
                     * 32-bit and float. So with float output enabled, a
                     * 24-bit FLAC took a path with neither the resampler
                     * nor the tap in it: nothing was resampled to 48 kHz
                     * and nothing ever reached the ring. From the outside
                     * that is a track that plays silently forever, with
                     * the screen saying "published, waiting" and no packet
                     * ever sent — a fault that looks like the network.
                     *
                     * Turning it off puts every file through the int path,
                     * where `ToInt16PcmAudioProcessor` folds 24-bit,
                     * 32-bit and float down to 16-bit first, and the
                     * resampler and the tap run on everything.
                     *
                     * The cost, stated plainly: a 24-bit file reaches the
                     * wire as 16 bits. The wire is still L24 and every
                     * ordinary 16/44.1 recording is unaffected, but this
                     * app is not a high-resolution path and should not be
                     * described as one. Keeping the depth would mean
                     * resampling here rather than in Media3 — a general
                     * rate converter, not the fixed 147:160 the Spotify
                     * source uses — which is a real piece of work and not
                     * one to do by accident.
                     */
                    .setEnableFloatOutput(false)
                    .setAudioProcessorChain(
                        DefaultAudioSink.DefaultAudioProcessorChain(
                            resampler,
                            TeeAudioProcessor(tap),
                        )
                    )
                    .build()
            }
        }

        player = ExoPlayer.Builder(context, renderers).build().apply {
            setMediaItem(MediaItem.fromUri(uri))
            // The speakers play; the phone does not. The sink still runs,
            // because it is what paces the decoder.
            volume = 0f
            prepare()
            play()
        }
    }

    override fun stop() {
        playing = false
        player?.release()
        player = null
        ring = null
    }
}
