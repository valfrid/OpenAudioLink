package org.openaudiolink.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Discovery and control, against `protocol/DISCOVERY.md` and
 * `protocol/CONTROL.md`.
 *
 * A malformed request body is a 400 nobody sees on a phone, and it looks
 * exactly like a network problem. These read the bytes instead.
 */
class DiscoveryTest {

    private val announce = """
        {"oal":"0.1","type":"announce","id":"mac-a0b1c2d3e4f5","name":"Kitchen",
         "roles":["consumer"],"hw":"esp32s3-pcm5102a","fw":"0.54.0",
         "caps":["control-v0"],"ctrlPort":41001}
    """.trimIndent()

    @Test
    fun `the group and port are the ones the protocol names`() {
        assertEquals("239.255.41.10", Discovery.GROUP)
        assertEquals(41000, Discovery.PORT)
        assertEquals(41001, Discovery.DEFAULT_CONTROL_PORT)
        assertEquals(30_000L, Discovery.LIVENESS_MS)
    }

    @Test
    fun `an announce parses into what a controller needs`() {
        val message = assertNotNull(Discovery.parseAnnounce(announce))
        assertEquals("mac-a0b1c2d3e4f5", message.id)
        assertEquals("Kitchen", message.name)
        assertTrue(message.isConsumer)
        assertTrue(!message.isProducer)
        assertEquals(41001, message.controlPort)
    }

    /*
     * The protocol says a receiver must keep the roles it recognises and
     * ignore the rest, so a node running newer firmware is not treated as
     * less capable than it claims. A parser that refused an unknown field
     * would make every future addition a breaking change.
     */
    @Test
    fun `a field we have never heard of does not reject the message`() {
        val future = """{"oal":"0.1","type":"announce","id":"x","name":"n",
            "roles":["consumer","teleporter"],"hw":"h","fw":"f","quantumMode":true}"""
        val message = assertNotNull(Discovery.parseAnnounce(future))
        assertTrue(message.isConsumer)
        assertEquals(41001, message.controlPort, "an absent ctrlPort means the default")
    }

    @Test
    fun `rubbish on a shared multicast group is dropped, not thrown`() {
        assertNull(Discovery.parseAnnounce("not json at all"))
        assertNull(Discovery.parseAnnounce(""))
        assertNull(Discovery.parseAnnounce("""{"oal":"0.1","type":"probe"}"""))
        assertNull(Discovery.parseAnnounce("""{"oal":"0.1","type":"announce"}"""))
    }

    @Test
    fun `a probe is recognised and is what the protocol specifies`() {
        assertEquals("""{"oal":"0.1","type":"probe"}""", Discovery.encodeProbe())
        assertTrue(Discovery.isProbe(Discovery.encodeProbe()))
        assertTrue(!Discovery.isProbe(announce))
    }

