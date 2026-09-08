package org.openaudiolink.core

import java.net.DatagramPacket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.MulticastSocket
import java.net.NetworkInterface

/**
 * Listens for speakers, and says the phone is here.
 *
 * The phone announces itself like every other device — the protocol has
 * the Hub doing it too — so a node's own page and the Hub's switchboard
 * show the phone as a producer rather than as an unexplained source of
 * packets.
 *
 * On Android this needs a `WifiManager.MulticastLock` held for as long as
 * the socket is open. Without one the radio filters multicast frames
 * before they reach the socket and discovery finds nothing at all, with no
 * error to say why. The lock is acquired by the caller, because it is the
 * one part of this that is not portable.
 */
class DiscoveryClient(
    private val self: Announce?,
    private val networkInterface: NetworkInterface? = null,
    private val table: PeerTable = PeerTable(),
    private val clock: () -> Long = { System.currentTimeMillis() },
    /**
     * Pins the socket to one network, where the platform can do that.
     *
     * On a phone this is the difference between joining the group on the
     * Wi-Fi and joining it on whatever interface the system happens to
     * prefer — which, with mobile data up, is the one no speaker is on.
     * Free of Android here; the caller supplies the binding.
     */
    private val bindSocket: ((MulticastSocket) -> Unit)? = null,
) {
    /** Called when the visible set changed in a way worth redrawing. */
    var onChange: (() -> Unit)? = null

    @Volatile private var running = false
    private var socket: MulticastSocket? = null
    private var listener: Thread? = null
    private var announcer: Thread? = null

    /*
     * Counters, because "not working" and "working, nothing out there" look
     * identical from a screen with an empty list on it. These make the
     * difference visible: datagrams arriving at all says the socket and the
     * multicast lock are right, and only the peer table is empty.
     */

    /** Every datagram that reached the socket, ours and other people's. */
    @Volatile var datagramsHeard: Long = 0; private set

    /** Announces this device has sent. */
    @Volatile var announcesSent: Long = 0; private set

    /** Probes sent, including every press of "Look again". */
    @Volatile var probesSent: Long = 0; private set

    /** The interface the group was actually joined on, for the record. */
    @Volatile var joinedOn: String? = null; private set

    /** The last thing that went wrong, if anything has. */
    @Volatile var lastError: String? = null; private set

    val peers: PeerTable get() = table

    @Synchronized
    fun start() {
        if (running) return
        running = true

        val group = InetAddress.getByName(Discovery.GROUP)

        /*
         * Held in a local, and it matters.
         *
         * `MulticastSocket` has a `networkInterface` property of its own,
         * so inside the `apply` below the bare name resolves to the
         * socket's, not to this class's constructor parameter — the test
         * would ask the socket what it was already bound to and the
         * assignment would be a no-op against itself. Naming it separately
         * is the only way the two cannot be confused.
         */
        val chosen = networkInterface

        val opened = MulticastSocket(Discovery.PORT).apply {
            reuseAddress = true
            // TTL 1: OpenAudioLink is link-local by design.
            timeToLive = 1
            bindSocket?.invoke(this)

            /*
             * Join on the named interface, and fall back rather than fail.
             *
             * An earlier version resolved this name to the *socket's* own
             * property by accident and joined on whatever that returned;
             * naming the Wi-Fi interface explicitly is correct, but only
             * while the name is right. If the join is refused — a name that
             * no longer exists, an interface that is down — the group is
             * still worth joining the old way, because a group joined on
             * the system's choice finds speakers more often than a group
             * never joined at all.
             */
            joinedOn = try {
                if (chosen == null) throw IllegalStateException("no interface named")
                networkInterface = chosen
                joinGroup(InetSocketAddress(group, Discovery.PORT), chosen)
                chosen.name
            } catch (e: Exception) {
                @Suppress("DEPRECATION")
                joinGroup(group)
                "system default (${e.message})"
            }
            soTimeout = 1_000
        }
        socket = opened

        listener = Thread({ listen(opened, group) }, "oal-discovery-rx").apply { start() }
        announcer = Thread({ announce(opened, group) }, "oal-discovery-tx").apply { start() }
    }

    @Synchronized
    fun stop() {
        running = false
        socket?.close()
        socket = null
        listener?.join(500)
        announcer?.join(500)
        listener = null
        announcer = null
    }

    /** Asks everyone to announce now, rather than waiting five seconds. */
    fun probe() {
        val open = socket ?: return
        val payload = Discovery.encodeProbe().toByteArray(Charsets.UTF_8)
        probesSent++
        try {
            open.send(DatagramPacket(payload, payload.size,
                InetAddress.getByName(Discovery.GROUP), Discovery.PORT))
        } catch (_: Exception) {
            // Multicast is unacknowledged; a lost probe costs one announce
            // interval and nothing else.
        }
        /*
         * And by unicast to everyone already known. The protocol says to,
         * and the reason is worth restating: multicast frames are never
         * retransmitted over Wi-Fi, so announces alone make a perfectly
         * healthy speaker appear to flap between online and offline. A
         * unicast probe and the unicast announce it draws both get
         * link-layer retries.
         */
        for (peer in table.online(clock())) {
            try {
                open.send(DatagramPacket(payload, payload.size,
                    InetAddress.getByName(peer.address), Discovery.PORT))
            } catch (_: Exception) {
            }
        }
    }

    private fun listen(open: MulticastSocket, group: InetAddress) {
        val buffer = ByteArray(1500)
        while (running) {
            val datagram = DatagramPacket(buffer, buffer.size)
            try {
                open.receive(datagram)
            } catch (_: Exception) {
                continue   // the 1 s timeout, or a close on the way out
            }
            datagramsHeard++

            /*
             * Nothing in here may escape this thread.
             *
             * `onChange` is the caller's code, and on Android an uncaught
             * exception on *any* thread kills the whole process — so a
             * callback that threw once would take the app down from a
             * background thread, with the last frame left on screen and
             * every tap going nowhere. That is indistinguishable from a
             * freeze, which is the worst thing for it to look like.
             */
            try {
                val text = String(datagram.data, 0, datagram.length, Charsets.UTF_8)

                // Somebody else's probe: answer it, unicast, as the protocol says.
                if (Discovery.isProbe(text)) {
                    self?.let { reply(open, it, datagram.address, datagram.port) }
                    continue
                }

                val announce = Discovery.parseAnnounce(text) ?: continue
                if (announce.id == self?.id) continue   // our own voice coming back
                val from = datagram.address.hostAddress ?: continue
                if (table.heard(announce, from, clock())) {
                    onChange?.invoke()
                }
            } catch (e: Exception) {
                lastError = "handling a datagram: ${e.message}"
            }
        }
        try {
            open.leaveGroup(group)
        } catch (_: Exception) {
        }
    }

    private fun announce(open: MulticastSocket, group: InetAddress) {
        val message = self ?: return
        val payload = Discovery.encode(message).toByteArray(Charsets.UTF_8)
        while (running) {
            try {
                open.send(DatagramPacket(payload, payload.size, group, Discovery.PORT))
                announcesSent++
            } catch (e: Exception) {
                lastError = e.message
            }
            try {
                if (table.expire(clock()) > 0) onChange?.invoke()
            } catch (e: Exception) {
                lastError = "expiring peers: ${e.message}"
            }
            // Every 5 seconds, as the protocol specifies.
            for (i in 0 until 50) {
                if (!running) return
                Thread.sleep(100)
            }
        }
    }

    private fun reply(open: MulticastSocket, message: Announce,
                      to: InetAddress, port: Int) {
        val payload = Discovery.encode(message).toByteArray(Charsets.UTF_8)
        try {
            // The protocol asks for a random 0-500 ms delay so a group of
            // devices does not answer in one burst.
            Thread.sleep((0..500).random().toLong())
            open.send(DatagramPacket(payload, payload.size, to, port))
        } catch (_: Exception) {
        }
    }
}
