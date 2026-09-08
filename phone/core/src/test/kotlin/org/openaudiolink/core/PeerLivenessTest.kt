package org.openaudiolink.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Staying in touch, which turned out to be the hard part.
 *
 * Two speakers and a turntable worked on real hardware, and then kept
 * dropping off the list — reappearing on "Look again", unticked and
 * silent. Multicast on Wi-Fi goes out at a low basic rate, unacknowledged,
 * and a phone's power save batches what does arrive; six lost announces in
 * a row is the entire thirty-second liveness window.
 *
 * The list-forgetting half of that is the app's, and it is fixed by
 * remembering a choice as a fact about a device rather than as a property
 * of whichever devices happen to be visible. This is the other half: a
 * unicast reply is better evidence than a multicast announce, and it now
 * counts as one.
 */
class PeerLivenessTest {

    private fun announce(id: String) = Announce(
        oal = Discovery.SUITE,
        id = id,
        name = "Kitchen",
        roles = listOf("consumer"),
        hw = "xiao-esp32s3",
        fw = "0.11.0",
        caps = emptyList(),
        ctrlPort = Discovery.DEVICE_CONTROL_PORT,
    )

    @Test
    fun `an answer keeps a speaker alive through lost announces`() {
        val table = PeerTable()
        table.heard(announce("a"), "192.168.0.50", 0)

        // Twenty-nine seconds of silence: still just inside the window.
        assertEquals(1, table.online(29_000).size)

        // It answers an HTTP request at twenty-nine seconds. That is
        // better evidence than an announce, and it resets the clock.
        assertTrue(table.answered("a", 29_000))

        // Thirty-one seconds after the last *announce*, and still present.
        assertEquals(1, table.online(31_000).size, "a device that answers has not gone")
        assertEquals(0, table.expire(31_000))
    }

    @Test
    fun `without an answer it still expires on time`() {
        val table = PeerTable()
        table.heard(announce("a"), "192.168.0.50", 0)
        assertEquals(0, table.online(31_000).size)
        assertEquals(1, table.expire(31_000))
    }

    /*
     * A reply cannot conjure a device. Discovery is how something joins
     * the list; this only refreshes what is already on it.
     */
    @Test
    fun `answering for an unknown device does nothing`() {
        val table = PeerTable()
        assertFalse(table.answered("never-seen", 1_000))
        assertEquals(0, table.online(1_000).size)
    }

    @Test
    fun `silence is measured from the last thing heard, of either kind`() {
        val table = PeerTable()
        assertNull(table.silentFor("a", 0), "nothing is known about a stranger")

        table.heard(announce("a"), "192.168.0.50", 1_000)
        assertEquals(4_000, table.silentFor("a", 5_000))

        table.answered("a", 6_000)
        assertEquals(1_000, table.silentFor("a", 7_000))
    }

    /*
     * The threshold the app probes at: well inside the liveness window, so
     * the question is asked while there is still time for the answer to
     * keep the device on the list.
     */
    @Test
    fun `a speaker is worth asking about long before it expires`() {
        val table = PeerTable()
        table.heard(announce("a"), "192.168.0.50", 0)
        assertTrue(table.silentFor("a", 12_000)!! > 10_000, "asked at twelve seconds")
        assertEquals(1, table.online(12_000).size, "and still listed while we ask")
    }

    @Test
    fun `an answer does not change the address or the announce`() {
        val table = PeerTable()
        table.heard(announce("a"), "192.168.0.50", 0)
        table.answered("a", 1_000)
        val peer = table.online(1_000).single()
        assertEquals("192.168.0.50", peer.address)
        assertEquals("Kitchen", peer.name)
    }
}
