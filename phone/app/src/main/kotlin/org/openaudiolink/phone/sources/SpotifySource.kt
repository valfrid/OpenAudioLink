package org.openaudiolink.phone.sources

import android.content.Context
import android.util.Log
import org.openaudiolink.core.LibrespotPcm
import org.openaudiolink.phone.WifiBinding
import org.openaudiolink.core.NowPlaying
import org.openaudiolink.core.PcmRing
import org.openaudiolink.core.SpotifyLog
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
 * One build carries it, alongside the vinyl node, the file player and the
 * tone — decision 21. Spotify at a party is what the phone hub is for, so
 * it is not a variant of the app but part of it.
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

    /**
     * The last few things librespot said, for the screen.
     *
     * Ten rather than one. A single line was enough to show that the
     * cast point had been reached and not enough to show what happened
     * to it: the one that arrived was librespot's stop handler
     * complaining that it had no context to fall back to, which is the
     * *consequence* of a failed session and says nothing about the cause.
     */
    private val recent = ArrayDeque<String>()

    @Volatile private var authenticated = false

    @Synchronized private fun remember(line: String) {
        recent.addLast(line)
        while (recent.size > KEPT_LINES) recent.removeFirst()
    }

    @Synchronized private fun snapshot(): List<String> = recent.toList()

    override val log: List<String> get() = snapshot()

    /*
     * Folded as the lines arrive rather than re-parsed from the kept ten.
     *
     * The log is a ring of the last few lines, so a track that loaded
     * twenty lines ago is no longer in it — and on a wall panel showing
     * one album for six minutes, that is the common case rather than the
     * edge one. Folding on arrival means what is playing outlives the line
     * that said so.
     */
    @Volatile private var playing: NowPlaying? = null

    override val nowPlaying: NowPlaying? get() = playing

    /**
     * Authentication is the fork in the road, so it is not left to be
     * inferred from a line scrolling past.
     *
     * Not authenticated: nothing else matters and no amount of pressing
     * play in Spotify will help. Authenticated and still silent: the
     * account reached Spotify and the problem is further in — which is a
     * completely different thing to go and look at.
     */
    override val ready: Boolean get() = authenticated

    override fun start(ring: PcmRing) {
        if (running) return
        running = true
        authenticated = false
        synchronized(this) { recent.clear() }
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
            /*
             * Should not happen: the build fetches librespot and fails if
             * it cannot. It is checked anyway because the alternative is
             * ProcessBuilder throwing from a background thread, which
             * reaches a person as a button that does nothing.
             */
            Log.e(TAG, "no librespot in ${exe.parent}; the build did not package it")
            running = false
            return
        }

        /*
         * Credentials are cached so a restart does not mean logging in from
         * the phone again; the *audio* cache is not, because it is large and
         * the audio is going straight out to the air.
         */
        val cache = File(context.filesDir, "librespot").apply { mkdirs() }

        val command = mutableListOf(
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

        /*
         * Which interface the cast point is advertised on.
         *
         * Without it libmdns binds every interface and lets the operating
         * system decide where multicast goes — the Hub passes this for the
         * same reason, where the wrong answer is a VPN. On a phone the
         * wrong answer is cellular, and a cast point advertised there is
         * one nobody in the room can see.
         *
         * An **address**, not an interface name: librespot parses this as
         * an IP and calls exit(1) on anything else, so "wlan0" would kill
         * the process before it ever reached the network.
         */
        WifiBinding(context).localAddress()?.let { address ->
            command += listOf("--zeroconf-interface", address)
        } ?: Log.w(TAG, "no Wi-Fi address; librespot will advertise on every interface")

        Log.i(TAG, "starting librespot as \"$name\"")

        val process = try {
            ProcessBuilder(command)
                // stderr into the same stream would corrupt the PCM, so it
                // goes to the log instead, where a login failure can be read.
                .redirectErrorStream(false)
                .apply { environment()["TMPDIR"] = scratch(context).absolutePath }
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

    /**
     * librespot's own diagnostics, kept where a person can read them.
     *
     * A failed login appears here and nowhere else, and "Authenticated
     * as …" is the one line that separates a cast point Spotify has not
     * shown yet from one that never connected. It goes on the screen for
     * the same reason the sign-in output does: the phone that failed is
     * where the explanation belongs.
     */
    private fun drainErrors(errors: InputStream) {
        try {
            errors.bufferedReader().forEachLine { line ->
                Log.i(TAG, "librespot: $line")

                /*
                 * The one line worth recognising rather than merely
                 * displaying. Everything downstream of a failed
                 * authentication looks like a network fault, and this is
                 * what tells the two apart.
                 */
                if (line.contains(AUTHENTICATED)) authenticated = true

                /*
                 * The timestamp and module are noise on a phone screen,
                 * but the *level* is not: it is what separates librespot
                 * narrating from librespot complaining. So the prefix is
                 * dropped and a marker kept.
                 */
                val text = line.substringAfterLast("] ").trim()
                if (text.isBlank()) return@forEachLine
                playing = SpotifyLog.update(playing, text)
                val loud = line.contains(" ERROR") || line.contains(" WARN")
                remember(if (loud) "! $text" else text)
            }
        } catch (_: Exception) {
        }
    }

    companion object {
        private const val TAG = "oal.spotify"

        /**
         * Somewhere librespot is allowed to write, which is not the default.
         *
         * This is the whole reason Spotify played nothing on a real phone.
         * With `--disable-audio-cache` librespot streams each track into a
         * temporary file from the `tempfile` crate, which asks Rust for
         * `std::env::temp_dir()` — and Rust's implementation is:
         *
         *     env::var_os("TMPDIR").map(PathBuf::from).unwrap_or_else(|| {
         *         if cfg!(target_os = "android") { "/data/local/tmp".into() }
         *         else { "/tmp".into() }
         *     })
         *
         * `/data/local/tmp` belongs to the shell, not to apps. So librespot
         * authenticated, took the play command, resolved the track, and
         * then could not create the file to download it into:
         *
         *     Unable to load encrypted file: PermissionDenied,
         *       PathError { path: "/data/local/tmp/.tmpIyyh2o", code: 13 }
         *     Skipping to next track, unable to load track
         *
         * and then the next, and the next, until "there are no more tracks
         * left in queue". Every layer above worked perfectly and nothing
         * was ever going to come out.
         *
         * The cfg is compile-time, so patching `env::consts::OS` to "linux"
         * for the OAuth client ID does not change it — this build still
         * looks in the Android place, and has to be told otherwise.
         *
         * Not cleaned on start, deliberately: a sign-in may be running
         * alongside and using the same directory, and deleting a file out
         * from under it would be a new bug in place of an old one. It is
         * the app's cache directory, which Android reclaims under storage
         * pressure.
         */
        fun scratch(context: Context): File =
            File(context.cacheDir, "librespot-tmp").apply { mkdirs() }

        /** Spotify decodes to 44.1 kHz, always. */
        private const val SPOTIFY_RATE = 44_100

        /** How many of librespot's lines are kept for the screen. */
        private const val KEPT_LINES = 10

        /** librespot's own words on the only question that gates the rest. */
        private const val AUTHENTICATED = "Authenticated as"
    }
}
