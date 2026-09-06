package org.openaudiolink.core

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.locks.LockSupport
import kotlin.random.Random

/**
 * The producer: 200 packets a second to every selected speaker.
 *
 * Unicast, one copy per destination, because OpenAudioLink nodes are on
 * Wi-Fi — where multicast frames go out at a low basic rate with no
 * acknowledgement and no retries, and are markedly less reliable than the
 * unicast this replaces them with.
 *
 * The socket is supplied rather than opened here. On Android it has to be
 * bound to the Wi-Fi network explicitly: a phone with mobile data up is
 * multi-homed, and a socket left to choose for itself will happily send a
 * speaker's audio to the cellular interface, where it vanishes. That is
 * the Android form of the lesson decision 15 records for the Hub.
 */
class RtpSender(
    private val ring: PcmRing,
    private val port: Int = Rtp.DEFAULT_PORT,
    private val socketProvider: () -> DatagramSocket = { DatagramSocket() },
) {
    private val destinations = CopyOnWriteArrayList<InetAddress>()
    private val clock = SendClock()

    @Volatile private var running = false
    private var thread: Thread? = null

    @Volatile var packetsSent: Long = 0; private set
    @Volatile var sendErrors: Long = 0; private set

    /** How often the phone was frozen long enough to give up catching up. */
    @Volatile var resyncs: Long = 0; private set

    val underruns: Long get() = ring.underruns
    val isRunning: Boolean get() = running

    fun setDestinations(addresses: List<InetAddress>) {
        destinations.clear()
        destinations.addAll(addresses)
    }

    fun destinationCount(): Int = destinations.size

    @Synchronized
    fun start() {
        if (running) return
        running = true
        thread = Thread({ run() }, "oal-rtp-sender").apply {
            // Audio pacing loses to almost nothing else on the device, and
            // being late here is audible in a way that being late almost
            // anywhere else in an app is not.
            priority = Thread.MAX_PRIORITY
            start()
        }
    }

    @Synchronized
    fun stop() {
        running = false
        thread?.join(500)
        thread = null
    }

    private fun run() {
        val socket = try {
            socketProvider()
        } catch (_: Exception) {
            running = false
            return
        }

        /*
         * Random per stream, as the protocol requires. The sequence starts
         * random too; the timestamp does not have to, but a random start
         * is the honest choice — a receiver must not be able to depend on
         * a stream beginning at zero, and the only way to be sure nobody
         * does is never to send one that does.
         */
        val stream = RtpStream(
            ssrc = Random.nextInt(),
            startSequence = Random.nextInt(0, 0x10000),
            startTimestamp = Random.nextInt(),
        )

        val frames = FloatArray(Rtp.FRAMES_PER_PACKET * Rtp.CHANNELS)
        val payload = ByteArray(Rtp.PAYLOAD_BYTES)
        val packet = ByteArray(Rtp.PACKET_BYTES)
        val datagram = DatagramPacket(packet, packet.size)

        clock.start(System.nanoTime())

        try {
            while (running) {
                when (val tick = clock.tick(System.nanoTime())) {
                    is SendClock.Tick.Wait -> {
                        val wait = clock.nanosUntilNext(System.nanoTime())
                        if (wait > 0) LockSupport.parkNanos(minOf(wait, 2_000_000L))
                    }

                    is SendClock.Tick.Resync -> {
                        /*
                         * Everything held is now older than the gap that
                         * caused this. Playing it out before the new audio
                         * would be a minute of stale sound followed by a
                         * jump; the marker bit says "new thing" and the
                         * consumers re-seat themselves once, deliberately.
                         */
                        ring.clear()
                        stream.markDiscontinuity()
                        resyncs++
                    }

                    is SendClock.Tick.Send -> {
                        repeat(tick.packets) {
                            ring.readOrSilence(frames, Rtp.FRAMES_PER_PACKET)
                            L24.fromFloat(frames, frames.size, payload)
                            val length = stream.write(packet, payload, Rtp.FRAMES_PER_PACKET)
                            datagram.setData(packet, 0, length)
                            for (destination in destinations) {
                                datagram.address = destination
                                datagram.port = port
                                try {
                                    socket.send(datagram)
                                } catch (_: Exception) {
                                    // One speaker off the air must not stop
                                    // the others: a party is exactly where
                                    // a device leaves and comes back.
                                    sendErrors++
                                }
                            }
                            packetsSent++
                        }
                        clock.sent(tick.packets)
                    }
                }
            }
        } finally {
            socket.close()
        }
    }
}
