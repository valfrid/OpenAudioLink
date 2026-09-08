package org.openaudiolink.core

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.locks.LockSupport
import kotlin.random.Random

/**
 * The producer: 200 packets a second to every selected speaker, while
 * there is something to play.
 *
 * The qualifier is [SilenceGate]'s and it matters: a published Spotify
 * cast point that nobody has selected produces no audio, and this sends
 * nothing until it does. Starting the sender is therefore free — it can be
 * running before Spotify has ever connected, which is what lets *publish*
 * and *play* be two separate steps a person can watch happen.
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
    /**
     * Run on the sending thread before it sends anything.
     *
     * `Thread.MAX_PRIORITY` is set below and on Android it is very nearly
     * a no-op: the Java priorities are squeezed into a narrow band of nice
     * values, and the one that actually matters is
     * `Process.setThreadPriority(THREAD_PRIORITY_URGENT_AUDIO)`, which
     * sets the Linux nice value the scheduler reads. That call lives in
     * `android.os` and this module has no Android in it, so the app passes
     * it in.
     */
    private val onSendingThread: () -> Unit = {},
) {
    private val destinations = CopyOnWriteArrayList<InetAddress>()
    private val clock = SendClock()
    private val gate = SilenceGate()

    @Volatile private var running = false
    private var thread: Thread? = null

    @Volatile var packetsSent: Long = 0; private set
    @Volatile var sendErrors: Long = 0; private set

    /**
     * Packets the gate held because nothing was playing.
     *
     * Worth counting rather than merely not sending: a cast point that has
     * been published for ten minutes and never played has a held count in
     * the hundreds of thousands and a sent count of zero, and those two
     * numbers together say "running, waiting" — which is a different thing
     * from "running, broken" and looks identical without them.
     */
    @Volatile var packetsHeld: Long = 0; private set

    /**
     * Whether audio is actually going out right now.
     *
     * The distinction the app puts on screen: publishing a cast point is
     * not the same as playing through it.
     */
    @Volatile var sendingAudio: Boolean = false; private set

    /** How often the phone was frozen long enough to give up catching up. */
    @Volatile var resyncs: Long = 0; private set

    /**
     * This end's own view of how evenly it sent — see [SendGaps].
     *
     * Read beside the node's `arrivalGaps`. Two speakers reporting the
     * same gap count to within a fifth of a percent said the cause was
     * upstream of both of them; only the producer can say whether upstream
     * means this app or the air.
     */
    val sendGaps = SendGaps()

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
        thread = Thread({
            onSendingThread()
            run()
        }, "oal-rtp-sender").apply {
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
                        // The timeline restarted; the interval before it
                        // describes a freeze already counted as a resync.
                        sendGaps.resume()
                        /*
                         * Everything held is now older than the gap that
                         * caused this. Playing it out before the new audio
                         * would be a minute of stale sound followed by a
                         * jump; the marker bit says "new thing" and the
                         * consumers re-seat themselves once, deliberately.
                         */
                        ring.clear()
                        stream.markDiscontinuity()
                        // The ring is empty again and the timeline has
                        // restarted, so the gate starts over with it.
                        gate.reset()
                        sendingAudio = false
                        resyncs++
                    }

                    is SendClock.Tick.Send -> {
                        repeat(tick.packets) {
                            /*
                             * Asked before the ring is read, so a held
                             * packet never counts as an underrun — see
                             * SilenceGate. A source that nobody has
                             * started has not stumbled.
                             */
                            val verdict = gate.next(ring.availableFrames)
                            sendingAudio = !gate.quiet

                            if (verdict == SilenceGate.Verdict.HOLD) {
                                // The clock still runs while nobody plays.
                                stream.skip(Rtp.FRAMES_PER_PACKET)
                                packetsHeld++
                                return@repeat
                            }

                            if (verdict == SilenceGate.Verdict.RESUME) {
                                stream.markDiscontinuity()
                                // A deliberate silence is not a late send.
                                sendGaps.resume()
                            }

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
                            sendGaps.sent()
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
