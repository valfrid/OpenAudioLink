package org.openaudiolink.phone.sources

import android.content.Context
import android.util.Log
import java.io.File

/**
 * Signing the cast point in, once, ever.
 *
 * **Discovery is not enough, and this is the whole reason this file
 * exists.** `docs/LIBRESPOT.md` records it from two evenings of measuring
 * the wrong things: current Spotify clients do not offer *unclaimed*
 * zeroconf devices in their picker. A librespot announcing itself
 * perfectly, on a network that provably carries the announcement, is
 * invisible to every Spotify client until it has authenticated — proven
 * over loopback on one machine, with no network involved at all.
 *
 * So publishing the cast point and then looking in Spotify finds nothing,
 * and that is not a fault. The receiver has to sign in once; after that it
 * is registered against the account and simply appears, anywhere that
 * account is used, with no multicast involved.
 *
 * The Hub does this with `--enable-oauth` at a terminal. A phone can do it
 * better: the browser, the redirect and librespot are all on the same
 * device, so the loopback the OAuth flow wants is right there.
 */
object SpotifyAccount {

    private const val TAG = "oal.spotify"

    /**
     * The line librespot prints when it wants a browser.
     *
     * It goes to **stdout**, via `println!` — the same stream the pipe
     * backend writes PCM to. That is why signing in is its own run of the
     * process with the audio sent to /dev/null: sharing one process would
     * put this sentence into the audio and hide the URL inside it.
     */
    private const val BROWSE_PREFIX = "Browse to: "

    /**
     * librespot's own OAuth redirect port, and it must not be changed.
     *
     * The redirect URI has to match the one registered against librespot's
     * client ID with Spotify. Choosing a different port produces an
     * authorisation page that refuses at the end rather than a failure
     * anyone can read.
     */
    private const val REDIRECT_PORT = 5588

    fun cacheDir(context: Context): File =
        File(context.filesDir, "librespot").apply { mkdirs() }

    /**
     * Where librespot keeps the credential it earns.
     *
     * App-private storage, and it stays there. This file is reusable
     * playback access to a real Spotify account — the same class of secret
     * as the Wi-Fi credentials this project keeps out of its repository —
     * so nothing exports it, logs it, or puts it in a status document.
     */
    fun credentials(context: Context): File = File(cacheDir(context), "credentials.json")

    fun isSignedIn(context: Context): Boolean = credentials(context).exists()

    /** Forgets the account. The next publish needs a fresh sign-in. */
    fun forget(context: Context): Boolean {
        val gone = credentials(context).delete()
        Log.i(TAG, if (gone) "credentials deleted" else "no credentials to delete")
        return gone
    }

    private fun binary(context: Context): File =
        File(context.applicationInfo.nativeLibraryDir, "liblibrespot.so")

    /**
     * Runs the one-time sign-in.
     *
     * Blocks, so call it off the main thread. Reports the authorisation URL
     * through [onUrl] the moment librespot prints it — the caller opens it
     * in a browser — and returns true once a credential has been written.
     *
     * The process is killed afterwards, deliberately. `LIBRESPOT.md` has
     * the reason and it is not tidiness: a second instance left running
     * keeps the cast point's name, so Spotify offers *it* rather than the
     * one that carries audio, and playing to it fails in a way that reads
     * as a broken speaker.
     */
    /** Set by [cancelSignIn]; the wait below watches it. */
    @Volatile private var cancelled = false

    /**
     * The sign-in process, held so a second attempt can end the first.
     *
     * Two librespots at once is the failure `LIBRESPOT.md` warns about
     * from the Hub: they carry the same cast point name, so Spotify offers
     * whichever it heard last, and the wrong one has no audio behind it.
     * On a phone there is a second reason — the OAuth listener binds
     * 127.0.0.1:5588, and a leftover holding that port makes every retry
     * fail before it starts.
     */
    @Volatile private var signInProcess: Process? = null

    /**
     * Ends any sign-in still running. Safe to call when none is.
     *
     * `destroy()` is SIGTERM and librespot does not always take the hint
     * promptly, so this waits a moment and then insists. A retry that
     * begins while the last attempt still holds the port fails in a way
     * that looks like Spotify refusing, which is the wrong place to look.
     */
    fun stopAnySignIn() {
        cancelled = true
        val running = signInProcess ?: return
        signInProcess = null
        try {
            running.destroy()
            for (i in 0 until 20) {
                if (!running.isAlive) break
                Thread.sleep(100)
            }
            if (running.isAlive) {
                Log.w(TAG, "sign-in process ignored destroy(); forcing it")
                running.destroyForcibly()
            }
        } catch (e: Exception) {
            Log.w(TAG, "could not stop the sign-in process", e)
        }
    }

