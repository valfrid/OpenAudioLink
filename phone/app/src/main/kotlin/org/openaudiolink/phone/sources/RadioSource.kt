package org.openaudiolink.phone.sources

import android.content.Context
import android.net.Uri
import android.util.Log
import org.openaudiolink.core.PcmRing
import org.openaudiolink.core.Station
import org.openaudiolink.core.StationKind
import org.openaudiolink.core.StationPlaylist
import java.net.HttpURLConnection
import java.net.URL

/**
 * An internet radio station, which is a URL and nothing else.
 *
 * **The cheapest source this project will ever add**, and on Android
 * cheaper than on the Hub. `LibrarySource` already fetches, decodes,
 * resamples to 48 kHz and taps the result; Media3 decodes MP3, AAC and
 * FLAC over HTTP with its own network stack, and has a real HLS client for
 * the segment playlists the Hub deliberately refuses. So a station is a
 * different `MediaItem` and this class is mostly the paperwork around it —
 * which is exactly what the note at the top of `AudioSource` predicted
 * when the interface was shaped.
 *
 * The Hub's `RadioSource` is a week of work by comparison, and the
 * difference is not skill: Windows decodes FLAC only in its own container,
 * ships no Ogg demuxer, and its MP3 reader wants a seekable stream, which
 * a station is the opposite of. Android's decoders were written for
 * streaming from the start.
 *
 * What does not come free is **resolving what somebody pasted**. Half of
 * what people call a stream URL is a few lines of text naming the real one,
 * and handing that to a decoder is the worst way to find out — see
 * `StationPlaylist`, ported from the Hub with its reasoning intact.
 */
class RadioSource(
    private val context: Context,
    private val station: Station,
) : AudioSource {

    override val label: String get() = station.name

    /**
     * The player, once a URL has been resolved.
     *
     * A station is played by exactly the pipeline a local file is, so this
     * delegates rather than duplicating it. Everything `LibrarySource`
     * learned about Media3 — the int path, the tap after the resampler,
     * the sink that paces the decoder — applies unchanged to a station.
     */
    @Volatile private var player: LibrarySource? = null

    @Volatile private var started = false
    @Volatile private var note: String? = null

    override val isPlaying: Boolean get() = started && player?.isPlaying != false

    override val log: List<String> get() = listOfNotNull(note)

    override fun start(ring: PcmRing) {
        if (started) return
        started = true

        /*
         * Resolving is a network fetch, so it cannot happen here: this is
         * called from the UI's coroutine and a URL fetch on the main
         * thread is the NetworkOnMainThreadException that took three
         * builds to find the first time.
         */
        Thread({ resolveAndPlay(ring) }, "oal-radio-open").apply { isDaemon = true }.start()
    }

    private fun resolveAndPlay(ring: PcmRing) {
        try {
            open(ring)
        } catch (e: Exception) {
            /*
             * Nothing on this thread may escape.
             *
             * An uncaught exception on a bare thread kills the process,
             * and this one runs a network fetch on a URL somebody typed —
             * the single most likely thing in the app to throw. A station
             * that will not open is a line on the screen, not a closed
             * app.
             */
            Log.e(TAG, "station ${station.name} did not open", e)
            note = "could not open this station: ${e.message ?: e::class.simpleName}"
            started = false
        }
    }

    private fun open(ring: PcmRing) {
        val url = try {
            resolve(station.url)
        } catch (e: Exception) {
            Log.w(TAG, "could not resolve ${station.url}", e)
            /*
             * Fall back to what was pasted rather than refusing. Some
             * stations cannot be identified from a fetch — join an Icecast
             * stream mid-broadcast and its header went out hours ago — and
             * a real demuxer given the URL knows more than we do. That is
             * the Hub's correction, learned when refusing an
             * unidentifiable stream turned "I cannot tell" into "this will
             * not play".
             */
            station.url
        }

        if (!started) return
        note = "playing $url"
        Log.i(TAG, "station ${station.name}: $url")
        /*
         * `LibrarySource` confines every player call to the main thread
         * itself, so starting it from here is safe. It was not always:
         * ExoPlayer binds to the Looper of whichever thread built it and
         * rejects calls from any other, so this line used to throw
         * `Player is accessed on the wrong thread` and, uncaught on a bare
         * thread, close the app the moment anybody pressed Play.
         */
        LibrarySource(context, Uri.parse(url), station.name)
            .also { player = it }
            .start(ring)
    }

    /**
     * Fetches the URL far enough to tell a playlist from audio.
     *
     * **The body is only read when the content type says text.** Reading
     * even the first line of a live stream consumes audio and blocks until
     * more arrives, so the type decides and an unknown type is treated as
     * audio.
     */
    private fun resolve(url: String): String {
        val connection = (URL(url).openConnection() as HttpURLConnection).apply {
            connectTimeout = RESOLVE_TIMEOUT_MS
            readTimeout = RESOLVE_TIMEOUT_MS
            instanceFollowRedirects = true
            // Icecast answers a plain GET with audio and this with metadata.
            setRequestProperty("User-Agent", "OpenAudioLink")
        }
        try {
            if (!StationPlaylist.looksLikePlaylist(connection.contentType, url)) return url

            val body = connection.inputStream.bufferedReader()
                .use { it.readText().take(PLAYLIST_LIMIT) }
            val target = StationPlaylist.parse(body)
            return when (target.kind) {
                // Media3 has a real HLS client, so the URL itself is right.
                StationKind.SEGMENT_PLAYLIST -> url
                StationKind.STREAM -> target.urls.first()
                StationKind.UNUSABLE -> url
            }
        } finally {
            connection.disconnect()
        }
    }

    override fun stop() {
        started = false
        player?.stop()
        player = null
    }

    private companion object {
        const val TAG = "oal.radio"
        const val RESOLVE_TIMEOUT_MS = 8_000

        /** A playlist is a few lines; anything longer is not one. */
        const val PLAYLIST_LIMIT = 64 * 1024
    }
}
