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
) {
    /** Called when the visible set changed in a way worth redrawing. */
    var onChange: (() -> Unit)? = null

    @Volatile private var running = false
    private var socket: MulticastSocket? = null
    private var listener: Thread? = null
    private var announcer: Thread? = null

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
            if (chosen != null) {
                networkInterface = chosen
                joinGroup(InetSocketAddress(group, Discovery.PORT), chosen)
            } else {
                @Suppress("DEPRECATION")
                joinGroup(group)
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
            val text = String(datagram.data, 0, datagram.length, Charsets.UTF_8)

            // Somebody else's probe: answer it, unicast, as the protocol says.
            if (Discovery.isProbe(text)) {
                self?.let { reply(open, it, datagram.address, datagram.port) }
                continue
            }

            val announce = Discovery.parseAnnounce(text) ?: continue
            if (announce.id == self?.id) continue   // our own voice coming back
            if (table.heard(announce, datagram.address.hostAddress ?: continue, clock())) {
                onChange?.invoke()
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
            } catch (_: Exception) {
            }
            if (table.expire(clock()) > 0) onChange?.invoke()
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
