package org.openaudiolink.core

import kotlinx.serialization.Serializable

/**
 * A saved internet radio station: a name somebody chose and the URL behind
 * it.
 *
 * The Hub's `Station` record, in Kotlin, deliberately field for field. Two
 * halves of one project should not disagree about what a station is, and
 * the day these lists are exchanged rather than typed twice, the shapes
 * already match.
 *
 * No genre, no logo, no bitrate. Those come from a station directory and
 * the directory is a later job; what a switchboard needs is a label and
 * something to play.
 */
@Serializable
data class Station(
    /** Stable, derived from the first name given. Never changes. */
    val id: String,
    /** What the button says. */
    val name: String,
    /**
     * What was pasted, unresolved.
     *
     * A playlist URL is the durable address of a station and the stream
     * URLs behind it move, so resolving happens afresh at each play rather
     * than once on the way in — see [StationPlaylist].
     */
    val url: String,
) {
    companion object {
        /**
         * A slug from a name, so a station keeps one identity across
         * renames.
         */
        fun idFor(name: String, taken: Set<String> = emptySet()): String {
            val base = name.lowercase()
                .map { if (it.isLetterOrDigit()) it else '-' }
                .joinToString("")
                .trim('-')
                .replace(Regex("-+"), "-")
                .ifEmpty { "station" }
            if (base !in taken) return base
            var n = 2
            while ("$base-$n" in taken) n++
            return "$base-$n"
        }
    }
}

/** What a URL turned out to be pointing at. */
enum class StationKind {
    /** An endless Icecast or Shoutcast stream. What we can play. */
    STREAM,

    /**
     * A segment playlist — HLS or DASH.
     *
     * The Hub refuses these, because fetching a rolling list of four-second
     * files and handling their discontinuities is a client rather than a
     * fetch. **Android is the exception**: Media3 has a real HLS client, so
     * the phone hands the URL straight to it. The value still exists here
     * because the two halves share this parser, and because naming what
     * something is beats discovering it inside a decoder.
     */
    SEGMENT_PLAYLIST,

    /** Nothing usable was found. */
    UNUSABLE,
}

/**
 * The result of resolving whatever somebody pasted.
 *
 * @property urls stream URLs in the order the playlist gave them, which is
 * the order to try: stations list mirrors, and the first is the one they
 * prefer.
 */
data class StationTarget(val kind: StationKind, val urls: List<String> = emptyList())

/**
 * Turns a pasted radio URL into the stream URLs behind it.
 *
 * A port of the Hub's `StationPlaylist`, and the reasoning transfers whole:
 * half of what people call a "stream URL" is a few lines of text naming the
 * real one — a `.pls` from Shoutcast, an `.m3u` from almost everyone else.
 * Resolving that is trivial and skipping it is not, because without it the
 * first URL anyone tries fails inside the decoder, which is the worst place
 * to discover a text file.
 *
 * Content-based rather than extension-based, deliberately: stations serve
 * `.m3u` from URLs ending in `.pls` and the reverse, and plenty serve a
 * playlist from a URL with no extension at all.
 */
object StationPlaylist {

    /**
     * Reads a fetched document, returning the URLs it names or why it
     * cannot.
     */
    fun parse(content: String?): StationTarget {
        if (content.isNullOrBlank()) return StationTarget(StationKind.UNUSABLE)

        val lines = content.split('\n', '\r')

        /*
         * HLS first, because an HLS playlist is a valid m3u and would
         * otherwise parse into a list of segment files that play for four
         * seconds each and then stop. Either the media tags or a variant
         * list is enough to tell them apart.
         */
        if (lines.any { it.trim().startsWith("#EXT-X-", ignoreCase = true) }) {
            return StationTarget(StationKind.SEGMENT_PLAYLIST)
        }

        val urls = mutableListOf<String>()
        for (raw in lines) {
            val line = raw.trim()
            if (line.isEmpty()) continue

            // A .pls entry: File1=http://..., possibly spaced around the
            // equals sign. Title and Length lines are ignored.
            if (line.startsWith("File", ignoreCase = true)) {
                val equals = line.indexOf('=')
                if (equals > 0) add(urls, line.substring(equals + 1).trim())
                continue
            }

            // Comments in either format, #EXTINF track titles included.
            if (line.startsWith('#') || line.startsWith(';')) continue

            // Anything else in a .m3u is a URL, and a bare URL is itself.
            add(urls, line)
        }

        return if (urls.isNotEmpty()) {
            StationTarget(StationKind.STREAM, urls)
        } else {
            StationTarget(StationKind.UNUSABLE)
        }
    }

    /**
     * Whether a fetched body should be parsed rather than played.
     *
     * Audio arrives as bytes and a playlist as text, so the content type
     * decides — and **a stream must never be read as text**, because
     * reading even the first line of one consumes audio and blocks until
     * it arrives. Unknown types are treated as audio: a station serving
     * `application/octet-stream` is common, a playlist doing so is not.
     */
    fun looksLikePlaylist(contentType: String?, url: String?): Boolean {
        val type = contentType?.substringBefore(';')?.trim()?.lowercase()
        if (type != null) {
            if (type in PLAYLIST_TYPES) return true
            if (type.startsWith("audio/") || type.startsWith("application/octet-stream")) {
                return false
            }
            if (type.startsWith("text/")) return true
        }

        // No usable type: fall back to the extension, which is a hint and
        // not evidence, so it only ever adds a suspicion.
        val path = url?.substringBefore('?')?.lowercase() ?: return false
        return path.endsWith(".pls") || path.endsWith(".m3u")
    }

    private fun add(urls: MutableList<String>, candidate: String) {
        val trimmed = candidate.trim()
        if (trimmed.startsWith("http://", ignoreCase = true) ||
            trimmed.startsWith("https://", ignoreCase = true)
        ) {
            urls.add(trimmed)
        }
    }

    private val PLAYLIST_TYPES = setOf(
        "audio/x-mpegurl",
        "audio/mpegurl",
        "application/vnd.apple.mpegurl",
        "audio/x-scpls",
        "application/pls+xml",
    )
}