    fun cancelSignIn() {
        cancelled = true
        stopAnySignIn()
    }

    /**
     * The last thing librespot said, kept for the screen.
     *
     * Telling somebody to run `adb logcat` is telling them to go and get a
     * computer. The lines that explain a failed sign-in should be on the
     * phone that failed.
     */
    private val recent = ArrayDeque<String>()

    @Synchronized private fun remember(line: String) {
        recent.addLast(line)
        while (recent.size > 12) recent.removeFirst()
    }

    @Synchronized fun lastOutput(): List<String> = recent.toList()

    fun signIn(
        context: Context,
        name: String,
        onUrl: (String) -> Unit,
    ): Boolean {
        val exe = binary(context)
        if (!exe.exists()) {
            Log.e(TAG, "no librespot to sign in with")
            return false
        }

        val cache = cacheDir(context)
        val command = listOf(
            exe.absolutePath,
            "--name", name,
            // Audio to nowhere: this run is only here to earn a credential,
            // and stdout has to stay clean for the URL.
            "--backend", "pipe",
            "--device", "/dev/null",
            "--cache", cache.absolutePath,
            "--disable-audio-cache",
            "--enable-oauth",
            "--oauth-port", REDIRECT_PORT.toString(),
        )

        /*
         * Clear the ground first.
         *
         * A previous attempt may still be holding 127.0.0.1:5588 and the
         * cast point's name. Starting a second one on top produces a
         * failure that reads as Spotify's — librespot cannot bind, gives
         * up, and the app reports a sign-in that never began.
         */
        stopAnySignIn()

        cancelled = false
        synchronized(this) { recent.clear() }
        Log.i(TAG, "signing in as \"$name\"")
        val process = try {
            ProcessBuilder(command)
                // The same writable scratch directory the publishing run
                // needs, and for the same reason — see SpotifySource. A
                // sign-in fetches less, but it is the same binary with the
                // same idea of where a temporary file goes.
                .apply { environment()["TMPDIR"] = SpotifySource.scratch(context).absolutePath }
                .start()
        } catch (e: Exception) {
            Log.e(TAG, "could not start librespot to sign in", e)
            return false
        }
        signInProcess = process

        Thread({
            try {
                process.errorStream.bufferedReader().forEachLine {
                    Log.i(TAG, "librespot: $it")
                    remember(it)
                }
            } catch (_: Exception) {
            }
        }, "oal-signin-log").apply { isDaemon = true }.start()

        /*
         * Read stdout for the URL. librespot also tries to open a browser
         * itself, through a crate that knows about xdg-open and Windows and
         * nothing about Android — so that attempt fails quietly and this
         * line is the only way the URL reaches anybody.
         */
        Thread({
            try {
                process.inputStream.bufferedReader().forEachLine { line ->
                    Log.i(TAG, "librespot out: $line")
                    remember(line)
                    val at = line.indexOf(BROWSE_PREFIX)
                    if (at >= 0) {
                        onUrl(line.substring(at + BROWSE_PREFIX.length).trim())
                    }
                }
            } catch (_: Exception) {
            }
        }, "oal-signin-out").apply { isDaemon = true }.start()

        /*
         * Wait for the credential to appear rather than for the process to
         * exit: librespot carries on running as a receiver once it is
         * signed in, and the file is what this run was for.
         *
         * **No deadline.** The first version gave it five minutes, which is
         * a plausible-sounding number and shorter than this actually takes:
         * a person types a username, waits for a code to arrive by email,
         * finds it, and types that. Five minutes ran out mid-sign-in and
         * the app reported a Spotify failure that was entirely its own
         * impatience. It now waits until it succeeds, until librespot
         * gives up, or until the person cancels.
         */
        var signedIn = false
        while (!cancelled) {
            if (credentials(context).exists()) {
                signedIn = true
                break
            }
            if (!process.isAlive) break
            Thread.sleep(500)
        }

        signInProcess = null
        process.destroy()
        for (i in 0 until 20) {
            if (!process.isAlive) break
            Thread.sleep(100)
        }
        if (process.isAlive) process.destroyForcibly()

        Log.i(TAG, if (signedIn) "signed in; credential cached" else "sign-in did not complete")
        return signedIn
    }
}
