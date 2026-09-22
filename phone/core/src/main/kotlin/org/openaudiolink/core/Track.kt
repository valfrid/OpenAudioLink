package org.openaudiolink.core

import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.Json

/**
 * A file on the phone that somebody has played once and may want again.
 *
 * [Station]'s shape, for the same reason: a label and something to play.
 * What differs is where the address points — a station's URL is public and
 * durable, a track's is a `content://` URI handed out by Android's document
 * picker and good only for as long as the grant behind it lasts.
 *
 * **Why this list exists at all.** The "A music file" tile used to open the
 * system picker directly, which made every play a trip through another
 * app's file browser — and on a device with gesture navigation there is no
 * visible way back out of that browser without choosing something. A
 * remembered list turns the common case into one tap and leaves the picker
 * for the rare one.
 *
 * The app already took a *persistable* grant on every URI it was given and
 * then threw the URI away, so the expensive half of this was already built
 * and only ever used once per play.
 */
@Serializable
data class Track(
    /** Stable, derived from the first name given. Never changes. */
    val id: String,
    /** What the button says — the document's display name, usually. */
    val name: String,
    /**
     * The `content://` URI, exactly as the picker gave it.
     *
     * Stored as a string rather than an `android.net.Uri` because this
     * module is plain Kotlin and runs in a host JVM test. The app parses it
     * on the way out, which is one line and keeps the model testable.
     *
     * **It can stop working**, unlike a station URL: the file can be
     * deleted, an SD card removed, or the grant dropped when app data is
     * cleared. That is not worth guarding against on the way in — a URI
     * that resolves today may not tomorrow — so the list tolerates dead
     * entries and the Remove button is how they leave.
     */
    val uri: String,
) {
    companion object {
        /**
         * How many are kept.
         *
         * Android caps the number of persistable URI grants a package may
         * hold — 128 on older releases, 512 from API 30 — and silently
         * starts dropping the oldest once the cap is reached. Dropping the
         * oldest *here*, deliberately and while we can still release the
         * grant, keeps this list well inside that limit and means nothing
         * in it is a grant the system quietly revoked.
         */
        const val LIMIT = 50

        private val json = Json { ignoreUnknownKeys = true }

        // Named rather than inferred, exactly as Station does: the reified
        // overloads need an extra import, and without it the error points
        // at the argument instead of at the missing import.
        private val listOfTracks = ListSerializer(serializer())

        fun encode(tracks: List<Track>): String = json.encodeToString(listOfTracks, tracks)

        /**
         * @return the tracks, or an empty list if the text is not a list of
         * them. A saved file that will not parse is not worth taking an app
         * down for.
         */
        fun decode(raw: String?): List<Track> = try {
            if (raw.isNullOrBlank()) emptyList() else json.decodeFromString(listOfTracks, raw)
        } catch (_: Exception) {
            emptyList()
        }

        /**
         * Adds a picked file, newest first.
         *
         * Three rules, and each one is there because of how the picker
         * behaves rather than out of tidiness:
         *
         * - **Identity is the URI**, not the name. Two files called
         *   `track01.mp3` in different albums are two tracks; the same file
         *   picked twice is one.
         * - **Re-picking moves it to the top** rather than duplicating it.
         *   Somebody who went all the way through the picker for a file
         *   they already had wants that file now.
         * - **Newest first**, because the list exists to make the picker
         *   rare and the thing most likely to be wanted next is the thing
         *   most recently chosen. Playing from the list does *not* reorder
         *   it — a list that rearranges itself under a finger is a list
         *   people mis-tap.
         *
         * @return the new list, or the old one unchanged if [uri] is blank.
         */
        fun add(existing: List<Track>, name: String, uri: String, limit: Int = LIMIT): List<Track> {
            val cleanUri = uri.trim()
            if (cleanUri.isEmpty()) return existing

            val known = existing.firstOrNull { it.uri == cleanUri }
            if (known != null) return listOf(known) + existing.filterNot { it.uri == cleanUri }

            val cleanName = name.trim().ifEmpty { fallbackName(cleanUri) }
            val track = Track(
                id = idFor(cleanName, existing.map { it.id }.toSet()),
                name = cleanName,
                uri = cleanUri,
            )
            return (listOf(track) + existing).take(limit)
        }

        /**
         * A slug from a name, so a track keeps one identity across the list
         * being reordered. [Station.idFor]'s rules, and deliberately its
         * behaviour: ids are what the screen keys rows on and what Remove
         * names, so two tracks may not share one.
         */
        fun idFor(name: String, taken: Set<String> = emptySet()): String {
            val base = name.lowercase()
                .map { if (it.isLetterOrDigit()) it else '-' }
                .joinToString("")
                .trim('-')
                .replace(Regex("-+"), "-")
                .ifEmpty { "track" }
            if (base !in taken) return base
            var n = 2
            while ("$base-$n" in taken) n++
            return "$base-$n"
        }

        /**
         * Something to call a file whose display name could not be read.
         *
         * Rare but real: a provider may answer the name query with nothing
         * at all. The tail of the URI is a poor label — often an opaque
         * document id — and it is still better than a blank row, because a
         * blank row cannot be told apart from the one below it.
         */
        private fun fallbackName(uri: String): String =
            uri.substringAfterLast('/')
                .substringAfterLast("%3A")
                .substringAfterLast(':')
                .ifBlank { "Track" }
    }
}