    @Test
    fun `the phone announces itself like every other device`() {
        val self = Announce(
            id = "phone-abc", name = "Pixel", roles = listOf("producer"),
            hw = "android", fw = "0.1.0",
        )
        val text = Discovery.encode(self)
        val back = assertNotNull(Discovery.parseAnnounce(text))
        assertEquals(self.id, back.id)
        assertTrue(back.isProducer)
        assertTrue(text.contains(""""oal":"0.1""""), "the suite version is required")
    }
}

class PeerTableTest {

    private fun announce(id: String, name: String = id, role: String = "consumer") =
        Announce(id = id, name = name, roles = listOf(role), hw = "h", fw = "f")

    @Test
    fun `a new device is news and a repeat is not`() {
        val table = PeerTable()
        assertTrue(table.heard(announce("a"), "192.168.0.71", 1_000))
        assertTrue(!table.heard(announce("a"), "192.168.0.71", 6_000),
            "announcing every five seconds is not a change")
    }

    @Test
    fun `a rename or a new address is news`() {
        val table = PeerTable()
        table.heard(announce("a", "Kitchen"), "192.168.0.71", 0)
        assertTrue(table.heard(announce("a", "Garden"), "192.168.0.71", 1))
        assertTrue(table.heard(announce("a", "Garden"), "192.168.0.99", 2),
            "a speaker that came back on a new address")
    }

    @Test
    fun `silence past the window takes a device offline`() {
        val table = PeerTable()
        table.heard(announce("a"), "192.168.0.71", 0)
        assertEquals(1, table.online(29_000).size)
        assertEquals(0, table.online(31_000).size)
    }

    @Test
    fun `expiry removes what the window dropped`() {
        val table = PeerTable()
        table.heard(announce("a"), "192.168.0.71", 0)
        table.heard(announce("b"), "192.168.0.72", 20_000)
        assertEquals(1, table.expire(35_000))
        assertEquals(1, table.online(35_000).size)
    }

    @Test
    fun `only consumers are offered as destinations`() {
        val table = PeerTable()
        table.heard(announce("a", role = "consumer"), "192.168.0.71", 0)
        table.heard(announce("b", role = "producer"), "192.168.0.72", 0)
        assertEquals(listOf("a"), table.destinations(1_000).map { it.id })
    }

    /* An analog source that also plays is both, and must appear as both. */
    @Test
    fun `a device holding several roles keeps all of them`() {
        val table = PeerTable()
        val both = Announce(id = "v", name = "Turntable",
            roles = listOf("producer", "consumer"), hw = "h", fw = "f")
        table.heard(both, "192.168.0.80", 0)
        assertEquals(1, table.destinations(0).size)
        assertEquals(1, table.sources(0).size)
    }

    /*
     * The Hub is not a speaker, and roles alone will not say so.
     *
     * It announces ["controller","producer"] — the same producer role a
     * turntable announces — so the first build on real hardware put a
     * Windows PC in the speaker list with a tick box offering to play
     * music at it. What separates them is the port: the device control API
     * lives on 41001 and the Hub serves a different REST API on 41080.
     */
    @Test
    fun `a hub is neither a destination nor a source this app can drive`() {
        val hub = Announce(
            id = "hub-1", name = "OpenAudioLink Hub",
            roles = listOf("controller", "producer"),
            hw = "windows-hub", fw = "0.104.0", ctrlPort = 41080,
        )
        val table = PeerTable()
        table.heard(hub, "192.168.0.201", 0)

        assertTrue(!hub.speaksDeviceControl)
        assertEquals(emptyList(), table.destinations(0).map { it.id })
        assertEquals(emptyList(), table.sources(0).map { it.id })
        assertEquals(1, table.online(0).size, "but it is still on the network, and visible")
    }

    /* A turntable node announces the same producer role and *is* drivable. */
    @Test
    fun `a producer node on the device port can be told to play`() {
        val vinyl = Announce(
            id = "v", name = "Vinylspelare", roles = listOf("producer"),
            hw = "esp32s3-pcm1808", fw = "0.54.0", ctrlPort = 41001,
        )
        val table = PeerTable()
        table.heard(vinyl, "192.168.0.237", 0)

        assertEquals(listOf("v"), table.sources(0).map { it.id })
        assertEquals(emptyList(), table.destinations(0).map { it.id },
            "a producer that does not also play is not a speaker")
    }

    /* An announce with no ctrlPort at all is a node, by the default. */
    @Test
    fun `an announce without a control port is taken as a node`() {
        val terse = Announce(id = "n", name = "Kitchen",
            roles = listOf("consumer"), hw = "h", fw = "f")
        assertTrue(terse.speaksDeviceControl)
        assertTrue(terse.canReceiveAudio)
    }
}

class RequestsTest {

    @Test
    fun `volume is a percentage and refuses to amplify`() {
        assertEquals("""{"percent":40}""", Requests.volume(40))
        assertEquals("""{"percent":0}""", Requests.volume(0))
        assertFailsWith<IllegalArgumentException> { Requests.volume(101) }
        assertFailsWith<IllegalArgumentException> { Requests.volume(-1) }
    }

    @Test
    fun `room correction is a config key, per node`() {
        assertEquals("""{"eqEnabled":true}""", Requests.roomCorrection(true))
        assertEquals("""{"eqEnabled":false}""", Requests.roomCorrection(false))
    }

    @Test
    fun `a stream names its destinations, its port and its source`() {
        assertEquals(
            """{"destinations":["192.168.0.71","192.168.0.72"],"port":41100,"source":"capture"}""",
            Requests.streamStart(listOf("192.168.0.71", "192.168.0.72"), "capture"),
        )
        assertFailsWith<IllegalArgumentException> { Requests.streamStart(emptyList(), "capture") }
    }

    /*
     * Removals first, so moving a speaker between rooms is not refused for
     * filling the set with an entry that is on its way out.
     */
    @Test
    fun `a destination change puts removals before additions`() {
        assertEquals(
            """{"remove":["192.168.0.99"],"add":["192.168.0.71"]}""",
            Requests.destinations(add = listOf("192.168.0.71"), remove = listOf("192.168.0.99")),
        )
        assertEquals("""{"add":["192.168.0.71"]}""",
            Requests.destinations(add = listOf("192.168.0.71")))
        assertEquals("{}", Requests.destinations(emptyList(), emptyList()))
    }
}

class StatusTest {

    /*
     * Firmware older than 0.11.0 has no volume field at all, and a
     * controller must read that as "this node cannot" rather than as
     * "this node is silent" — one of those puts a slider at zero.
     */
    @Test
    fun `a node without a volume field is not a node at zero`() {
        val old = Discovery.json.decodeFromString(
            NodeClient.Status.serializer(),
            """{"id":"x","name":"Old","roles":["consumer"],"fw":"0.10.0"}""",
        )
        assertTrue(!old.hasVolume)

        val current = Discovery.json.decodeFromString(
            NodeClient.Status.serializer(),
            """{"id":"x","name":"Kitchen","roles":["consumer"],"fw":"0.54.0",
                "volume":40,"eqEnabled":true}""",
        )
        assertTrue(current.hasVolume)
        assertEquals(40, current.volume)
        assertTrue(current.eqEnabled)
    }

    /* A node's /status carries thirty-odd fields this app has no use for. */
    @Test
    fun `the fields this app ignores do not break it`() {
        val full = """{"oal":"0.1","id":"x","name":"Kitchen","roles":["consumer"],
            "channel":"stereo","volume":40,"hw":"h","fw":"0.54.0","uptimeS":1234,
            "heapFree":206936,"wifi":{"rssi":-53},"partyReady":true,"delayMs":0,
            "eqEnabled":false,"eqPreampDb":-3.5,"ringMs":400,"httpdStackFreeB":2456}"""
        val status = Discovery.json.decodeFromString(NodeClient.Status.serializer(), full)
        assertEquals("Kitchen", status.name)
        assertEquals(40, status.volume)
    }
}
