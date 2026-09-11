package org.openaudiolink.core

/**
 * What is playing, as much of it as the source will say.
 *
 * A wall panel is read from across a room, and "Spotify — OAL Phone" tells
 * somebody standing there nothing they did not already know. This is the
 * other question: *what is this?*
 *
 * Every field but [title] is optional, because the two sources that can
 * answer at all answer differently and neither answers fully:
 *
 *  - **Internet radio** sends a station name in its headers and a free-text
 *    `StreamTitle` in the stream, conventionally `Artist - Title`. There is
 *    no duration — a live stream has no end — and nothing is guaranteed.
 *  - **Spotify** says a title and a duration in librespot's own log, and
 *    says nothing at all about the artist.
 *
 * So this is deliberately shaped around what is actually obtainable rather
 * than around what a music player usually shows. Album art is neither, and
 * is not here.
 *
 * In `core` because it is string handling with sharp edges — a title
 * containing a hyphen, a duration that has not arrived yet — and that is
 * exactly the kind of thing this module exists to keep testable without an
 * Android device in the room.
 */
data class NowPlaying(
    val title: String,
    val artist: String? = null,
    val station: String? = null,
    val durationMs: Long? = null,
) {
    /** Artist and title as one line, for somewhere there is only one. */
    val line: String get() = if (artist.isNullOrBlank()) title else "$artist — $title"
}

/**
 * librespot's log, read for the two lines that say what is playing.
 *
 * There is no metadata API here to call. librespot is a separate process
 * whose stderr this app already reads to tell "not signed in" from "signed
 * in and idle", and the same stream happens to narrate track changes:
 *
 * ```
 * Loading <Everybody Hurts> with Spotify URI <spotify:track:...>
 * <Everybody Hurts> (320266 ms) loaded
 * ```
 *
 * So the title and duration were on screen already, as raw log lines under
 * *Show details*. This parses what was being displayed.
 *
 * **The alternative was worse.** `--onevent` runs an external program per
 * event, which on Android means a second executable in the native library
 * directory — the W^X rule permits execution from nowhere else — and from
 * what its own source says it would supply position and track id rather
 * than a name. More machinery for less than the log already gives.
 *
 * What this cannot do is the artist, which librespot never prints. A panel
 * showing Spotify gets a title; a panel showing radio gets both.
 */
object SpotifyLog {

    /*
     * Anchored at both ends, and greedy in the middle.
     *
     * A track called "1950" and a track called "<3 (feat. > )" both have to
     * work, so the title is allowed to contain anything at all: the fixed
     * suffix is what bounds it, and a greedy group backtracks to the last
     * one on the line rather than the first.
     */
    private val LOADING = Regex("""^Loading <(.+)> with Spotify URI <[^>]*>$""")
    private val LOADED = Regex("""^<(.+)> \((\d+) ms\) loaded$""")

    /** Lines that mean nothing is playing any more. */
    private val STOPPED = listOf(
        "no more tracks left in queue",
        "device became inactive",
    )

    /**
     * Folds one log line into what was known before it.
     *
     * Returns the new state, which may be [previous] unchanged — most lines
     * say nothing about the track — or null when the line says playback has
     * ended.
     *
     * Order matters between the two patterns and is handled by keeping the
     * title: `Loading` arrives first with no duration, `loaded` arrives
     * after with one. A panel that showed nothing until the duration landed
     * would flicker on every track change for no reason.
     */
    fun update(previous: NowPlaying?, line: String): NowPlaying? {
        val text = line.removePrefix("! ").trim()

        LOADED.find(text)?.let { m ->
            val title = m.groupValues[1]
            val ms = m.groupValues[2].toLongOrNull()
            return NowPlaying(title = title, durationMs = ms)
        }

        LOADING.find(text)?.let { m ->
            val title = m.groupValues[1]
            // Same track as the one already known: keep the duration
            // rather than dropping back to null and re-acquiring it.
            if (previous?.title == title) return previous
            return NowPlaying(title = title)
        }

        if (STOPPED.any { text.contains(it, ignoreCase = true) }) return null

        return previous
    }

    /** Every line in order, for a log that arrived before anybody looked. */
    fun readAll(lines: List<String>): NowPlaying? =
        lines.fold(null as NowPlaying?) { acc, line -> update(acc, line) }
}

/**
 * Shoutcast and Icecast metadata, which is one free-text field.
 *
 * A server interleaves `StreamTitle='…'` into the audio and the decoder
 * hands it over already unquoted. By convention it is `Artist - Title`, and
 * convention is all it is: some stations send only a title, some send the
 * station name, some send advertising, and some send nothing ever.
 */
object IcyTitle {

    /**
     * Splits a stream title into artist and title, conservatively.
     *
     * **On the first separator, not the last.** `Simon - The Sound of
     * Silence - Live` is far more likely to be an artist and a title that
     * contains a hyphen than an artist whose name contains one, and
     * splitting at the end gets that backwards.
     *
     * A title with no separator is a title, not an artist: a panel reading
     * an artist with no track is worse than one reading a track with no
     * artist, because the second is obviously incomplete and the first
     * looks wrong.
     */
    fun parse(streamTitle: String?, station: String? = null): NowPlaying? {
        val raw = streamTitle?.trim()?.trim('\'', '"')?.trim().orEmpty()
        if (raw.isEmpty()) {
            // The station alone is still worth showing: it is what a
            // listener would call this, and a radio with no track metadata
            // is common rather than broken.
            val name = station?.trim().orEmpty()
            return if (name.isEmpty()) null else NowPlaying(title = name, station = name)
        }

        val at = raw.indexOf(" - ")
        if (at <= 0 || at + 3 >= raw.length) {
            return NowPlaying(title = raw, station = station?.trim()?.ifBlank { null })
        }
        return NowPlaying(
            title = raw.substring(at + 3).trim(),
            artist = raw.substring(0, at).trim(),
            station = station?.trim()?.ifBlank { null },
        )
    }
}
