package org.openaudiolink.phone

import android.content.Context
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import org.openaudiolink.core.Announce
import org.openaudiolink.core.Discovery
import org.openaudiolink.core.DiscoveryClient
import org.openaudiolink.core.NodeClient
import org.openaudiolink.core.PcmRing
import org.openaudiolink.core.Rtp
import org.openaudiolink.core.RtpSender
import org.openaudiolink.phone.sources.AudioSource
import java.net.InetAddress

/**
 * What the app knows and what it can be told to do.
 *
 * One object rather than a graph of them, because this app is small on
 * purpose: decision 19 gives the phone a Producer and just enough
 * Controller to get a stream running, and the moment this needs a
 * dependency framework it has stopped being that.
 *
 * The service owns the lifecycle; the UI only reads [state] and calls the
 * commands below.
 */
object Producer {

    /** A speaker as the app currently understands it. */
    data class Speaker(
        val id: String,
        val name: String,
        val address: String,
        val controlPort: Int,
        val selected: Boolean = false,
        val volume: Int = -1,
        val roomCorrection: Boolean = false,
        val isProducerNode: Boolean = false,
    ) {
        val hasVolume: Boolean get() = volume >= 0
    }

    data class State(
        val speakers: List<Speaker> = emptyList(),
        val streaming: Boolean = false,
        val sourceLabel: String? = null,
        val packetsSent: Long = 0,
        val underruns: Long = 0,
        val resyncs: Long = 0,
        /** Something a person needs to be told, in their own words. */
        val warning: String? = null,
    ) {
        val selected: List<Speaker> get() = speakers.filter { it.selected }
    }

    private val _state = MutableStateFlow(State())
    val state: StateFlow<State> = _state.asStateFlow()

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val ring = PcmRing(capacityFrames = Rtp.SAMPLE_RATE)   // one second
    private var sender: RtpSender? = null
    private var discovery: DiscoveryClient? = null
    private var binding: WifiBinding? = null
    private var source: AudioSource? = null

    /* ---------- lifecycle, called by the service ---------- */

    fun attach(context: Context, identity: Announce) {
        val wifi = WifiBinding(context).also { binding = it }
        wifi.acquireLocks()

        if (wifi.wifiNetwork() == null) {
            warn("No Wi-Fi. The speakers are on the Wi-Fi, so nothing can reach them yet.")
        }

        val client = DiscoveryClient(
            self = identity,
            networkInterface = wifi.multicastInterface(),
        )
        client.onChange = { refreshSpeakers() }
        client.start()
        client.probe()
        discovery = client

        refreshSpeakers()
    }

    fun detach() {
        stopStream()
        discovery?.stop()
        discovery = null
        binding?.releaseLocks()
        binding = null
    }

    /* ---------- the stream ---------- */

    fun startStream(newSource: AudioSource) {
        val wifi = binding ?: return
        if (wifi.wifiNetwork() == null) {
            warn("No Wi-Fi to send on. Join the speakers' network, or turn on the hotspot.")
            return
        }

        stopStream()
        ring.clear()

        val rtp = RtpSender(ring, socketProvider = {
            wifi.boundSocket() ?: error("the Wi-Fi went away between the check and the socket")
        })
        rtp.setDestinations(currentDestinations())
        rtp.start()
        sender = rtp

        newSource.start(ring)
        source = newSource

        _state.update { it.copy(streaming = true, sourceLabel = newSource.label, warning = null) }
        pollCounters()
    }

    fun stopStream() {
        source?.stop()
        source = null
        sender?.stop()
        sender = null
        _state.update { it.copy(streaming = false, sourceLabel = null) }
    }

    /* ---------- the four controls ---------- */

    /**
     * Adding or removing a speaker mid-song.
     *
     * On the phone's own stream this is a change to the destination list
     * and nothing else — no teardown, no restart, and the speakers already
     * playing never know. A late joiner starts wherever the stream has
     * reached and its probation handles arriving mid-stream.
     */
    fun toggleSpeaker(id: String) {
        _state.update { current ->
            current.copy(speakers = current.speakers.map {
                if (it.id == id) it.copy(selected = !it.selected) else it
            })
        }
        sender?.setDestinations(currentDestinations())
    }

    /**
     * Volume is per node, not per source.
     *
     * A Consumer property, and it stays one — decision 14. An app that
     * wants Spotify quieter than the record player remembers a set of node
     * volumes per source and applies it on selection; that lives here, and
     * costs the firmware nothing.
     */
    fun setVolume(id: String, percent: Int) {
        val speaker = _state.value.speakers.firstOrNull { it.id == id } ?: return
        _state.update { current ->
            current.copy(speakers = current.speakers.map {
                if (it.id == id) it.copy(volume = percent) else it
            })
        }
        scope.launch { client(speaker).setVolume(percent) }
    }

