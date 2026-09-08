package org.openaudiolink.core

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * Finding the speakers, per `protocol/DISCOVERY.md`.
 *
 * UDP multicast to 239.255.41.10:41000, UTF-8 JSON, one message per
 * datagram. Devices announce every 5 seconds; a controller may probe for
 * an immediate reply.
 */
object Discovery {
    const val GROUP = "239.255.41.10"
    const val PORT = 41000
    const val SUITE = "0.1"

    /** A device is offline after this long without an announce. */
    const val LIVENESS_MS = 30_000L

    /** The default control port, when an announce omits `ctrlPort`. */
    const val DEFAULT_CONTROL_PORT = 41001

    /**
     * Where the *device* control API lives — the endpoints in
     * `protocol/CONTROL.md` that this app knows how to speak.
     *
     * It doubles as the only reliable way to tell a node from a Hub. The
     * Hub announces `producer` like a turntable does, and announces its own
     * port (41080) because it serves a different REST API; nothing in an
     * announce says "I speak the device API" outright. The port does, by
     * construction: that is the door those endpoints are behind. The Hub's
     * own code says as much where it sets the field — without it "a node
     * assumes the device control port and knocks on a door the Hub does
     * not have".
     */
    const val DEVICE_CONTROL_PORT = 41001

    /**
     * Lenient on the way in, exact on the way out.
     *
     * `ignoreUnknownKeys` is not laziness: the protocol says a receiver
     * must keep the roles it recognises and ignore the rest, so that a
     * node running newer firmware is not treated as less capable than it
     * claims. A parser that refuses a message for carrying a field it has
     * not heard of would make every future addition a breaking change.
     */
    val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
        explicitNulls = false
    }

    fun encode(message: Announce): String = json.encodeToString(Announce.serializer(), message)

    fun encodeProbe(): String = """{"oal":"$SUITE","type":"probe"}"""

    /** Returns null for anything that is not an announce we understand. */
    fun parseAnnounce(text: String): Announce? = try {
        val message = json.decodeFromString(Announce.serializer(), text)
        if (message.type == "announce" && message.id.isNotEmpty()) message else null
    } catch (_: Exception) {
        // A malformed datagram is a fact of life on a shared multicast
        // group, not an error worth propagating: drop it and keep listening.
        null
    }

    fun isProbe(text: String): Boolean = try {
        json.decodeFromString(Probe.serializer(), text).type == "probe"
    } catch (_: Exception) {
        false
    }
}

@Serializable
data class Probe(
    val oal: String = Discovery.SUITE,
    val type: String = "probe",
)

@Serializable
data class Announce(
    val oal: String = Discovery.SUITE,
    val type: String = "announce",
    val id: String,
    val name: String,
    val roles: List<String>,
    val hw: String,
    val fw: String,
    val caps: List<String> = emptyList(),
    @SerialName("ctrlPort") val ctrlPort: Int? = null,
) {
    val isConsumer: Boolean get() = roles.contains("consumer")
    val isProducer: Boolean get() = roles.contains("producer")
    val isController: Boolean get() = roles.contains("controller")
    val controlPort: Int get() = ctrlPort ?: Discovery.DEFAULT_CONTROL_PORT

    /** Whether this app can drive it: see `Discovery.DEVICE_CONTROL_PORT`. */
    val speaksDeviceControl: Boolean get() = controlPort == Discovery.DEVICE_CONTROL_PORT

    /**
     * Somewhere this phone can send audio.
     *
     * Roles are not enough on their own. A Hub announces `producer`
     * exactly as a turntable does, so a list built from roles alone offers
     * to play music at a Windows PC — which is what the first build on
     * real hardware did.
     */
    val canReceiveAudio: Boolean get() = isConsumer && speaksDeviceControl

    /** Something this phone can *start*, as its Controller rather than its source. */
    val canBeToldToPlay: Boolean get() = isProducer && speaksDeviceControl
}

/** A device as this app currently understands it. */
data class Peer(
    val announce: Announce,
    val address: String,
    val lastSeenMs: Long,
) {
    val id: String get() = announce.id
    val name: String get() = announce.name
    val controlPort: Int get() = announce.controlPort
}

/**
 * Who is out there, and whether they still are.
 *
 * Pure, and time is a parameter: liveness is the kind of rule that is
 * tedious to test by waiting and trivial to test by passing a number.
 */
class PeerTable(private val livenessMs: Long = Discovery.LIVENESS_MS) {
    private val peers = LinkedHashMap<String, Peer>()

    /**
     * @return true when this changed something a UI would want to redraw —
     * a new device, a rename, a new address. A device merely repeating
     * itself every five seconds is not news.
     */
    @Synchronized
    fun heard(announce: Announce, address: String, nowMs: Long): Boolean {
        val existing = peers[announce.id]
        peers[announce.id] = Peer(announce, address, nowMs)
        return existing == null ||
            existing.address != address ||
            existing.announce != announce
    }

    /** Everything heard from within the liveness window, in arrival order. */
    @Synchronized
    fun online(nowMs: Long): List<Peer> =
        peers.values.filter { nowMs - it.lastSeenMs <= livenessMs }

    /** Everything this phone can send audio to. Not everything with a role. */
    @Synchronized
    fun destinations(nowMs: Long): List<Peer> =
        online(nowMs).filter { it.announce.canReceiveAudio }

    /** Nodes this phone can tell to start their own stream — a turntable. */
    @Synchronized
    fun sources(nowMs: Long): List<Peer> =
        online(nowMs).filter { it.announce.canBeToldToPlay }

    /** Drops what has been silent for long enough, returning how many went. */
    @Synchronized
    fun expire(nowMs: Long): Int {
        val before = peers.size
        peers.entries.removeIf { nowMs - it.value.lastSeenMs > livenessMs }
        return before - peers.size
    }

    @Synchronized
    fun clear() = peers.clear()
}
