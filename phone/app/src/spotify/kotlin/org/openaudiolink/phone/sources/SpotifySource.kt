package org.openaudiolink.phone.sources

import android.content.Context
import android.util.Log
import org.openaudiolink.core.LibrespotPcm
import org.openaudiolink.core.PcmRing
import org.openaudiolink.core.RationalResampler
import org.openaudiolink.core.Rtp
import java.io.File
import java.io.InputStream

/**
 * Spotify Connect, by running librespot and reading its pipe.
 *
 * The same arrangement the Windows Hub uses, for the same reason: librespot
 * is a Rust program, not a library with a Java binding, and `--backend
 * pipe` makes it a process that writes raw PCM to stdout. The pipe is also
 * the flow control — stop reading and the kernel buffer fills, which blocks
 * librespot, which is exactly the back pressure a producer wants.
 *
 * **This is the cast point.** librespot publishes its own mDNS name, so
 * whatever [name] is here is what appears in the Spotify picker — on this
 * phone and on everyone else's. That is the party-mode feature: a guest
 * picks it in their own Spotify app and their music comes out of the
 * speakers this phone has ticked.
 *
 * Only in the `spotify` flavour. The `plain` build has no such file and no
 * librespot binary in it — see decision 21.
 */
class SpotifySource(
    private val context: Context,
    private val name: String,
) : AudioSource {

    override val label: String get() = "Spotify — $name"

    @Volatile private var running = false
    private var process: Process? = null
    private var thread: Thread? = null

    override val isPlaying: Boolean get() = running

    override fun start(ring: PcmRing) {
        if (running) return
        running = true
        thread = Thread({ run(ring) }, "oal-librespot").apply { start() }
    }

    override fun stop() {
        running = false
        // The reader is blocked on a pipe that killing the process closes.
        process?.destroy()
        process = null
        thread?.join(1_000)
        thread = null
    }

    /**
     * Where the binary lives, and why it is not in `filesDir`.
     *
     * Android refuses to execute a file from an app's data directory —
     * W^X, enforced since Android 10 — but will execute one from the APK's
     * native library directory. Anything named `lib*.so` in `jniLibs` is
     * unpacked there, so librespot travels under a library's name. It is an
     * executable, not a shared object; the extension is a packaging
     * convention, not a claim about the file.
     */
    private fun binary(): File =
        File(context.applicationInfo.nativeLibraryDir, "liblibrespot.so")

    private fun run(ring: PcmRing) {
        val exe = binary()
        if (!exe.exists()) {
            // The plain flavour, or a build whose fetch step was skipped.
            Log.e(TAG, "no librespot in ${exe.parent}; this is not a Spotify build")
            running = false
            return
        }

        /*
         * Credentials are cached so a restart does not mean logging in from
         * the phone again; the *audio* cache is not, because it is large and
         * the audio is going straight out to the air.
         */
        val cache = File(context.filesDir, "librespot").apply { mkdirs() }

        val command = listOf(
            exe.absolutePath,
            "--name", name,
            "--backend", "pipe",
            "--format", "S16",
            "--device-type", "speaker",
            "--bitrate", "320",
            // Decision 14: the only gain in the system belongs to the
            // speaker. "fixed" is librespot's name for a volume control
            // that scales nothing.
            "--volume-ctrl", "fixed",
            "--cache", cache.absolutePath,
            "--disable-audio-cache",
        )

        Log.i(TAG, "starting librespot as \"$name\"")

        val process = try {
            ProcessBuilder(command)
                // stderr into the same stream would corrupt the PCM, so it
                // goes to the log instead, where a login failure can be read.
                .redirectErrorStream(false)
                .start()
        } catch (e: Exception) {
            Log.e(TAG, "could not start librespot", e)
            running = false
            return
        }
        this.process = process

        Thread({ drainErrors(process.errorStream) }, "oal-librespot-log")
            .apply { isDaemon = true }.start()

        pump(process.inputStream, ring)

        Log.i(TAG, "librespot ended")
        running = false
    }

    /**
     * Reads PCM until the pipe closes, resampling on the way in.
     *
     * Spotify is 44.1 kHz and the wire is 48, so every track goes through
     * the filter — this is the one source that needs it.
     */
    private fun pump(output: InputStream, ring: PcmRing) {
        val resampler = RationalResampler(SPOTIFY_RATE, Rtp.SAMPLE_RATE, Rtp.CHANNELS)
        resampler.reset()

        val raw = ByteArray(16 * 1024)
        val decoded = FloatArray(raw.size / LibrespotPcm.S16_WIDTH)
        val resampled = FloatArray(
            (resampler.maxOutputFrames(decoded.size / Rtp.CHANNELS) + 1) * Rtp.CHANNELS
        )

        var carried = 0
        while (running) {
            val read = try {
                output.read(raw, carried, raw.size - carried)
            } catch (_: Exception) {
                return   // the process was killed, which is how stopping works
            }
            if (read <= 0) return

            /*
             * Whole frames only. A partial frame decoded now would put the
             * channels out of step for the rest of the track — a swap that
             * never recovers, rather than one bad sample.
             */
            val have = carried + read
            val frameBytes = LibrespotPcm.S16_WIDTH * Rtp.CHANNELS
            val frames = have / frameBytes
            val used = frames * frameBytes

            if (frames > 0) {
                val samples = LibrespotPcm.decodeS16(raw, used, decoded)
                val written = resampler.process(decoded, samples, resampled)
                offer(resampled, written, ring)
            }

            carried = have - used
            if (carried > 0) {
                System.arraycopy(raw, used, raw, 0, carried)
            }
        }
    }

    /**
     * Blocks until the ring takes it all.
     *
     * Being slow here is the point: it stops reading the pipe, the kernel
     * buffer fills, and librespot waits. That is the whole flow-control
     * mechanism, and it is why nothing here needs a rate limiter.
     */
    private fun offer(samples: FloatArray, count: Int, ring: PcmRing) {
        var written = 0
        while (running && written < count) {
            val frames = ring.write(samples, count - written, written)
            written += frames * Rtp.CHANNELS
            if (frames == 0) Thread.sleep(2)
        }
    }

    /** librespot's own diagnostics — a failed login is only visible here. */
    private fun drainErrors(errors: InputStream) {
        try {
            errors.bufferedReader().forEachLine { Log.i(TAG, "librespot: $it") }
        } catch (_: Exception) {
        }
    }

    private companion object {
        const val TAG = "oal.spotify"

        /** Spotify decodes to 44.1 kHz, always. */
        const val SPOTIFY_RATE = 44_100
    }
}
