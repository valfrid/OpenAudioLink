package org.openaudiolink.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * Resolving what somebody pasted, before a decoder has to guess.
 *
 * Half of what people call a "stream URL" is a few lines of text naming
 * the real one. The Hub learned this the expensive way — the worst place
 * to discover a text file is inside a decoder, which searches for a sync
 * word until the music stops and reports nothing at all.
 */
class StationPlaylistTest {

    @Test
    fun `a pls names its files`() {
        val target = StationPlaylist.parse(
            """
            [playlist]
            NumberOfEntries=2
            File1=http://ice.example/stream.mp3
            Title1=Some Station
            Length1=-1
            File2 = http://backup.example/stream.mp3
            """.trimIndent()
        )
        assertEquals(StationKind.STREAM, target.kind)
        assertEquals(
            listOf("http://ice.example/stream.mp3", "http://backup.example/stream.mp3"),
            target.urls,
            "mirrors in the order given: the first is the one they prefer",
        )
    }

    @Test
    fun `an m3u is bare urls and comments`() {
        val target = StationPlaylist.parse(
            """
            #EXTM3U
            #EXTINF:-1,A Station
            https://ice.example/flac

            ; an old-style comment
            https://mirror.example/flac
            """.trimIndent()
        )
        assertEquals(StationKind.STREAM, target.kind)
        assertEquals(2, target.urls.size)
    }

    /*
     * An HLS playlist is a valid m3u, so without this it parses into a
     * list of four-second segment files that play once and stop.
     */
    @Test
    fun `an HLS playlist is recognised rather than parsed`() {
        val target = StationPlaylist.parse(
            """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:4
            #EXTINF:4.0,
            segment-01.aac
            """.trimIndent()
        )
        assertEquals(StationKind.SEGMENT_PLAYLIST, target.kind)
        assertTrue(target.urls.isEmpty())
    }

    @Test
    fun `a bare url is its own answer`() {
        val target = StationPlaylist.parse("http://ice.example/stream")
        assertEquals(StationKind.STREAM, target.kind)
        assertEquals(listOf("http://ice.example/stream"), target.urls)
    }

    @Test
    fun `nothing usable is said so rather than guessed at`() {
        assertEquals(StationKind.UNUSABLE, StationPlaylist.parse(null).kind)
        assertEquals(StationKind.UNUSABLE, StationPlaylist.parse("   ").kind)
        assertEquals(
            StationKind.UNUSABLE,
            StationPlaylist.parse("<html><body>Not found</body></html>").kind,
            "a web page names no stream",
        )
    }

    /*
     * The content type decides, and it must, because reading even the
     * first line of a live stream as text consumes audio and blocks until
     * more arrives.
     */
    @Test
    fun `audio is never read as text`() {
        assertFalse(StationPlaylist.looksLikePlaylist("audio/mpeg", "http://x/y"))
        assertFalse(StationPlaylist.looksLikePlaylist("audio/aacp", "http://x/y.pls"))
        assertFalse(
            StationPlaylist.looksLikePlaylist("application/octet-stream", "http://x/y"),
            "common for a station, unheard of for a playlist",
        )
    }

    @Test
    fun `playlist content types are read as text`() {
        for (type in listOf(
            "audio/x-mpegurl",
            "audio/mpegurl; charset=utf-8",
            "application/vnd.apple.mpegurl",
            "audio/x-scpls",
            "application/pls+xml",
            "text/plain",
        )) {
            assertTrue(StationPlaylist.looksLikePlaylist(type, "http://x/y"), type)
        }
    }

    /* An extension is a hint, so it only counts when the type says nothing. */
    @Test
    fun `an extension decides only when the type is missing`() {
        assertTrue(StationPlaylist.looksLikePlaylist(null, "http://x/listen.pls"))
        assertTrue(StationPlaylist.looksLikePlaylist(null, "http://x/listen.m3u?id=4"))
        assertFalse(StationPlaylist.looksLikePlaylist(null, "http://x/stream"))
    }

    @Test
    fun `an id survives a rename and never collides`() {
        assertEquals("bbc-radio-6", Station.idFor("BBC Radio 6"))
        assertEquals("sveriges-radio-p2", Station.idFor("Sveriges Radio  P2"))
        assertEquals("station", Station.idFor("!!!"), "something unpronounceable still needs an id")
        assertEquals("jazz-2", Station.idFor("Jazz", taken = setOf("jazz")))
        assertEquals("jazz-3", Station.idFor("Jazz", taken = setOf("jazz", "jazz-2")))
    }
}

/**
 * The saved list, which has to survive an app the system reclaims.
 *
 * JSON rather than a delimiter, because a station has two fields somebody
 * typed and one of them is a URL — and a URL can contain very nearly
 * anything, including whichever character seemed safe as a separator.
 */
class StationStorageTest {

    private val stations = listOf(
        Station("p2", "Sveriges Radio P2", "https://http-live.sr.se/p2-mp3-192"),
        Station("jazz", "Jazz | Groove", "http://ice.example/listen.pls?id=1&x=2"),
    )

    @Test
    fun `a list survives the round trip intact`() {
        assertEquals(stations, Station.decode(Station.encode(stations)))
    }

    @Test
    fun `awkward characters in a name and a url survive`() {
        val awkward = listOf(Station("odd", "A \"quoted\", comma'd\nname", "http://x/y?a=1&b=2#z"))
        assertEquals(awkward, Station.decode(Station.encode(awkward)))
    }

    /*
     * Nothing saved and something unreadable are both "no stations", and
     * neither is worth taking the app down for.
     */
    @Test
    fun `an unreadable list reads as empty rather than throwing`() {
        assertEquals(emptyList(), Station.decode(null))
        assertEquals(emptyList(), Station.decode(""))
        assertEquals(emptyList(), Station.decode("not json at all"))
        assertEquals(emptyList(), Station.decode("""{"id":"x"}"""))
    }

    @Test
    fun `an empty list is written and read back as one`() {
        assertEquals(emptyList(), Station.decode(Station.encode(emptyList())))
    }
}
