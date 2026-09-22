package org.openaudiolink.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * The remembered-file list, and the picker behaviour each rule is for.
 *
 * Every URI below is the shape Android's document picker actually hands
 * out — opaque document ids, percent-encoded colons and all — rather than
 * a tidy invented one, because the ugly ones are what the name fallback
 * and the identity rule have to survive.
 */
class TrackTest {

    private val song =
        "content://com.android.providers.media.documents/document/audio%3A1000000123"
    private val other =
        "content://com.android.providers.media.documents/document/audio%3A1000000456"

    @Test
    fun `a picked file is remembered under its display name`() {
        val tracks = Track.add(emptyList(), "Everybody Hurts.mp3", song)
        assertEquals(1, tracks.size)
        assertEquals("Everybody Hurts.mp3", tracks[0].name)
        assertEquals(song, tracks[0].uri)
    }

    @Test
    fun `the newest is first`() {
        var tracks = Track.add(emptyList(), "First", song)
        tracks = Track.add(tracks, "Second", other)
        assertEquals(listOf("Second", "First"), tracks.map { it.name })
    }

    @Test
    fun `the same file picked twice is one entry, moved to the top`() {
        // Somebody who went all the way through the picker for a file they
        // already had wants it now, not a second row saying the same thing.
        var tracks = Track.add(emptyList(), "First", song)
        tracks = Track.add(tracks, "Second", other)
        tracks = Track.add(tracks, "First", song)
        assertEquals(listOf("First", "Second"), tracks.map { it.name })
        assertEquals(2, tracks.size)
    }

    @Test
    fun `identity is the URI and not the name`() {
        // Two albums, both with a track01.mp3. Collapsing them would make
        // one of the two unplayable.
        var tracks = Track.add(emptyList(), "track01.mp3", song)
        tracks = Track.add(tracks, "track01.mp3", other)
        assertEquals(2, tracks.size)
        assertEquals(setOf(song, other), tracks.map { it.uri }.toSet())
    }

    @Test
    fun `two files with one name still get two ids`() {
        val tracks = Track.add(Track.add(emptyList(), "track01.mp3", song), "track01.mp3", other)
        assertEquals(2, tracks.map { it.id }.toSet().size)
    }

    @Test
    fun `a blank URI is not a track`() {
        assertTrue(Track.add(emptyList(), "Nothing", "   ").isEmpty())
    }

    @Test
    fun `a nameless file still gets a label`() {
        val tracks = Track.add(emptyList(), "", song)
        assertEquals("1000000123", tracks[0].name)
    }

    @Test
    fun `a nameless file with nothing to fall back on is still not blank`() {
        val tracks = Track.add(emptyList(), "", "content://x/")
        assertEquals("Track", tracks[0].name)
    }

    @Test
    fun `the oldest falls off the end`() {
        // The cap is not tidiness: Android silently drops the oldest
        // persistable grant once a package holds too many, and dropping
        // one here means we can release it while it is still ours.
        var tracks = emptyList<Track>()
        for (i in 1..5) tracks = Track.add(tracks, "Track $i", "content://x/$i", limit = 3)
        assertEquals(listOf("Track 5", "Track 4", "Track 3"), tracks.map { it.name })
    }

    @Test
    fun `a round trip through storage keeps the list`() {
        var tracks = Track.add(emptyList(), "Everybody Hurts", song)
        tracks = Track.add(tracks, "The Sound of Silence", other)
        assertEquals(tracks, Track.decode(Track.encode(tracks)))
    }

    @Test
    fun `an unreadable saved list is an empty one`() {
        // A preferences file that will not parse is not worth a crash on
        // launch — the list rebuilds itself the next time somebody picks.
        assertTrue(Track.decode("{ not a list").isEmpty())
        assertTrue(Track.decode(null).isEmpty())
        assertTrue(Track.decode("").isEmpty())
    }
}
