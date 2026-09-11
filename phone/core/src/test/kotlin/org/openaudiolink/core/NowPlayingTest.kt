package org.openaudiolink.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

/**
 * The two sources that can say what is playing, and the ways each lies.
 *
 * Every librespot line below was copied from a real screenshot of this app
 * rather than invented, because the format is not documented anywhere and
 * a parser written against a guess is a parser that works until it does
 * not.
 */
class NowPlayingTest {

    @Test
    fun `loading gives a title before any duration arrives`() {
        val now = SpotifyLog.update(
            null,
            "Loading <Everybody Hurts> with Spotify URI <spotify:track:0eZBeB2xFIS65jQHerispi>",
        )
        assertEquals("Everybody Hurts", now?.title)
        assertNull(now?.durationMs)
    }

    @Test
    fun `loaded gives the duration`() {
        val now = SpotifyLog.update(null, "<Everybody Hurts> (320266 ms) loaded")
        assertEquals("Everybody Hurts", now?.title)
        assertEquals(320266L, now?.durationMs)
    }

    @Test
    fun `a purely numeric title is a title`() {
        // A real one: the track called 1950. A parser that treated the
        // angle brackets as optional would find a number here instead.
        val now = SpotifyLog.update(null, "<1950> (225133 ms) loaded")
        assertEquals("1950", now?.title)
        assertEquals(225133L, now?.durationMs)
    }

    @Test
    fun `loading the track already known keeps its duration`() {
        // Otherwise every track flickers: loaded arrives with a duration,
        // and a later Loading for the same track would drop it again.
        val loaded = SpotifyLog.update(null, "<Everybody Hurts> (320266 ms) loaded")
        val again = SpotifyLog.update(
            loaded,
            "Loading <Everybody Hurts> with Spotify URI <spotify:track:0eZBeB2xFIS65jQHerispi>",
        )
        assertEquals(320266L, again?.durationMs)
    }

    @Test
    fun `a title containing angle brackets survives`() {
        val now = SpotifyLog.update(null, "<<3 (feat. Nobody)> (1000 ms) loaded")
        assertEquals("<3 (feat. Nobody)", now?.title)
        assertEquals(1000L, now?.durationMs)
    }

    @Test
    fun `an unrelated line changes nothing`() {
        val before = SpotifyLog.update(null, "<Energy> (298750 ms) loaded")
        val after = SpotifyLog.update(before, "Using StdoutSink (pipe) with format: S16")
        assertEquals(before, after)
    }

    @Test
    fun `the queue running out clears it`() {
        val before = SpotifyLog.update(null, "<Energy> (298750 ms) loaded")
        assertNull(
            SpotifyLog.update(
                before,
                "Not playing next track because there are no more tracks left in queue.",
            )
        )
    }

    @Test
    fun `going inactive clears it`() {
        val before = SpotifyLog.update(null, "<Energy> (298750 ms) loaded")
        assertNull(SpotifyLog.update(before, "device became inactive"))
    }

    @Test
    fun `a warning marker does not hide the line`() {
        // The app prefixes ERROR and WARN lines with "! " for the screen,
        // and the parser sees the same list the screen does.
        val now = SpotifyLog.update(null, "! <Energy> (298750 ms) loaded")
        assertEquals("Energy", now?.title)
    }

    @Test
    fun `a whole log folds to the last thing that played`() {
        val now = SpotifyLog.readAll(
            listOf(
                "Loading <1950> with Spotify URI <spotify:track:0CZ8IquoTX2Dkg7Ak2inwA>",
                "<1950> (225133 ms) loaded",
                "Loading <The Sound of Silence> with Spotify URI <spotify:track:0eZBeB2xFIS65jQHerispi>",
                "<The Sound of Silence> (248466 ms) loaded",
            )
        )
        assertEquals("The Sound of Silence", now?.title)
        assertEquals(248466L, now?.durationMs)
    }

    /* ---------------------------------------------------------- ICY */

    @Test
    fun `a stream title splits on the first separator`() {
        val now = IcyTitle.parse("Simon - The Sound of Silence - Live", station = "P3")
        assertEquals("Simon", now?.artist)
        assertEquals("The Sound of Silence - Live", now?.title)
        assertEquals("P3", now?.station)
    }

    @Test
    fun `a stream title with no separator is a title and not an artist`() {
        val now = IcyTitle.parse("News at nine")
        assertEquals("News at nine", now?.title)
        assertNull(now?.artist)
    }

    @Test
    fun `quotes are stripped`() {
        val now = IcyTitle.parse("'R.E.M. - Everybody Hurts'")
        assertEquals("R.E.M.", now?.artist)
        assertEquals("Everybody Hurts", now?.title)
    }

    @Test
    fun `no stream title falls back to the station name`() {
        // Common rather than broken: plenty of stations send none at all.
        val now = IcyTitle.parse(null, station = "Sveriges Radio P2")
        assertEquals("Sveriges Radio P2", now?.title)
        assertEquals("Sveriges Radio P2", now?.station)
    }

    @Test
    fun `nothing at all is nothing`() {
        assertNull(IcyTitle.parse(null, null))
        assertNull(IcyTitle.parse("   ", "  "))
    }

    @Test
    fun `a trailing separator is not a split`() {
        val now = IcyTitle.parse("Coming up - ")
        assertEquals("Coming up -", now?.title)
        assertNull(now?.artist)
    }

    @Test
    fun `the one-line form names the artist when there is one`() {
        assertEquals("R.E.M. — Everybody Hurts", NowPlaying("Everybody Hurts", "R.E.M.").line)
        assertEquals("Everybody Hurts", NowPlaying("Everybody Hurts").line)
    }
}