    /** Room correction, on the speaker that holds it. It travels in NVS. */
    fun setRoomCorrection(id: String, enabled: Boolean) {
        val speaker = _state.value.speakers.firstOrNull { it.id == id } ?: return
        _state.update { current ->
            current.copy(speakers = current.speakers.map {
                if (it.id == id) it.copy(roomCorrection = enabled) else it
            })
        }
        scope.launch { client(speaker).setRoomCorrection(enabled) }
    }

    /**
     * The turntable: the phone is its Controller, not its source.
     *
     * The vinyl node is already a Producer, so the phone tells it where to
     * send rather than carrying the audio. From the app's point of view it
     * is the same operation as its own stream, which is why they share a
     * screen.
     */
    fun startVinyl(nodeId: String) {
        val node = _state.value.speakers.firstOrNull { it.id == nodeId } ?: return
        val destinations = currentDestinations().map { it.hostAddress ?: "" }.filter { it.isNotEmpty() }
        if (destinations.isEmpty()) {
            warn("Choose at least one speaker first.")
            return
        }
        scope.launch { client(node).startStream(destinations) }
    }

    fun stopVinyl(nodeId: String) {
        val node = _state.value.speakers.firstOrNull { it.id == nodeId } ?: return
        scope.launch { client(node).stopStream() }
    }

    fun probe() {
        discovery?.probe()
    }

    fun dismissWarning() = _state.update { it.copy(warning = null) }

    /* ---------- internals ---------- */

    private fun client(speaker: Speaker) = NodeClient(speaker.address, speaker.controlPort)

    private fun currentDestinations(): List<InetAddress> =
        _state.value.selected.mapNotNull {
            try {
                InetAddress.getByName(it.address)
            } catch (_: Exception) {
                null
            }
        }

    private fun warn(message: String) = _state.update { it.copy(warning = message) }

    /**
     * Rebuilds the speaker list, keeping what the person chose.
     *
     * A speaker that dropped off the network and came back must not lose
     * its tick — at a party that is the difference between "it reappeared"
     * and "it reappeared and went silent".
     */
    private fun refreshSpeakers() {
        val table = discovery?.peers ?: return
        val now = System.currentTimeMillis()
        val chosen = _state.value.speakers.filter { it.selected }.map { it.id }.toSet()
        val known = _state.value.speakers.associateBy { it.id }

        val speakers = table.online(now)
            .filter { it.announce.isConsumer || it.announce.isProducer }
            .map { peer ->
                val existing = known[peer.id]
                Speaker(
                    id = peer.id,
                    name = peer.name,
                    address = peer.address,
                    controlPort = peer.controlPort,
                    selected = peer.id in chosen,
                    volume = existing?.volume ?: -1,
                    roomCorrection = existing?.roomCorrection ?: false,
                    isProducerNode = peer.announce.isProducer,
                )
            }

        _state.update { it.copy(speakers = speakers) }
        sender?.setDestinations(currentDestinations())

        // What each one currently thinks its volume and correction are.
        scope.launch {
            for (speaker in speakers.filter { it.volume < 0 }) {
                val status = NodeClient(speaker.address, speaker.controlPort).status() ?: continue
                _state.update { current ->
                    current.copy(speakers = current.speakers.map {
                        if (it.id == speaker.id) {
                            it.copy(volume = status.volume, roomCorrection = status.eqEnabled)
                        } else {
                            it
                        }
                    })
                }
            }
        }
    }

    private fun pollCounters() {
        scope.launch {
            while (_state.value.streaming) {
                val rtp = sender
                if (rtp != null) {
                    _state.update {
                        it.copy(
                            packetsSent = rtp.packetsSent,
                            underruns = rtp.underruns,
                            resyncs = rtp.resyncs,
                        )
                    }
                }
                kotlinx.coroutines.delay(1_000)
            }
        }
    }

    /** The announce this phone sends, so it appears like any other device. */
    fun identity(name: String, id: String): Announce = Announce(
        oal = Discovery.SUITE,
        id = id,
        name = name,
        roles = listOf("producer"),
        hw = "android",
        fw = BuildInfo.VERSION,
        caps = emptyList(),
        ctrlPort = null,
    )
}

object BuildInfo {
    const val VERSION = "0.1.0"
}
